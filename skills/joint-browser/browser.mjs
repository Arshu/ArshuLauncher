#!/usr/bin/env node
// joint-browser: one Playwright action per call on the joint session's shared browser (CDP, default
// http://127.0.0.1:9222). It attaches to the open window and never launches or closes a browser. Playwright comes from
// the project (node_modules/playwright or playwright-core under the current folder), else from ~/.joint-browser/node,
// where `node browser.mjs setup` installs playwright-core with npm. Every action prints a JSON report: what it did, the
// URL before and after, and every console error, page error, dialog and failed request it raised; --expect-* asserts
// the action's effect. Exit 0 = ok and clean, 2 = ok but something unexpected was raised, 1 = failed.
import { createRequire } from 'node:module';
import { spawn, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// A launcher that starts this detached without a working directory of its own names the session's folder here.
if (process.env.JOINT_FOLDER) { try { process.chdir(process.env.JOINT_FOLDER); } catch { /* keep the current folder */ } }

const PLAYWRIGHT_VERSION = '1.64.0';
const NODE_DIR = path.join(os.homedir(), '.joint-browser', 'node');
const MIN_NODE = 20;   // what Playwright requires
const NODE_INSTALL = 'nodejs.org; winget install --id OpenJS.NodeJS.LTS -e; brew install node; on Debian/Ubuntu ' +
  'curl -fsSL https://deb.nodesource.com/setup_lts.x | sudo -E bash - && sudo apt install -y nodejs (the distro\'s own nodejs can be older)';

const USAGE = `Usage: node browser.mjs <command> [args] [options]

Setup and state:
  setup                         npm install playwright-core@${PLAYWRIGHT_VERSION} into ${NODE_DIR}
  status                        where Playwright comes from and whether the browser answers
  pages                         the open tabs (index, url, title); --page <index> picks one for any command
Actions (each reports its effect):
  goto <url>                    navigate
  click <selector>              click (dblclick, hover, check, uncheck likewise)
  fill <selector> <text>        replace a field's text
  type <selector> <text>        type key by key (for fields that react per key)
  press <key> [selector]        press a key (Enter, Tab, Control+A ...) on the page or an element
  select <selector> <value>     choose an <option> by value or label
  upload <selector> <file...>   set files on an <input type=file>
  back | forward | reload       history and reload
  newtab [url]                  open a tab (it becomes the newest tab)
  closetab                      close the chosen tab (--page; the window itself is never closed)
  wait <selector> [state]       wait until visible (or attached, detached, hidden)
Reading:
  snapshot [selector]           url, title and the ARIA tree (roles, names, states) of the page or an element
  text <selector>               an element's visible text
  attr <selector> <name>        an attribute's value
  count <selector>              how many elements match
  eval <expression>             evaluate JavaScript in the page and print the result
  screenshot <file> [selector]  save a PNG of the viewport (--full: whole page) or an element
  console                       console lines and page errors raised while waiting --settle ms
AI cost bar (a strip at the bottom of every page, for the whole joint session):
  costbar start                 start the background daemon: the bar shows this session's cost and its session group's total (every 60 s)
  costbar stop | status         remove the bar and stop the daemon / is it running, and the last cost it read
  costbar group                 the folder's session groups with their sessions and cost (a launch joins one with --group)

Selectors are Playwright selectors: CSS ("#email", "button.primary"), text ("text=Sign in"), role
("role=button[name='Sign in']"), label ("internal:label=\\"Email\\"i") or XPath ("xpath=//h1").
Options:
  --cdp <url>                   the browser's DevTools endpoint (default: $JOINT_CDP, else http://127.0.0.1:9222)
  --page <index>                the tab (from 'pages'); default: the newest tab
  --timeout <ms>                how long an action may wait for its element (default 10000)
  --settle <ms>                 how long to keep listening after the action (default 700)
  --expect-url <text>           fail unless the URL then contains the text
  --expect-text <text>          fail unless the page then shows the text
  --expect-visible <selector>   fail unless the element is then visible
  --expect-hidden <selector>    fail unless the element is then hidden or gone
  --accept-dialog [text]        accept a dialog (alert/confirm/prompt, with the prompt text) instead of dismissing it
  --allow <regex>               do not count console errors / failed requests whose text matches as unexpected
  --full                        screenshot: the whole page`;

function parse(argv) {
  const positional = [];
  const options = { cdp: process.env.JOINT_CDP || 'http://127.0.0.1:9222', page: null, timeout: 10000, settle: 700, expect: [], acceptDialog: null, allow: null, full: false };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const value = () => {
      if (i + 1 >= argv.length) throw new Error(`${a} needs a value`);
      return argv[++i];
    };
    if (a === '--cdp') options.cdp = value();
    else if (a === '--page') options.page = Number(value());
    else if (a === '--timeout') options.timeout = Number(value());
    else if (a === '--settle') options.settle = Number(value());
    else if (a === '--expect-url') options.expect.push(['url', value()]);
    else if (a === '--expect-text') options.expect.push(['text', value()]);
    else if (a === '--expect-visible') options.expect.push(['visible', value()]);
    else if (a === '--expect-hidden') options.expect.push(['hidden', value()]);
    else if (a === '--accept-dialog') options.acceptDialog = i + 1 < argv.length && !argv[i + 1].startsWith('--') ? argv[++i] : '';
    else if (a === '--allow') options.allow = new RegExp(value());
    else if (a === '--full') options.full = true;
    else if (a.startsWith('--')) throw new Error(`unknown option ${a}`);
    else positional.push(a);
  }
  return { command: positional[0], args: positional.slice(1), options };
}

/** Playwright from the project first (its own install), then from ~/.joint-browser/node; null when neither has it. */
function loadPlaywright() {
  for (const base of [process.cwd(), NODE_DIR]) {
    const require = createRequire(path.join(base, 'joint-browser.cjs'));
    for (const name of ['playwright', 'playwright-core']) {
      let file;
      try { file = require.resolve(name); } catch { continue; }
      return { playwright: require(file), from: file, version: require(path.join(path.dirname(require.resolve(`${name}/package.json`)), 'package.json')).version };
    }
  }
  return null;
}

function setup() {
  fs.mkdirSync(NODE_DIR, { recursive: true });
  const args = ['install', '--prefix', NODE_DIR, '--no-fund', '--no-audit', `playwright-core@${PLAYWRIGHT_VERSION}`];
  console.log(`npm ${args.join(' ')}`);
  // npm is npm.cmd on Windows, which only a shell runs.
  const result = spawnSync('npm', args, { stdio: 'inherit', shell: process.platform === 'win32' });
  if (result.error) throw new Error(`npm could not start (${result.error.message}): install Node.js (it brings npm)`);
  return result.status ?? 1;
}

async function cdpVersion(cdp) {
  try {
    const response = await fetch(new URL('/json/version', cdp), { signal: AbortSignal.timeout(2000) });
    return response.ok ? await response.json() : null;
  } catch {
    return null;
  }
}

function print(report) {
  console.log(JSON.stringify(report, null, 2));
}

async function main() {
  const { command, args, options } = parse(process.argv.slice(2));
  if (!command || command === 'help' || command === '--help') { console.log(USAGE); return 0; }
  const nodeMajor = Number(process.versions.node.split('.')[0]);
  if (nodeMajor < MIN_NODE) throw new Error(`Node.js ${process.versions.node} is too old: Playwright needs ${MIN_NODE} or later (${NODE_INSTALL})`);
  if (command === 'setup') return setup();

  const loaded = loadPlaywright();
  if (command === 'costbar') return costbarCommand(args[0], args.slice(1), options, loaded);
  const browserInfo = await cdpVersion(options.cdp);
  if (command === 'status') {
    print({ playwright: loaded ? { from: loaded.from, version: loaded.version } : null, cdp: options.cdp, browser: browserInfo ? browserInfo.Browser : null });
    return loaded && browserInfo ? 0 : 1;
  }
  if (!loaded) throw new Error(`no Playwright: run "node browser.mjs setup" (installs playwright-core into ${NODE_DIR}), or "npm install -D playwright" in the project`);
  if (!browserInfo) throw new Error(`no browser answers on ${options.cdp}: open the joint session first (it starts the shared browser with --remote-debugging-port=9222)`);

  const browser = await loaded.playwright.chromium.connectOverCDP(options.cdp);
  const context = browser.contexts()[0];
  if (!context) throw new Error('the browser has no window to drive');
  const pages = context.pages();

  if (command === 'pages') {
    const list = [];
    for (const [index, p] of pages.entries()) list.push({ index, url: p.url(), title: await p.title() });
    print({ pages: list });
    return 0;
  }
  if (command === 'newtab') {
    const opened = await context.newPage();
    if (args[0]) await opened.goto(args[0], { timeout: options.timeout });
    await opened.bringToFront();
    print({ ok: true, action: 'newtab', index: context.pages().indexOf(opened), url: opened.url() });
    return 0;
  }
  if (pages.length === 0) throw new Error('the browser has no open tab (newtab opens one)');
  if (options.page != null && !(options.page >= 0 && options.page < pages.length)) throw new Error(`--page ${options.page}: the browser has tabs 0-${pages.length - 1}`);
  const page = pages[options.page ?? pages.length - 1];
  page.setDefaultTimeout(options.timeout);

  // What the action raises: listened to from before it starts until --settle ms after it ends.
  const raised = { console_errors: [], page_errors: [], dialogs: [], failed_requests: [] };
  const allowed = (text) => options.allow != null && options.allow.test(text);
  page.on('console', (m) => { if ((m.type() === 'error' || m.type() === 'warning') && !allowed(m.text())) raised.console_errors.push(`${m.type()}: ${m.text()}`); });
  page.on('pageerror', (e) => raised.page_errors.push(String(e.message ?? e)));
  page.on('requestfailed', (r) => { const t = `${r.method()} ${r.url()} ${r.failure()?.errorText ?? ''}`; if (!allowed(t)) raised.failed_requests.push(t); });
  page.on('response', (r) => { const t = `${r.status()} ${r.request().method()} ${r.url()}`; if (r.status() >= 400 && !allowed(t)) raised.failed_requests.push(t); });
  // A dialog the action was told to accept is its expected effect; any other is dismissed and counted.
  const accepted = [];
  page.on('dialog', async (d) => {
    if (options.acceptDialog != null) { accepted.push(`${d.type()}: ${d.message()}`); await d.accept(options.acceptDialog); }
    else { raised.dialogs.push(`${d.type()}: ${d.message()}`); await d.dismiss(); }
  });

  const need = (n, what) => { if (args.length < n) throw new Error(`${command} needs ${what}`); };
  const urlBefore = page.url();
  let result;
  switch (command) {
    case 'goto': need(1, '<url>'); await page.goto(args[0]); break;
    case 'click': need(1, '<selector>'); await page.locator(args[0]).click(); break;
    case 'dblclick': need(1, '<selector>'); await page.locator(args[0]).dblclick(); break;
    case 'hover': need(1, '<selector>'); await page.locator(args[0]).hover(); break;
    case 'check': need(1, '<selector>'); await page.locator(args[0]).check(); break;
    case 'uncheck': need(1, '<selector>'); await page.locator(args[0]).uncheck(); break;
    case 'fill': need(2, '<selector> <text>'); await page.locator(args[0]).fill(args[1]); break;
    case 'type': need(2, '<selector> <text>'); await page.locator(args[0]).pressSequentially(args[1], { delay: 30 }); break;
    case 'press': need(1, '<key>'); if (args[1]) await page.locator(args[1]).press(args[0]); else await page.keyboard.press(args[0]); break;
    case 'select': need(2, '<selector> <value>'); result = await page.locator(args[0]).selectOption(args[1]); break;
    case 'upload': need(2, '<selector> <file...>'); await page.locator(args[0]).setInputFiles(args.slice(1)); break;
    case 'back': await page.goBack(); break;
    case 'forward': await page.goForward(); break;
    case 'reload': await page.reload(); break;
    case 'closetab':
      if (pages.length === 1) throw new Error('closetab would close the last tab and with it the shared window; goto another page instead');
      await page.close();
      print({ ok: true, action: 'closetab', closed: urlBefore });
      return 0;
    case 'wait': need(1, '<selector>'); await page.locator(args[0]).first().waitFor({ state: args[1] ?? 'visible' }); break;
    case 'snapshot': result = await page.locator(args[0] ?? 'body').first().ariaSnapshot(); break;
    case 'text': need(1, '<selector>'); result = await page.locator(args[0]).first().innerText(); break;
    case 'attr': need(2, '<selector> <name>'); result = await page.locator(args[0]).first().getAttribute(args[1]); break;
    case 'count': need(1, '<selector>'); result = await page.locator(args[0]).count(); break;
    case 'eval': need(1, '<expression>'); result = await page.evaluate(args[0]); break;
    case 'screenshot':
      need(1, '<file>');
      if (args[1]) await page.locator(args[1]).first().screenshot({ path: args[0] });
      else await page.screenshot({ path: args[0], fullPage: options.full });
      result = path.resolve(args[0]);
      break;
    case 'console': break;
    default: throw new Error(`unknown command '${command}' (node browser.mjs help lists them)`);
  }
  // An action that navigates settles here; a page the action closed throws, and the report says so.
  await page.waitForLoadState('domcontentloaded');
  await page.waitForTimeout(options.settle);

  const failed = [];
  for (const [kind, expected] of options.expect) {
    if (kind === 'url' && !page.url().includes(expected)) failed.push(`url ${page.url()} does not contain ${expected}`);
    if (kind === 'text' && (await page.getByText(expected).count()) === 0) failed.push(`the page shows no text ${expected}`);
    if (kind === 'visible' && !(await page.locator(expected).first().isVisible())) failed.push(`${expected} is not visible`);
    if (kind === 'hidden' && (await page.locator(expected).first().isVisible())) failed.push(`${expected} is still visible`);
  }
  if (options.acceptDialog != null && accepted.length === 0) failed.push('--accept-dialog: no dialog appeared');
  const clean = Object.values(raised).every((list) => list.length === 0);
  const report = { ok: failed.length === 0, clean, action: [command, ...args].join(' '), url_before: urlBefore, url: page.url(), title: await page.title() };
  if (failed.length) report.expect_failed = failed;
  if (accepted.length) report.dialogs_accepted = accepted;
  if (result !== undefined) report.result = result;
  for (const [kind, list] of Object.entries(raised)) if (list.length) report[kind] = list;
  print(report);
  return failed.length ? 1 : clean ? 0 : 2;
}

// ---- costbar: the AI cost of this folder's harness session as a strip at the bottom of every page of the shared
// browser, so a joint session always shows what it spends (the joint-hackernews skill draws its own bar and this one
// steps aside on its pages). `costbar start` runs a daemon that stays attached to the browser, injects the strip into
// every document (it survives navigation, including the user's own clicks) and re-reads the cost with the repository's
// cost reader (runJointCostAST.cs). An error is shown as "unavailable", never as a number. The strip ignores the pointer, so
// it never intercepts a click.
const HERE = path.dirname(fileURLToPath(import.meta.url));
const COSTBAR_DIR = path.join(os.homedir(), '.joint-browser');
const COSTBAR_FILE = path.join(COSTBAR_DIR, 'costbar.json');
const COSTBAR_LOG = path.join(COSTBAR_DIR, 'costbar.log');

const COSTBAR_INJECT = `(() => {
  // A page holding an older strip gets this one in its place (the daemon injects again after an update).
  if (window.top !== window || window.__jcCostLook === 3) return;
  window.__jcCostLook = 3;
  const old = document.getElementById('jc-costbar');
  if (old) old.remove();
  let host = null;
  const clear = () => { if (host) { host.remove(); host = null; } };
  window.__jcCostApply = (s) => {
    if (!s || document.getElementById('hn-foot')) return clear();
    if (!document.documentElement) return;
    if (!host || !host.isConnected) {
      host = document.createElement('div');
      host.id = 'jc-costbar';
      host.style.cssText = 'position:fixed;left:0;right:0;bottom:0;z-index:2147483647;pointer-events:none;margin:0;padding:0;border:0';
      const root = host.attachShadow({ mode: 'closed' });
      root.innerHTML = '<style>' +
        '.bar{display:flex;justify-content:space-between;align-items:baseline;gap:16px;padding:4px 14px;font:12px/1.3 system-ui,sans-serif;background:#1d1d22;color:#e6e6e6;border-top:1px solid #444}' +
        '.n{color:#9a9aa3}.c{display:flex;align-items:baseline;gap:8px;white-space:nowrap}' +
        '.t{color:#ffd9bd;font-weight:600}.l{color:#9a9aa3}.v{font-weight:600;font-variant-numeric:tabular-nums}.sep{color:#555}.note{color:#9a9aa3;font-size:11px}.err{color:#ff9d9d}' +
        '</style><div class="bar"><span class="n"></span><span class="c"></span></div>';
      host.__c = root.querySelector('.c'); host.__n = root.querySelector('.n'); host.__bar = root.querySelector('.bar');
      document.documentElement.append(host);
    }
    host.__n.textContent = s.tool || 'joint session';   // the tool that started the session (JOINT_TOOL), so its browser tells it apart
    // The session panel sits over the bottom-right corner of the window: the text keeps clear of it (s.right px, from JOINT_PANEL_RIGHT).
    host.__bar.style.paddingRight = (14 + (s.right || 0)) + 'px';
    // Only text nodes: the group name is the user's own text.
    const part = (cls, text) => { const e = document.createElement('span'); e.className = cls; e.textContent = text; return e; };
    const money = (usd) => '$' + usd.toFixed(2) + (s.defaulted ? '*' : '');
    const parts = [part('t', 'AI cost:')];
    if (s.error) parts.push(part('err', 'unavailable (' + String(s.error).slice(0, 90) + ')'));
    else {
      const priced = (usd) => { const e = part('v', money(usd)); if (s.defaulted) e.title = 'estimate: default price for ' + s.defaulted; return e; };
      parts.push(part('l', 'Session'), priced(s.session), part('sep', '|'), part('l', 'Group ' + s.group), priced(s.groupUsd));
    }
    host.__c.replaceChildren(...parts);
  };
})()`;

function readJsonFile(file, fallback) {
  try { return JSON.parse(fs.readFileSync(file, 'utf8')); } catch { return fallback; }
}
function alive(pid) {
  try { process.kill(pid, 0); return true; } catch { return false; }
}
function costbarRunning() {
  const d = readJsonFile(COSTBAR_FILE, null);
  return d && alive(d.pid) ? d : null;
}

/** How far the bar's text keeps from the right edge so the session panel (bottom-right) does not cover it: JOINT_PANEL_RIGHT when a tool
 *  sets it, else read from the standalone panel's own settings (runJointPanelAST.json beside the panel, found as the cost reader is):
 *  PanelInset + the 28-unit square and its margin (36) when the corner is bottom-right, else 0. No panel program found: 0. */
function panelRight() {
  const cap = (n) => Math.max(0, Math.min(400, n));
  if (process.env.JOINT_PANEL_RIGHT) return cap(parseInt(process.env.JOINT_PANEL_RIGHT, 10) || 0);
  for (let dir of [process.cwd(), HERE]) {
    for (let i = 0; i < 8; i++, dir = path.dirname(dir)) {
      for (const sub of ['', 'csharp/ArshuPanel']) {
        const folder = path.join(dir, sub);
        let names = [];
        try { names = fs.readdirSync(folder); } catch { continue; }
        if (!names.some((n) => /^runJointPanelAST(\.cs|-.+)$/.test(n))) continue;
        const settings = readJsonFile(path.join(folder, 'runJointPanelAST.json'), {});
        const corner = typeof settings.PanelCorner === 'string' ? settings.PanelCorner : 'bottom-right';
        const inset = Number.isInteger(settings.PanelInset) ? settings.PanelInset : 0;
        return corner === 'bottom-right' ? cap(inset + 36) : 0;
      }
    }
  }
  return 0;
}

function findCostReader() {
  if (process.env.JOINT_COST_READER) return process.env.JOINT_COST_READER;
  for (let dir of [process.cwd(), HERE]) {
    for (let i = 0; i < 8; i++, dir = path.dirname(dir)) {
      for (const rel of ['csharp/ArshuCost/runJointCostAST.cs', 'ArshuCost/runJointCostAST.cs', 'runJointCostAST.cs']) {
        const file = path.join(dir, rel);
        if (fs.existsSync(file)) return file;
      }
    }
  }
  return null;
}

/** This skill's own harness (the folder it lives in), so another harness's old sessions cannot break the number. */
function skillHarness() {
  // the launcher that started the session names the harness (it runs this from its own skills folder, not the harness's)
  if (['claude', 'codex', 'pi'].includes(process.env.JOINT_HARNESS)) return process.env.JOINT_HARNESS;
  return HERE.includes(`${path.sep}.agents${path.sep}`) ? 'codex' : HERE.includes(`${path.sep}.pi${path.sep}`) ? 'pi' : 'claude';
}

/** How to run the cost reader: `dotnet run runJointCostAST.cs -- args` for the script, `<binary> --cost args` for a tool's NativeAOT
 *  binary, which carries the reader (the launcher puts either path in JOINT_COST_READER). The two ways to ship stay independent. */
function costCommand(reader, args) {
  if (!/\.cs$/i.test(reader)) return { file: reader, args: ['--cost', ...args] };
  // The tool's cost watcher runs this same script and holds its build output (dotnet's run cache is keyed by the script's
  // path; on Windows the running exe cannot be replaced), so every `dotnet run` of it fails while the watcher lives. The bar
  // reads from a copy of its own: another path, another build output.
  const copy = path.join(COSTBAR_DIR, 'reader', path.basename(reader));
  fs.mkdirSync(path.dirname(copy), { recursive: true });
  const source = fs.readFileSync(reader);
  if (!fs.existsSync(copy) || !fs.readFileSync(copy).equals(source)) fs.writeFileSync(copy, source);
  return { file: 'dotnet', args: ['run', copy, '--', ...args] };
}

/** Runs the cost reader for the current folder; resolves {session, group, groupUsd, defaulted} or {error}. */
function readCost() {
  return new Promise((resolve) => {
    const reader = findCostReader();
    if (!reader) return resolve({ error: 'runJointCostAST.cs not found (set JOINT_COST_READER to it, or to a tool binary that carries it)' });
    // this skill's own harness (the folder it lives in), so another harness's old sessions cannot break the number
    const harness = skillHarness();
    const cmd = costCommand(reader, ['--folder', process.cwd(), '--harness', harness, '--json', '--no-snapshot']);
    const child = spawn(cmd.file, cmd.args, { windowsHide: true });
    let out = '', err = '';
    const timer = setTimeout(() => child.kill(), 120000);
    child.stdout.on('data', (d) => (out += d));
    child.stderr.on('data', (d) => (err += d));
    child.on('error', (e) => { clearTimeout(timer); resolve({ error: 'cannot start dotnet: ' + e.message }); });
    child.on('close', (code) => {
      clearTimeout(timer);
      try {
        const j = JSON.parse(out);
        const rows = j.rows || [];
        const def = rows.find((r) => String(r.costSource || '').includes('+default:'));
        const unpriced = rows.some((r) => r.unpriced);
        resolve({ session: j.session ? j.session.costUsd : 0, group: j.group.name, groupUsd: j.group.costUsd, defaulted: def ? String(def.costSource).split('+default:')[1] : unpriced ? 'a model with no price (its cost is not counted)' : null });
      } catch {
        resolve({ error: ((out + err).trim().split('\n')[0] || `cost reader exited ${code}`).slice(0, 300) });
      }
    });
  });
}

async function costbarServe(options, loaded) {
  if (!loaded) throw new Error('no Playwright: run "node browser.mjs setup" first');
  fs.mkdirSync(COSTBAR_DIR, { recursive: true });
  const browser = await loaded.playwright.chromium.connectOverCDP(options.cdp);
  const context = browser.contexts()[0];
  if (!context) throw new Error('the browser has no window to drive');
  let cost = null, busy = false;

  const write = () => fs.writeFileSync(COSTBAR_FILE, JSON.stringify({ pid: process.pid, cdp: options.cdp, folder: process.cwd(), cost, at: new Date().toISOString() }, null, 2));
  const apply = (page) => page.evaluate(COSTBAR_INJECT).then(() => page.evaluate((s) => window.__jcCostApply && window.__jcCostApply(s), cost && { ...cost, right: panelRight(), tool: process.env.JOINT_TOOL || '' })).catch(() => {});
  const applyAll = () => Promise.all(context.pages().map(apply));
  await context.addInitScript({ content: COSTBAR_INJECT });   // later documents; the daemon pushes the state when they load
  const watch = (page) => page.on('domcontentloaded', () => apply(page));
  context.pages().forEach(watch);
  context.on('page', watch);

  const refresh = async () => {
    if (busy) return;
    busy = true;
    const c = await readCost();
    busy = false;
    cost = c;
    write();
    await applyAll();
  };
  write();
  const exit = () => { try { fs.unlinkSync(COSTBAR_FILE); } catch { /* gone */ } process.exit(0); };
  browser.on('disconnected', exit);
  process.on('SIGINT', exit);
  process.on('SIGTERM', exit);
  console.log(`[costbar] serving ${options.cdp} for ${process.cwd()}`);
  setInterval(refresh, 60000);
  setInterval(applyAll, 5000);   // cheap and local: a page the app rebuilt gets its strip back
  await refresh();
}

async function costbarCommand(sub, args, options, loaded) {
  const running = costbarRunning();
  if (sub === 'status') {
    print(running ? { running: true, pid: running.pid, folder: running.folder, cost: running.cost, at: running.at } : { running: false });
    return running ? 0 : 1;
  }
  if (sub === 'start') {
    if (running) { print({ running: true, pid: running.pid, note: 'already running' }); return 0; }
    if (!loaded) throw new Error('no Playwright: run "node browser.mjs setup" first');
    if (!(await cdpVersion(options.cdp))) throw new Error(`no browser answers on ${options.cdp}: open the joint session first`);
    fs.mkdirSync(COSTBAR_DIR, { recursive: true });
    try { fs.unlinkSync(COSTBAR_FILE); } catch { /* none */ }
    const log = fs.openSync(COSTBAR_LOG, 'w');
    spawn(process.execPath, [fileURLToPath(import.meta.url), 'costbar', 'serve', '--cdp', options.cdp], { detached: true, stdio: ['ignore', log, log], windowsHide: true, cwd: process.cwd() }).unref();
    for (let i = 0; i < 80; i++) {   // up to 40 s: the first cost read runs dotnet
      await new Promise((r) => setTimeout(r, 500));
      const d = costbarRunning();
      if (d && d.cost) { print({ running: true, pid: d.pid, cost: d.cost }); return d.cost.error ? 2 : 0; }
    }
    print({ running: false, error: 'the cost bar daemon did not come up', log: fs.readFileSync(COSTBAR_LOG, 'utf8').slice(-600) });
    return 1;
  }
  if (sub === 'stop') {
    if (!running) { print({ running: false, note: 'not running' }); return 0; }
    try {   // take the strip off the open pages first; with no browser there is nothing to clean
      const browser = await loaded.playwright.chromium.connectOverCDP(options.cdp);
      for (const page of browser.contexts()[0]?.pages() ?? []) await page.evaluate(() => window.__jcCostApply && window.__jcCostApply(null)).catch(() => {});
    } catch { /* browser gone */ }
    try { process.kill(running.pid); } catch { /* already gone */ }
    try { fs.unlinkSync(COSTBAR_FILE); } catch { /* gone */ }
    print({ running: false, stopped: running.pid });
    return 0;
  }
  if (sub === 'group') {   // the folder's session groups (a launch is put in one with the launcher's --group)
    const reader = findCostReader();
    if (!reader) throw new Error('runJointCostAST.cs not found (set JOINT_COST_READER)');
    const cmd = costCommand(reader, ['--folder', process.cwd(), '--groups', '--harness', skillHarness()]);
    const run = spawnSync(cmd.file, cmd.args, { encoding: 'utf8', windowsHide: true });
    process.stdout.write(run.stdout ?? '');
    if (run.status !== 0) throw new Error(((run.stderr ?? '') + (run.stdout ?? '')).trim().split('\n')[0] || 'the cost reader failed');
    return 0;
  }
  throw new Error('costbar start | stop | status | group');
}

// The connection is dropped by exiting, never by browser.close(): the shared window stays as it is.
const fail = (error) => {
  print({ ok: false, error: String(error.message ?? error).split('\n')[0] });
  process.exit(1);
};
if (process.argv[2] === 'costbar' && process.argv[3] === 'serve') costbarServe(parse(process.argv.slice(4)).options, loadPlaywright()).catch(fail);
else main().then((code) => process.exit(code), fail);
