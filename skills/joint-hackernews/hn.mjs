#!/usr/bin/env node
// joint-hackernews: the helper behind the skill. `serve` is a daemon that stays attached to the joint session's browser
// (CDP, default http://127.0.0.1:9222), injects hn-inject.js into every news.ycombinator.com document (so the options
// survive navigation, including the user's own clicks), looks up what HN's CSP stops the page fetching (user karma),
// keeps the options and Claude's annotations, and reads the AI cost for the dock. The other commands talk to it over
// 127.0.0.1 with a token.
//
// READ-ONLY BY DESIGN: there is no command here that clicks, types, submits or votes, and `open` only goes to an
// allow-list of read-only HN pages. That is a recommendation and a good-faith guard, not a security boundary: the user
// owns the skill and the generic joint-browser skill is not touched.
//
// Usage: node hn.mjs <command>      (node hn.mjs help)
import { createRequire } from 'node:module';
import { spawn } from 'node:child_process';
import crypto from 'node:crypto';
import fs from 'node:fs';
import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const nowS = () => Math.floor(Date.now() / 1000);
const alg = async (q) => {                                       // HN's public Algolia search (read-only), for "worth joining"
  const r = await fetch(`https://hn.algolia.com/api/v1/${q}`, { signal: AbortSignal.timeout(10000) });
  if (!r.ok) throw new Error(`Algolia answered ${r.status}`);
  return (await r.json()).hits || [];
};

const HERE = path.dirname(fileURLToPath(import.meta.url));
const HOME = path.join(os.homedir(), '.joint-hackernews');
const CDP = process.env.JOINT_CDP || 'http://127.0.0.1:9222';
const PLAYWRIGHT_DIR = path.join(os.homedir(), '.joint-browser', 'node');
const F = { daemon: path.join(HOME, 'daemon.json'), options: path.join(HOME, 'options.json'), ann: path.join(HOME, 'annotations.json'),
  karma: path.join(HOME, 'karma.json'), costBase: path.join(HOME, 'cost-base.json'), bookmarks: path.join(HOME, 'bookmarks.json'), personas: path.join(HOME, 'personas.json'), requests: path.join(HOME, 'requests.json'), log: path.join(HOME, 'daemon.log') };

const USAGE = `Usage: node hn.mjs <command>
  start                      start the daemon in the background (log: ${F.log}) and print its status
  serve                      the daemon in the foreground
  stop | status | restart    stop it (returns when it has exited) / what it is doing / stop then start (after editing hn.mjs or hn-inject.js)
  get [path]                 the options (or one part)      set <path> <json|text>     change an option (the page updates at once)
  reset                      options back to the defaults
  open [url]                 go to an allowed READ-ONLY HN page in the newest tab (default https://news.ycombinator.com/)
  read                       the current HN page as compact JSON (stories, or the thread's comments) for Claude to read
  annotate <file.json>       merge Claude's annotations: {stories:{id:group}, picks:{id:reason}, comments:{id:{label,reason}}, summaries:{itemId:{...}}}
  requests [--clear]         what the user asked Claude for from the dock (classify, picks, summary, labels, article)
  cost                       re-read the AI cost now
  persona list | apply <id[,id]> | save <name> [about] | export <name> | import <file.json>   who you are: a preset of modules (data in modules.json and ~/.joint-hackernews/personas.json; yours can be shared)
  opps [--refresh]           the raw candidate threads behind "worth joining" (the dock scores them with your groups)
  module on|off <id>         switch a module (look, triage, bookmarks, opportunities) on or off; the persona becomes "custom"
  budget frugal|balanced|thorough   how much of a page \`read\` gives Claude (fewer comments, shorter, shallower = fewer tokens); frugal also uses the free classifier only
  bookmarks [--md] [--group G] [--since YYYY-MM-DD] [--tag t]   the local bookmarks (JSON by group/date, or Markdown)
  article <story id>         the story's linked page as readable text (capped by the token-use level) + reading minutes, for Claude to summarise
  group list|add|remove <name> [--keywords a,b] [--domains x.com,y.org] [--color #rrggbb]   topic groups (add merges into an existing one)
There is no command that posts, comments, replies, votes, flags or submits, by design.`;

// ------------------------------------------------------------------ options
const DEFAULTS = {
  dock: 'right',
  visual: { theme: 'dark', width: 'wide', fontScale: 1, hideFooter: false },
  list: {
    karma: { on: true, threshold: 10000, topN: 0, watch: [] },
    classifier: 'keywords+claude', velocity: true, seen: true, newMarks: true,
    mute: { domains: [], users: [], words: [] },          // local only: hides rows in the page, never HN's own "hide"
    alerts: { words: [] },                                 // stories matching these get a bell and the strongest colour
    groups: [
      { name: 'AI', color: '#8b5cf6', keywords: ['ai', 'llm', 'llms', 'gpt', 'chatgpt', 'openai', 'anthropic', 'claude', 'gemini', 'deepseek', 'mistral', 'llama', 'neural', 'machine learning', 'transformer', 'diffusion', 'agent', 'agents', 'copilot', 'embedding', 'inference', 'rag', 'altman', 'sam altman', 'nvidia', 'model', 'models', 'reasoning', 'weights', 'open weight', 'minimax', 'qwen', 'grok', 'a.i.', 'prompt', 'prompts', 'harness'], domains: ['openai.com', 'anthropic.com', 'huggingface.co'] },
      { name: 'Startups', color: '#f59e0b', keywords: ['startup', 'startups', 'founder', 'founders', 'funding', 'raises', 'raised', 'series a', 'series b', 'seed round', 'y combinator', 'yc', 'ipo', 'acquires', 'acquired', 'acquisition', 'saas', 'bootstrapped', 'venture', 'vc', 'valuation', 'revenue', 'layoffs', 'unicorn', 'acquire', 'acquiring', 'buyouts', 'ceo', 'workforce', 'market value', 'hiring', 'investors'], domains: ['techcrunch.com', 'ycombinator.com', 'crunchbase.com'] },
      { name: 'Security', color: '#10b981', keywords: ['security', 'breach', 'breached', 'hacked', 'exploit', 'cve', 'malware', 'ransomware', 'phishing', 'encryption', 'privacy', 'vulnerability', 'vulnerabilities', 'zero-day', 'leak', 'leaked', 'surveillance', 'backdoor', 'cybersecurity', 'hack', 'hacker', 'hackers', 'hijack', 'hijacked', 'compromise', 'compromised', 'compromises', 'pwned', 'rce', 'jailbreaking', 'jailbreak', 'vulnerable', 'stolen', 'steals', 'fuzz', 'fuzzing', 'incident', 'certificate', 'tls', 'ssl', 'embargo', 'grapheneos', 'root shell'], domains: ['krebsonsecurity.com', 'bleepingcomputer.com', 'schneier.com'] },
      { name: 'Politics', color: '#ef4444', keywords: ['trump', 'biden', 'election', 'congress', 'senate', 'tariff', 'tariffs', 'government', 'court', 'ban', 'war', 'sanctions', 'ukraine', 'russia', 'china', 'israel', 'gaza', 'president', 'law', 'lawsuit', 'regulation', 'antitrust', 'gdpr', 'eu', 'tax', 'police', 'lawmakers', 'copyright', 'epstein', 'ofcom', 'democratic', 'charged', 'charges', 'arrested', 'penalty', 'spy', 'fed', 'universal basic income'], domains: ['nytimes.com', 'theguardian.com', 'bbc.com', 'bbc.co.uk', 'washingtonpost.com', 'politico.com', 'reuters.com'] },
      { name: 'Technical', color: '#3b82f6', keywords: ['rust', 'python', 'linux', 'kernel', 'database', 'postgres', 'postgresql', 'sqlite', 'compiler', 'programming', 'javascript', 'typescript', 'golang', 'c++', 'git', 'open source', 'protocol', 'browser', 'css', 'wasm', 'gpu', 'cpu', 'api', 'sql', 'algorithm', 'kubernetes', 'docker', 'gcc', 'jit', 'openbsd', 'freebsd', 'emacs', 'vim', 'ruby', 'php', 'webgl', 'webassembly', 'asm', 'os', 'duckdb', 'tidb', 'jepsen', 'latency', 'software', 'coding', 'code', 'github', 'android', 'dns', 'unicode', 'gui', 'microcontroller', 'microcontrollers', 'memory'], domains: ['github.com', 'gitlab.com', 'lwn.net'] },
      { name: 'Science', color: '#06b6d4', keywords: ['physics', 'biology', 'nasa', 'space', 'astronomy', 'climate', 'quantum', 'study', 'cancer', 'chemistry', 'mathematics', 'math', 'brain', 'universe', 'dna', 'battery', 'fusion', 'telescope', 'species', 'genome', 'medicine', 'vaccine', 'physicists', 'antimatter', 'radiation', 'geology', 'solar', 'energy', 'electrification', 'tides', 'medical', 'diagnosis'], domains: ['nature.com', 'science.org', 'quantamagazine.org', 'nasa.gov', 'arxiv.org'] },
    ],
  },
  discussion: { indentGuides: true, highlightOp: true, collapseDepth: 0, authorCard: true, repeats: true, labels: 1 },
  claude: { budget: 'balanced', maxComments: 150, commentChars: 350, maxDepth: 3, cheapModel: false },
  bookmarks: { path: '' },
  opportunities: { maxComments: 10, maxAgeHours: 6, includeAsk: true },   // "worth joining": early threads with few comments
  persona: 'reader',
  modules: ['triage', 'look', 'bookmarks'],
  identity: { user: '' },                       // the logged-in HN name, read from the page header (read-only)
};
// How much of a page `read` hands to Claude (tokens cost money): the user picks a level at the start.
const BUDGETS = {
  frugal: { maxComments: 60, commentChars: 200, maxDepth: 2 },
  balanced: { maxComments: 150, commentChars: 350, maxDepth: 3 },
  thorough: { maxComments: 400, commentChars: 700, maxDepth: 0 },
};
const clone = (x) => JSON.parse(JSON.stringify(x));
const isObj = (x) => x && typeof x === 'object' && !Array.isArray(x);
function merge(base, over) {
  if (!isObj(base) || !isObj(over)) return over === undefined ? base : over;
  const out = { ...base };
  for (const k of Object.keys(over)) out[k] = k in base ? merge(base[k], over[k]) : over[k];
  return out;
}
function setPath(obj, p, value) {
  const keys = p.split('.');
  let d = DEFAULTS, o = obj;
  for (let i = 0; i < keys.length; i++) {
    if (!isObj(d) || !(keys[i] in d)) throw new Error(`unknown option "${p}" (see: node hn.mjs get)`);
    if (i === keys.length - 1) {
      if (typeof d[keys[i]] !== typeof value && !(Array.isArray(d[keys[i]]) && Array.isArray(value))) throw new Error(`option "${p}" takes a ${Array.isArray(d[keys[i]]) ? 'list' : typeof d[keys[i]]}`);
      o[keys[i]] = value;
    } else { d = d[keys[i]]; o = o[keys[i]]; }
  }
}
const readJson = (f, d) => { try { return JSON.parse(fs.readFileSync(f, 'utf8')); } catch { return d; } };
const writeJson = (f, v) => { fs.mkdirSync(HOME, { recursive: true }); fs.writeFileSync(f, JSON.stringify(v, null, 2)); };

// ------------------------------------------------------------------ the read-only door
const OPEN_PATHS = new Set(['/', '/news', '/newest', '/front', '/ask', '/show', '/jobs', '/from', '/active', '/best', '/shownew', '/noobstories', '/classic', '/item', '/user', '/submitted', '/threads']);
const OPEN_QUERY = new Set(['id', 'p', 'next', 'site', 'day', 'n']);
function checkOpen(raw) {
  let u;
  try { u = new URL(raw); } catch { return 'not a URL'; }
  if (u.protocol !== 'https:' || u.hostname !== 'news.ycombinator.com') return 'only https://news.ycombinator.com pages';
  if (!OPEN_PATHS.has(u.pathname)) return `${u.pathname} is not a read-only page (this skill never posts, replies, votes, flags, submits or logs in)`;
  for (const k of u.searchParams.keys()) if (!OPEN_QUERY.has(k)) return `query "${k}" is not allowed`;
  return null;
}

// ------------------------------------------------------------------ daemon
function loadPlaywright() {
  for (const base of [process.cwd(), PLAYWRIGHT_DIR]) {
    const require = createRequire(path.join(base, 'joint-hackernews.cjs'));
    for (const name of ['playwright', 'playwright-core']) { try { return require(require.resolve(name)); } catch { /* next */ } }
  }
  throw new Error('no Playwright: run  node <skills>/joint-browser/browser.mjs setup');
}

function findCostReader() {
  if (process.env.HN_COST_READER) return process.env.HN_COST_READER;
  if (process.env.JOINT_COST_READER) return process.env.JOINT_COST_READER;   // the launcher's: the script, or a tool binary that carries the reader
  for (let dir of [process.cwd(), HERE]) {
    for (let i = 0; i < 8; i++, dir = path.dirname(dir)) {
      for (const rel of ['csharp/Tools/ArshuCost/runJointCost.cs', 'ArshuCost/runJointCost.cs', 'runJointCost.cs']) {
        const f = path.join(dir, rel); if (fs.existsSync(f)) return f;
      }
    }
  }
  return null;
}

async function serve() {
  fs.mkdirSync(HOME, { recursive: true });
  const pw = loadPlaywright();
  const browser = await pw.chromium.connectOverCDP(CDP).catch((e) => { throw new Error(`no joint session browser on ${CDP} (${e.message.split('\n')[0]})`); });
  const ctx = browser.contexts()[0];
  if (!ctx) throw new Error('the browser has no default context');

  let options = merge(clone(DEFAULTS), readJson(F.options, {}));
  let annotations = merge({ stories: {}, comments: {}, summaries: {}, articles: {}, picks: {} }, readJson(F.ann, {}));
  let requests = readJson(F.requests, []);
  let cost = null;
  const karmaCache = readJson(F.karma, {});

  // bookmarks: a local JSON file, group -> date saved -> entries. The daemon owns the file; the page asks through the bridge.
  // They are NOT Hacker News "favorites" (those write to the user's HN account, which this skill never does).
  const bmPath = () => (options.bookmarks.path ? path.resolve(options.bookmarks.path) : F.bookmarks);
  let bookmarks = readJson(bmPath(), {});
  const saveBm = () => { const f = bmPath(), t = f + '.tmp'; fs.mkdirSync(path.dirname(f), { recursive: true }); fs.writeFileSync(t, JSON.stringify(bookmarks, null, 2)); fs.renameSync(t, f); };
  const today = () => { const d = new Date(), p = (n) => String(n).padStart(2, '0'); return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`; };
  const GROUP_OK = /^[\w &+/-]{1,24}$/;
  const bmFind = (type, id) => {
    for (const g of Object.keys(bookmarks)) for (const d of Object.keys(bookmarks[g])) {
      const i = bookmarks[g][d].findIndex((e) => e.type === type && e.id === id);
      if (i >= 0) return { g, d, i };
    }
    return null;
  };
  const bmDrop = (at) => {
    bookmarks[at.g][at.d].splice(at.i, 1);
    if (!bookmarks[at.g][at.d].length) delete bookmarks[at.g][at.d];
    if (!Object.keys(bookmarks[at.g]).length) delete bookmarks[at.g];
  };
  const bmCmd = (m) => {
    const str = (x, n) => (typeof x === 'string' ? x.slice(0, n) : '');
    const type = m.kind === 'comment' ? 'comment' : 'story', id = String(m.id || '');
    if (!/^\d+$/.test(id)) return { ok: false, error: 'bad id' };
    const at = bmFind(type, id);
    if (m.action === 'add') {
      if (at) return { ok: true };
      const e = m.entry || {}, url = str(e.url, 500);
      const group = GROUP_OK.test(e.group || '') ? e.group : 'Others';
      const entry = { id, type, title: str(e.title, 200), url: /^https?:\/\//.test(url) ? url : `https://news.ycombinator.com/item?id=${id}`,
        domain: str(e.domain, 100), user: str(e.user, 20), points: Number(e.points) || 0, comments: Number(e.comments) || 0,
        postedAt: str(e.postedAt, 30), savedAt: new Date().toISOString(), note: '', tags: [] };
      ((bookmarks[group] ||= {})[today()] ||= []).push(entry);
    } else if (!at) return { ok: false, error: 'not bookmarked' };
    else if (m.action === 'remove') bmDrop(at);
    else if (m.action === 'note') bookmarks[at.g][at.d][at.i].note = str(m.note, 300);
    else if (m.action === 'tags') bookmarks[at.g][at.d][at.i].tags = String(m.tags || '').split(',').map((x) => x.trim().toLowerCase().replace(/^#/, '')).filter((x) => /^[\w-]{1,20}$/.test(x)).slice(0, 8);
    else if (m.action === 'move') {
      if (!GROUP_OK.test(m.group || '')) return { ok: false, error: 'bad group' };
      const entry = bookmarks[at.g][at.d][at.i];
      bmDrop(at);
      ((bookmarks[m.group] ||= {})[at.d] ||= []).push(entry);
    } else return { ok: false, error: 'unknown action' };
    saveBm(); push();
    return { ok: true };
  };
  const bmFilter = (m) => {
    const out = {};
    for (const g of Object.keys(bookmarks)) {
      if (m.group && g.toLowerCase() !== String(m.group).toLowerCase()) continue;
      for (const d of Object.keys(bookmarks[g])) {
        if (m.since && d < m.since) continue;
        const items = bookmarks[g][d].filter((x) => !m.tag || (x.tags || []).includes(String(m.tag).toLowerCase()));
        if (items.length) (out[g] ||= {})[d] = items;
      }
    }
    return out;
  };
  const bmMarkdown = (only, since, tag) => {
    const out = ['# Hacker News bookmarks', ''];
    for (const g of Object.keys(bookmarks)) {
      if (only && g.toLowerCase() !== String(only).toLowerCase()) continue;
      const days = Object.keys(bookmarks[g]).filter((d) => !since || d >= since).sort().reverse();
      if (!days.length) continue;
      out.push(`## ${g}`, '');
      for (const d of days) {
        const items = bookmarks[g][d].filter((x) => !tag || (x.tags || []).includes(String(tag).toLowerCase()));
        if (!items.length) continue;
        out.push(`### ${d}`, '');
        for (const e of items) out.push(`- [${e.title.replace(/[[\]]/g, '')}](${e.url})${e.domain ? ' (' + e.domain + ')' : ''} · ${e.user} · ${e.points} pts · [discussion](https://news.ycombinator.com/item?id=${e.id})${e.note ? ' — ' + e.note : ''}${(e.tags || []).length ? ' ' + e.tags.map((t) => '#' + t).join(' ') : ''}`);
        out.push('');
      }
    }
    return out.join('\n');
  };
  // an article for Claude to summarise, on request: the daemon fetches the story's link (HN's CSP stops the page), keeps
  // the readable text only, and caps it by the token-use level. Only a story's own link, never a private address.
  const ARTICLE_CHARS = { frugal: 3000, balanced: 6000, thorough: 12000 };
  const PRIVATE_HOST = /^(localhost|.*\.local|.*\.internal|127\.|10\.|192\.168\.|169\.254\.|172\.(1[6-9]|2\d|3[01])\.|0\.|\[?::1\]?$)/i;
  const htmlToText = (html) => {
    let h = html.replace(/<(script|style|noscript|svg|nav|header|footer|aside|form)\b[\s\S]*?<\/\1>/gi, ' ').replace(/<!--[\s\S]*?-->/g, ' ');
    const main = h.match(/<(article|main)\b[\s\S]*<\/\1>/i);
    if (main && main[0].length > 800) h = main[0];
    return h.replace(/<\/(p|div|h\d|li|tr|blockquote)>|<br\s*\/?>/gi, '\n').replace(/<[^>]+>/g, ' ')
      .replace(/&nbsp;/g, ' ').replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&#39;|&rsquo;|&lsquo;/g, "'")
      .replace(/[ \t\r\f\v]+/g, ' ').replace(/ ?\n ?/g, '\n').replace(/\n{3,}/g, '\n\n').trim();
  };
  const fetchArticle = async (id) => {
    if (!/^\d+$/.test(String(id))) return { ok: false, error: 'article <story id>' };
    const item = await (await fetch(`https://hacker-news.firebaseio.com/v0/item/${id}.json`, { signal: AbortSignal.timeout(8000) })).json();
    if (!item) return { ok: false, error: 'no such item' };
    const cap = ARTICLE_CHARS[options.claude.budget] || 6000;
    let text, host = '';
    if (item.url) {
      const u = new URL(item.url);
      host = u.hostname;
      if (!/^https?:$/.test(u.protocol) || PRIVATE_HOST.test(u.hostname)) return { ok: false, error: 'only public http(s) links are fetched' };
      const res = await fetch(u, { signal: AbortSignal.timeout(12000), redirect: 'follow', headers: { 'user-agent': 'joint-hackernews (a person reading; read-only)', accept: 'text/html,text/plain' } });
      const type = res.headers.get('content-type') || '';
      if (!res.ok) return { ok: false, error: `the site answered ${res.status}` };
      if (!/text\/(html|plain)/i.test(type)) return { ok: false, error: `not a web page (${type || 'unknown type'}): a PDF or video cannot be summarised here` };
      const raw = (await res.text()).slice(0, 1500000);
      text = /html/i.test(type) ? htmlToText(raw) : raw;
    } else text = htmlToText(String(item.text || ''));
    const words = (text.match(/\S+/g) || []).length;
    return { ok: true, id: String(id), title: item.title || '', url: item.url || '', host, words, minutes: Math.max(1, Math.round(words / 230)), truncated: text.length > cap, includedChars: Math.min(text.length, cap), text: text.slice(0, cap) };
  };
  const applyBudget = (name) => {
    const b = BUDGETS[name];
    if (!b) return { ok: false, error: 'budget: frugal | balanced | thorough' };
    setBudget(name);
    writeJson(F.options, options); push();
    return { ok: true, claude: options.claude, classifier: options.list.classifier };
  };
  const token = crypto.randomBytes(16).toString('hex');
  const stateFor = () => ({ options, annotations, cost, requests: requests.length, bookmarks, catalog: catalogView() });
  const hnPages = () => ctx.pages().filter((p) => { try { return new URL(p.url()).hostname === 'news.ycombinator.com'; } catch { return false; } });
  const push = async () => { const st = stateFor(); await Promise.all(hnPages().map((p) => p.evaluate((s) => window.__hnApply && window.__hnApply(s), st).catch(() => {}))); };

  // karma: HN's CSP stops the page fetching, so the daemon does it (Firebase API), 6 h cache
  const fetchKarma = async (users) => {
    const out = {}, todo = [];
    for (const u of users) {
      if (!/^[A-Za-z0-9_-]{2,15}$/.test(u)) { out[u] = null; continue; }
      const c = karmaCache[u];
      if (c && Date.now() - c.at < 6 * 36e5) out[u] = c.v; else todo.push(u);
    }
    let next = 0;
    await Promise.all(Array.from({ length: Math.min(8, todo.length) }, async () => {
      while (next < todo.length) {
        const u = todo[next++];
        try {
          const r = await fetch(`https://hacker-news.firebaseio.com/v0/user/${u}.json`, { signal: AbortSignal.timeout(8000) });
          const j = r.ok ? await r.json() : null;
          out[u] = j ? { karma: j.karma, created: j.created } : null;
          karmaCache[u] = { at: Date.now(), v: out[u] };
        } catch (e) { out[u] = null; console.log(`[hn] karma lookup for ${u} failed: ${e.message}`); }   // shown as "unknown" in the page; the cause is in the log
      }
    }));
    if (todo.length) writeJson(F.karma, karmaCache);
    return out;
  };

  // cost: the repository's cost reader (runJointCost.cs); an error is shown as the error, never as a number
  // the baseline lives as long as this browser instance, so restarting the daemon (fine-tuning) does not reset "$0.00"
  const browserKey = await fetch(new URL('/json/version', CDP)).then((r) => r.json()).then((j) => j.webSocketDebuggerUrl).catch(() => 'unknown');
  const savedBase = readJson(F.costBase, null);
  let costBase = savedBase && savedBase.key === browserKey ? savedBase.base : null, costBusy = false;
  const readCost = () => new Promise((resolve) => {
    if (costBusy) return resolve();
    costBusy = true;
    const reader = findCostReader();
    const done = (c) => { costBusy = false; cost = c; push().finally(resolve); };
    if (!reader) return done({ error: 'runJointCost.cs not found (set HN_COST_READER)' });
    // this skill's own harness (the folder it lives in), so another harness's old sessions cannot break the number
    const harness = HERE.includes(`${path.sep}.agents${path.sep}`) ? 'codex' : HERE.includes(`${path.sep}.pi${path.sep}`) ? 'pi' : 'claude';
    const readerArgs = ['--folder', process.cwd(), '--harness', harness, '--json', '--no-snapshot'];
    const script = /\.cs$/i.test(reader);   // the script runs with dotnet; a tool binary carries the reader behind --cost
    const p = spawn(script ? 'dotnet' : reader, script ? ['run', reader, '--', ...readerArgs] : ['--cost', ...readerArgs], { windowsHide: true });
    let out = '', err = '';
    const timer = setTimeout(() => p.kill(), 120000);
    p.stdout.on('data', (d) => (out += d)); p.stderr.on('data', (d) => (err += d));
    p.on('error', (e) => { clearTimeout(timer); done({ error: 'cannot start dotnet: ' + e.message }); });
    p.on('close', (code) => {
      clearTimeout(timer);
      try {
        const j = JSON.parse(out);
        const usd = j.total.costUsd;
        if (costBase === null) { costBase = usd; writeJson(F.costBase, { key: browserKey, base: usd }); }
        const def = (j.rows || []).find((r) => String(r.costSource || '').includes('+default:'));
        const unpriced = (j.rows || []).some((r) => r.unpriced);
        done({ usd, base: costBase, session: j.session ? j.session.costUsd : 0, group: j.group.name, groupUsd: j.group.costUsd, defaulted: def ? String(def.costSource).split('+default:')[1] : unpriced ? 'a model with no price (its cost is not counted)' : null });
      } catch {
        done({ error: ((out + err).trim().split('\n')[0] || `cost reader exited ${code}`).slice(0, 300) });
      }
    });
  });

  // ---- personas and modules: data, not code (modules.json beside this file, the user's own in personas.json). A module is a
  // bundle of option values; a persona is a list of modules, a token level and optional option overrides. People tune
  // and share them (module toggles, persona save / export / import) because we do not know every kind of HN user yet.
  const PERSONA_ID = /^[\w-]{1,24}$/;
  const catalog = () => {
    const c = readJson(path.join(HERE, 'modules.json'), { modules: {}, personas: {} });
    return { modules: c.modules, personas: { ...c.personas, ...readJson(F.personas, {}) }, shipped: Object.keys(c.personas) };
  };
  const catalogView = () => {
    const c = catalog();
    return { modules: Object.fromEntries(Object.entries(c.modules).map(([k, m]) => [k, { title: m.title, about: m.about, status: m.status }])),
      personas: Object.fromEntries(Object.entries(c.personas).map(([k, p]) => [k, { title: p.title, about: p.about }])) };
  };
  const leaves = (tree, pre = '') => Object.entries(tree).flatMap(([k, v]) => (isObj(v) ? leaves(v, pre + k + '.') : [[pre + k, v]]));
  const applyModules = (ids) => {
    const c = catalog(), known = ids.filter((id) => c.modules[id]?.status === 'ready');
    for (const [id, m] of Object.entries(c.modules)) {
      if (m.status !== 'ready') continue;
      for (const [p, v] of Object.entries(known.includes(id) ? m.set || {} : m.off || {})) setPath(options, p, v);
    }
    options.modules = known;
  };
  const setBudget = (name) => {
    options.claude = { ...options.claude, budget: name, ...BUDGETS[name] };
    if (options.modules.includes('triage')) options.list.classifier = name === 'frugal' ? 'keywords' : 'keywords+claude';   // frugal: the free classifier only
  };
  const applyPersonas = (names) => {
    const c = catalog(), list = names.map((n) => String(n).trim()).filter(Boolean), missing = list.filter((n) => !c.personas[n]);
    if (!list.length || missing.length) return { ok: false, error: `no persona "${missing[0] || ''}"; there are: ${Object.keys(c.personas).join(', ')}` };
    applyModules([...new Set(list.flatMap((n) => c.personas[n].modules || []))]);
    for (const n of list) if (c.personas[n].options) for (const [k, v] of leaves(c.personas[n].options)) setPath(options, k, v);
    const budget = BUDGETS[c.personas[list[0]].budget] ? c.personas[list[0]].budget : options.claude.budget;
    setBudget(budget);
    options.persona = list.join('+');
    writeJson(F.options, options); push();
    return { ok: true, persona: options.persona, modules: options.modules, budget };
  };
  const toggleModule = (id, on) => {
    const c = catalog();
    if (c.modules[id]?.status !== 'ready') return { ok: false, error: `no ready module "${id}"; ready: ${Object.entries(c.modules).filter(([, m]) => m.status === 'ready').map(([k]) => k).join(', ')}` };
    const cur = new Set(options.modules);
    if (on) cur.add(id); else cur.delete(id);
    applyModules([...cur]);
    options.persona = 'custom';
    writeJson(F.options, options); push();
    return { ok: true, modules: options.modules };
  };
  const personaSave = (name, about) => {
    if (!PERSONA_ID.test(name || '')) return { ok: false, error: 'a persona name is 1-24 letters, digits, - or _' };
    if (catalog().shipped.includes(name)) return { ok: false, error: `"${name}" is a shipped persona: pick another name` };
    const mine = readJson(F.personas, {}), L = options.list;
    mine[name] = { title: name, about: String(about || 'my own mix').slice(0, 200), modules: options.modules, budget: options.claude.budget,
      options: { dock: options.dock, visual: options.visual, discussion: options.discussion, list: { karma: L.karma, classifier: L.classifier, velocity: L.velocity, seen: L.seen, groups: L.groups } } };
    writeJson(F.personas, mine); push();
    return { ok: true, saved: name, file: F.personas };
  };
  const personaImport = (data) => {
    if (!isObj(data) || !PERSONA_ID.test(String(data.id || ''))) return { ok: false, error: 'a persona needs an "id" of 1-24 letters, digits, - or _' };
    const c = catalog();
    if (c.shipped.includes(data.id)) return { ok: false, error: `"${data.id}" is a shipped persona: use another id` };
    const mods = Array.isArray(data.modules) ? data.modules.map(String) : [], bad = mods.find((m) => c.modules[m]?.status !== 'ready');
    if (bad) return { ok: false, error: `unknown or not ready module "${bad}"` };
    if (data.budget !== undefined && !BUDGETS[data.budget]) return { ok: false, error: 'budget: frugal | balanced | thorough' };
    if (data.options !== undefined) {                      // pure data: known option paths and types only, never identity or paths
      if (!isObj(data.options)) return { ok: false, error: 'options must be an object' };
      const probe = clone(options);
      for (const [k, v] of leaves(data.options)) {
        if (/^(identity|bookmarks|claude|modules|persona)(\.|$)/.test(k)) return { ok: false, error: `option "${k}" cannot be set by a persona` };
        try { setPath(probe, k, v); } catch (e) { return { ok: false, error: e.message }; }
      }
    }
    const str = (x, n) => String(x || '').slice(0, n), mine = readJson(F.personas, {});
    mine[data.id] = { title: str(data.title || data.id, 40), about: str(data.about, 200), modules: mods, ...(data.budget ? { budget: data.budget } : {}), ...(data.options ? { options: data.options } : {}) };
    writeJson(F.personas, mine); push();
    return { ok: true, imported: data.id };
  };

  // ---- opportunities ("worth joining"): candidate threads for the dock, read-only from HN's public search, cached 5 minutes.
  // The page classifies them with the user's own groups and scores them (early, few comments, Ask HN): free, no Claude tokens.
  let oppCache = { at: 0, items: [] };
  const fetchOpps = async (force) => {
    if (!force && oppCache.items.length && Date.now() - oppCache.at < 5 * 60000) return oppCache;
    const now = nowS(), seen = new Map();
    const queries = [`search_by_date?tags=ask_hn&numericFilters=created_at_i>${now - 12 * 3600}&hitsPerPage=50`,
      `search_by_date?tags=story&numericFilters=created_at_i>${now - 6 * 3600},points>=3&hitsPerPage=50`, 'search?tags=front_page&hitsPerPage=40'];
    for (const q of queries) {
      for (const h of await alg(q)) {
        if (!h.objectID || !h.title || seen.has(h.objectID)) continue;
        let domain = ''; try { domain = new URL(h.url).hostname.replace(/^www\./, ''); } catch { /* an Ask HN has no link */ }
        const tags = h._tags || [];
        seen.set(h.objectID, { id: String(h.objectID), title: String(h.title).slice(0, 160), domain, author: String(h.author || ''), points: h.points || 0, comments: h.num_comments || 0, at: h.created_at_i || now, ask: tags.includes('ask_hn'), show: tags.includes('show_hn') });
      }
    }
    oppCache = { at: Date.now(), items: [...seen.values()] };
    return oppCache;
  };

  // groups are dynamic: the user (dock) or Claude (after the user agreed) adds one while browsing
  const PALETTE = ['#8b5cf6', '#f59e0b', '#10b981', '#ef4444', '#06b6d4', '#3b82f6', '#ec4899', '#84cc16', '#f97316', '#14b8a6'];
  const words = (x) => (Array.isArray(x) ? x : String(x || '').split(',')).map((w) => String(w).trim().toLowerCase()).filter(Boolean).slice(0, 60);
  const groupCmd = (m) => {
    const groups = options.list.groups;
    if (m.action === 'list') return { ok: true, groups };
    const name = String(m.name || '').trim();
    if (!/^[\w &+/-]{1,24}$/.test(name) || name.toLowerCase() === 'others') return { ok: false, error: 'a group name is 1-24 letters, digits, spaces or & + / - (and is not "Others")' };
    const at = groups.findIndex((g) => g.name.toLowerCase() === name.toLowerCase());
    if (m.action === 'remove') { if (at < 0) return { ok: false, error: 'no such group' }; groups.splice(at, 1); }
    else if (m.action === 'add') {
      const kw = words(m.keywords), dom = words(m.domains);
      if (at >= 0) { groups[at].keywords = [...new Set([...groups[at].keywords, ...kw])]; groups[at].domains = [...new Set([...groups[at].domains, ...dom])]; }
      else groups.push({ name, color: /^#[0-9a-f]{6}$/i.test(m.color || '') ? m.color : PALETTE.find((c) => !groups.some((g) => g.color === c)) || '#a3a3a3', keywords: kw, domains: dom });
    } else return { ok: false, error: 'group add|remove|list' };
    writeJson(F.options, options); push();
    return { ok: true, groups: options.list.groups.map((g) => g.name) };
  };

  // the page asks for things through this binding
  await ctx.exposeBinding('__hnBridge', async (source, m) => {
    if (m.type === 'hello') return stateFor();
    if (m.type === 'karma') return fetchKarma(Array.isArray(m.users) ? m.users.slice(0, 60) : []);
    if (m.type === 'persona') { const r = applyPersonas(String(m.name).split(',')); if (!r.ok) throw new Error(r.error); return options; }
    if (m.type === 'module') { const r = toggleModule(String(m.id), !!m.on); if (!r.ok) throw new Error(r.error); return options; }
    if (m.type === 'opps') { const c = await fetchOpps(!!m.force); return { at: c.at, items: c.items }; }
    if (m.type === 'setOption') { if (m.path === 'identity.user' && !/^[A-Za-z0-9_-]{2,15}$/.test(String(m.value))) throw new Error('bad name'); setPath(options, String(m.path), m.value); writeJson(F.options, options); push(); return options; }
    if (m.type === 'bookmark') { const r = bmCmd(m); if (!r.ok) throw new Error(r.error); return bookmarks; }
    if (m.type === 'bookmarkMd') return bmMarkdown(null, null);
    if (m.type === 'budget') { const r = applyBudget(String(m.name)); if (!r.ok) throw new Error(r.error); return options; }
    if (m.type === 'addGroup') { const r = groupCmd({ action: 'add', name: m.name, keywords: m.keywords }); if (!r.ok) throw new Error(r.error); return options; }
    if (m.type === 'mute') {
      const k = { domain: 'domains', user: 'users', word: 'words' }[m.kind], v = String(m.value || '').trim().toLowerCase();
      if (!k || !/^[\w.@+ -]{1,60}$/.test(v)) throw new Error('bad mute');
      const a = options.list.mute[k], i = a.indexOf(v);
      if (m.on === false) { if (i >= 0) a.splice(i, 1); } else if (i < 0) a.push(v);
      writeJson(F.options, options); push();
      return options;
    }
    if (m.type === 'request') {
      if (!['classify', 'summary', 'labels', 'article', 'picks'].includes(m.kind)) throw new Error('unknown request');
      if (m.kind === 'article' && !/^\d+$/.test(String(m.id))) throw new Error('bad story id');
      requests.push({ kind: m.kind, id: m.kind === 'article' ? String(m.id) : undefined, url: String(m.url || ''), at: new Date().toISOString() });
      writeJson(F.requests, requests);
      return { pending: requests.length };
    }
    throw new Error('unknown bridge message');
  });
  await ctx.addInitScript({ content: fs.readFileSync(path.join(HERE, 'hn-inject.js'), 'utf8') });
  await Promise.all(hnPages().map((p) => p.reload().catch(() => {})));   // existing HN tabs get the script

  const newestHn = () => { const l = hnPages(); return l[l.length - 1]; };
  const handlers = {
    status: async () => ({ ok: true, pid: process.pid, cdp: CDP, pages: hnPages().map((p) => p.url()), pendingRequests: requests.length, cost, optionsFile: F.options }),
    get: async (m) => ({ ok: true, value: m.path ? m.path.split('.').reduce((a, k) => (a == null ? a : a[k]), options) : options }),
    set: async (m) => { setPath(options, m.path, m.value); writeJson(F.options, options); await push(); return { ok: true, path: m.path, value: m.value }; },
    reset: async () => { options = clone(DEFAULTS); writeJson(F.options, options); await push(); return { ok: true }; },
    open: async (m) => {
      const url = m.url || 'https://news.ycombinator.com/';
      const why = checkOpen(url);
      if (why) return { ok: false, refused: why };
      const page = newestHn() || ctx.pages()[ctx.pages().length - 1] || await ctx.newPage();
      await page.goto(url, { waitUntil: 'domcontentloaded' });
      return { ok: true, url: page.url(), title: await page.title() };
    },
    read: async () => {
      const page = newestHn();
      if (!page) return { ok: false, error: 'no Hacker News tab is open: node hn.mjs open' };
      const data = await page.evaluate((lim) => (window.__hnRead ? window.__hnRead(lim) : null), options.claude);
      return data ? { ok: true, ...data } : { ok: false, error: 'the page script is not loaded here (reload the tab)' };
    },
    annotate: async (m) => {
      const a = m.data, str = (x, n) => (typeof x === 'string' ? x.slice(0, n) : '');
      const LABELS = new Set(['attack', 'snark', 'lowvalue', 'repeat', 'good']);
      let n = 0;
      for (const [id, g] of Object.entries(a.stories || {})) if (/^\d+$/.test(id) && typeof g === 'string') { annotations.stories[id] = str(g, 40); n++; }
      for (const [id, c] of Object.entries(a.comments || {})) if (/^\d+$/.test(id) && c && LABELS.has(c.label)) { annotations.comments[id] = { label: c.label, reason: str(c.reason, 160) }; n++; }
      if (a.picks && isObj(a.picks)) {                    // Claude's picks REPLACE the previous ones: a pick is for this moment's front page
        const next = {};
        for (const [id, r] of Object.entries(a.picks).slice(0, 12)) if (/^\d+$/.test(id)) { next[id] = { reason: str(typeof r === 'string' ? r : r && r.reason, 140), ts: new Date().toISOString().slice(0, 16).replace('T', ' ') }; n++; }
        annotations.picks = next;
      }
      for (const [id, s] of Object.entries(a.articles || {})) if (/^\d+$/.test(id) && s) { annotations.articles[id] = { tldr: str(s.tldr, 500), minutes: Math.min(999, Number(s.minutes) || 0) }; n++; }
      for (const [id, s] of Object.entries(a.summaries || {})) {
        if (!/^\d+$/.test(id) || !s) continue;
        annotations.summaries[id] = {
          overview: str(s.overview, 600), disagreement: str(s.disagreement, 600), covered: Number(s.covered) || 0, total: Number(s.total) || 0, ts: new Date().toISOString().slice(0, 16).replace('T', ' '),
          positions: (Array.isArray(s.positions) ? s.positions : []).slice(0, 8).map((p) => ({ title: str(p.title, 80), text: str(p.text, 400), ids: (Array.isArray(p.ids) ? p.ids : []).filter((i) => /^\d+$/.test(String(i))).slice(0, 6).map(String) })),
          best: (Array.isArray(s.best) ? s.best : []).filter((i) => /^\d+$/.test(String(i))).slice(0, 5).map(String),
        };
        n++;
      }
      writeJson(F.ann, annotations); requests = []; writeJson(F.requests, requests);
      await push();
      return { ok: true, merged: n };
    },
    requests: async (m) => { const r = requests; if (m.clear) { requests = []; writeJson(F.requests, requests); await push(); } return { ok: true, requests: r }; },
    group: async (m) => groupCmd(m),
    budget: async (m) => applyBudget(m.name),
    persona: async (m) => {
      if (m.action === 'apply') return applyPersonas(String(m.name || '').split(','));
      if (m.action === 'save') return personaSave(m.name, m.about);
      if (m.action === 'import') return personaImport(m.data);
      if (m.action === 'export') { const p = catalog().personas[m.name]; return p ? { ok: true, persona: { id: m.name, ...p } } : { ok: false, error: `no persona "${m.name}"` }; }
      const c = catalog();
      return { ok: true, current: options.persona, modules: options.modules, personas: c.personas, moduleList: Object.fromEntries(Object.entries(c.modules).map(([k, v]) => [k, `${v.status}: ${v.about}`])) };
    },
    module: async (m) => toggleModule(String(m.id), m.on !== false),
    opps: async (m) => { try { const c = await fetchOpps(!!m.force); return { ok: true, count: c.items.length, items: c.items }; } catch (e) { return { ok: false, error: `could not read HN's search: ${e.message}` }; } },
    bookmarks: async (m) => (m.md ? { ok: true, markdown: bmMarkdown(m.group, m.since, m.tag) } : { ok: true, bookmarks: m.group || m.since || m.tag ? bmFilter(m) : bookmarks }),
    article: async (m) => { try { return await fetchArticle(m.id); } catch (e) { return { ok: false, error: `could not fetch it: ${e.message}` }; } },
    cost: async () => { await readCost(); return { ok: true, cost }; },
    stop: async () => { setTimeout(shutdown, 100); return { ok: true }; },
  };

  const server = http.createServer((req, res) => {
    let body = '';
    req.on('data', (d) => { body += d; if (body.length > 2e6) req.destroy(); });
    req.on('end', async () => {
      const send = (code, v) => { res.writeHead(code, { 'content-type': 'application/json' }); res.end(JSON.stringify(v)); };
      if (req.headers['x-hn-token'] !== token) return send(403, { ok: false, error: 'bad token' });
      try {
        const m = JSON.parse(body || '{}');
        if (!handlers[m.cmd]) return send(400, { ok: false, error: 'unknown command' });
        send(200, await handlers[m.cmd](m));
      } catch (e) { send(500, { ok: false, error: e.message }); }
    });
  });
  await new Promise((r) => server.listen(0, '127.0.0.1', r));          // localhost only, never *
  writeJson(F.daemon, { port: server.address().port, pid: process.pid, token });
  console.log(`[hn] serving on 127.0.0.1:${server.address().port}, browser ${CDP}, options ${F.options}`);

  function shutdown() { try { fs.unlinkSync(F.daemon); } catch { /* gone */ } process.exit(0); }
  browser.on('disconnected', () => { console.log('[hn] browser closed'); shutdown(); });
  process.on('SIGINT', shutdown); process.on('SIGTERM', shutdown);
  readCost(); setInterval(readCost, 60000);
  await push();
}

// ------------------------------------------------------------------ client
function daemonInfo() {
  const d = readJson(F.daemon, null);
  if (!d) return null;
  try { process.kill(d.pid, 0); return d; } catch { return null; }
}
function call(m) {
  const d = daemonInfo();
  if (!d) return Promise.resolve({ ok: false, error: 'the daemon is not running: node hn.mjs start' });
  return new Promise((resolve) => {
    const req = http.request({ host: '127.0.0.1', port: d.port, method: 'POST', headers: { 'x-hn-token': d.token, 'content-type': 'application/json' } }, (res) => {
      let b = ''; res.on('data', (x) => (b += x)); res.on('end', () => { try { resolve(JSON.parse(b)); } catch { resolve({ ok: false, error: b }); } });
    });
    req.on('error', (e) => resolve({ ok: false, error: e.message }));
    req.end(JSON.stringify(m));
  });
}
const parseValue = (s) => { try { return JSON.parse(s); } catch { return s; } };
const print = (r) => { console.log(JSON.stringify(r, null, 2)); process.exitCode = r.ok === false ? 1 : 0; };

async function start() {
  if (daemonInfo()) return print(await call({ cmd: 'status' }));
  fs.mkdirSync(HOME, { recursive: true });
  const log = fs.openSync(F.log, 'w');
  spawn(process.execPath, [fileURLToPath(import.meta.url), 'serve'], { detached: true, stdio: ['ignore', log, log], windowsHide: true, cwd: process.cwd() }).unref();
  for (let i = 0; i < 60; i++) {
    await new Promise((r) => setTimeout(r, 500));
    if (daemonInfo()) return print(await call({ cmd: 'status' }));
  }
  print({ ok: false, error: 'the daemon did not start', log: fs.readFileSync(F.log, 'utf8').slice(-800) });
}

// stop returns only once the daemon process is gone, so a `start` right after it can never meet the dying one
async function stop() {
  const d = daemonInfo(), r = await call({ cmd: 'stop' });
  if (r.ok === false || !d) return r;
  for (let i = 0; i < 50; i++) { try { process.kill(d.pid, 0); } catch { return r; } await new Promise((x) => setTimeout(x, 100)); }
  return { ok: false, error: `the daemon (pid ${d.pid}) did not exit within 5 s` };
}

const [cmd, ...rest] = process.argv.slice(2);
try {
  if (cmd === 'serve') await serve();
  else if (cmd === 'start') await start();
  else if (cmd === 'stop') print(await stop());
  else if (cmd === 'restart') { const r = await stop(); if (r.ok === false && !/not running/.test(r.error)) print(r); else await start(); }
  else if (cmd === 'status') print(await call({ cmd: 'status' }));
  else if (cmd === 'get') print(await call({ cmd: 'get', path: rest[0] }));
  else if (cmd === 'set') { if (rest.length < 2) throw new Error('set <path> <value>'); print(await call({ cmd: 'set', path: rest[0], value: parseValue(rest.slice(1).join(' ')) })); }
  else if (cmd === 'reset') print(await call({ cmd: 'reset' }));
  else if (cmd === 'open') print(await call({ cmd: 'open', url: rest[0] }));
  else if (cmd === 'read') { const r = await call({ cmd: 'read' }); console.log(JSON.stringify(r)); process.exitCode = r.ok === false ? 1 : 0; }   // compact: Claude pays for every token
  else if (cmd === 'annotate') { if (!rest[0]) throw new Error('annotate <file.json>'); print(await call({ cmd: 'annotate', data: JSON.parse(fs.readFileSync(rest[0], 'utf8')) })); }
  else if (cmd === 'requests') print(await call({ cmd: 'requests', clear: rest.includes('--clear') }));
  else if (cmd === 'cost') print(await call({ cmd: 'cost' }));
  else if (cmd === 'budget') print(await call({ cmd: 'budget', name: rest[0] }));
  else if (cmd === 'persona') {
    const [action = 'list', a, ...more] = rest;
    if (action === 'import') { if (!a) throw new Error('persona import <file.json>'); print(await call({ cmd: 'persona', action, data: JSON.parse(fs.readFileSync(a, 'utf8')) })); }
    else if (action === 'export') { const r = await call({ cmd: 'persona', action, name: a }); if (r.ok) console.log(JSON.stringify(r.persona, null, 2)); else print(r); }
    else print(await call({ cmd: 'persona', action, name: a, about: more.join(' ') }));
  }
  else if (cmd === 'opps') { const r = await call({ cmd: 'opps', force: rest.includes('--refresh') }); console.log(JSON.stringify(r)); process.exitCode = r.ok === false ? 1 : 0; }
  else if (cmd === 'module') print(await call({ cmd: 'module', on: rest[0] !== 'off', id: rest[1] }));
  else if (cmd === 'article') { const r = await call({ cmd: 'article', id: rest[0] }); console.log(JSON.stringify(r)); process.exitCode = r.ok === false ? 1 : 0; }
  else if (cmd === 'bookmarks') {
    const o = {};
    for (let i = 0; i < rest.length; i++) { if (rest[i] === '--md') o.md = true; else if (rest[i] === '--group') o.group = rest[++i]; else if (rest[i] === '--since') o.since = rest[++i]; else if (rest[i] === '--tag') o.tag = rest[++i]; }
    const r = await call({ cmd: 'bookmarks', ...o });
    if (r.markdown !== undefined) console.log(r.markdown); else print(r);
  }
  else if (cmd === 'group') {
    const [action, name, ...more] = rest, o = {};
    for (let i = 0; i < more.length; i += 2) o[more[i].replace(/^--/, '')] = more[i + 1];
    print(await call({ cmd: 'group', action, name, ...o }));
  }
  else { console.log(USAGE); process.exitCode = cmd && cmd !== 'help' ? 1 : 0; }
} catch (e) {
  console.error(JSON.stringify({ ok: false, error: e.message }));
  process.exitCode = 1;
}
