// joint-hackernews: the script hn.mjs injects into every news.ycombinator.com document (addInitScript, so it survives
// navigation). It never writes to HN: it only restyles and annotates what the page already shows. Data it cannot fetch
// itself (HN's CSP blocks cross-origin fetch) comes through window.__hnBridge, which the hn.mjs daemon exposes.
// The daemon pushes the whole state ({options, annotations, cost, requests}) with window.__hnApply(state); every
// feature is a pure function of that state, so apply() can run again at any time.
(() => {
  'use strict';
  if (location.hostname !== 'news.ycombinator.com' || window.__hnLoaded) return;
  window.__hnLoaded = true;

  const LIST_PATHS = new Set(['/', '/news', '/newest', '/front', '/ask', '/show', '/jobs', '/from', '/active', '/best', '/shownew', '/noobstories', '/classic', '/over']);
  const NOTICE = "Write your own words. Please don't post AI-generated comments, replies or submissions to HN.";

  const $ = (s, r = document) => r.querySelector(s);
  const $$ = (s, r = document) => [...r.querySelectorAll(s)];
  const el = (tag, attrs = {}, ...kids) => {
    const e = document.createElement(tag);
    for (const [k, v] of Object.entries(attrs)) {
      if (k === 'class') e.className = v;
      else if (k === 'text') e.textContent = v;
      else if (v === true) e.setAttribute(k, '');
      else if (v !== false && v != null) e.setAttribute(k, String(v));
    }
    for (const c of kids) if (c != null) e.append(c);
    return e;
  };
  const get = (o, p) => p.split('.').reduce((a, k) => (a == null ? a : a[k]), o);
  // a failing bridge means the daemon is gone: say so on the dock tab instead of failing silently (edits would not be saved)
  const bridgeDown = (e) => { const t = document.getElementById('hn-tab'); if (t) { t.textContent = 'HN ⚠ daemon off'; t.title = 'the joint-hackernews daemon is not running, so changes are not saved: node hn.mjs start'; } throw e; };
  // a call to a dead daemon never settles, so it also times out (20 s covers the slowest real call, the karma lookup)
  const bridge = (m) => Promise.race([window.__hnBridge ? window.__hnBridge(m) : Promise.reject(new Error('no bridge')),
    new Promise((_, no) => setTimeout(() => no(new Error('the daemon is not answering')), 20000))]).catch(bridgeDown);
  const store = {
    get(k, d) { try { const v = localStorage.getItem(k); return v == null ? d : JSON.parse(v); } catch { return d; } },
    set(k, v) { try { localStorage.setItem(k, JSON.stringify(v)); } catch { /* storage blocked: the feature just forgets */ } },
  };
  const ARTICLE_TOK = { frugal: 750, balanced: 1500, thorough: 3000 };   // what an article summary request reads, by token-use level
  const kfmt = (n) => (n >= 1000 ? (n / 1000).toFixed(n >= 10000 ? 0 : 1) + 'k' : String(n));

  let S = null;                  // {options, annotations, cost, requests}
  const karma = new Map();       // user -> {karma, created} | null (unknown)
  let version = 0;

  const pageType = () =>
    location.pathname === '/item' ? 'item' : LIST_PATHS.has(location.pathname) && $('tr.athing') ? 'list' : 'other';

  // ---------------------------------------------------------------- CSS
  const CSS = `
  #hn-foot{position:fixed;left:0;right:0;bottom:0;z-index:2147483646;display:flex;gap:12px;justify-content:space-between;align-items:center;
    padding:5px 12px;font:12px/1.3 system-ui,sans-serif;background:var(--hn-bar,#ff6600);color:var(--hn-bar-fg,#000)}
  #hn-foot .n{font-weight:700}#hn-foot .c{white-space:nowrap;font-weight:700;background:#1a0d00;color:#ffd9bd;padding:1px 8px;border-radius:8px}
  html.hn-on body{padding-bottom:30px}
  #hn-tab{position:fixed;z-index:2147483645;font:12px system-ui,sans-serif;background:var(--hn-bar,#ff6600);color:var(--hn-bar-fg,#000);border:0;padding:6px 8px;cursor:pointer;border-radius:6px 0 0 6px}
  #hn-panel{position:fixed;z-index:2147483644;background:#26262a;color:#e6e6e6;font:12px/1.4 system-ui,sans-serif;overflow:auto;padding:10px;box-sizing:border-box;display:none;box-shadow:0 0 12px #0008}
  #hn-panel h4{margin:10px 0 4px;font-size:12px;text-transform:uppercase;letter-spacing:.05em;color:#ffb27a}
  #hn-panel label{display:flex;gap:6px;align-items:center;margin:3px 0}
  #hn-panel input[type=number],#hn-panel input[type=text],#hn-panel select{background:#111;color:#eee;border:1px solid #555;border-radius:3px;padding:1px 4px;font:inherit}
  #hn-panel input[type=text]{width:100%}
  #hn-panel button{background:#3a3a40;color:#eee;border:1px solid #666;border-radius:4px;padding:3px 8px;cursor:pointer;font:inherit;margin:2px 4px 2px 0}
  #hn-panel .chip{display:inline-block;margin:2px 4px 2px 0;padding:1px 8px;border-radius:10px;border:2px solid var(--c);cursor:pointer}
  #hn-panel .chip.on{background:var(--c);color:#fff}
  #hn-panel .free{color:#7bd88f}#hn-panel .paid{color:#ffb27a}#hn-panel .req{margin-top:8px;padding:4px 6px;background:#4a3a1a;border-radius:4px}
  html.hn-pos-right #hn-tab{right:0;top:80px}html.hn-pos-right #hn-panel{right:0;top:0;bottom:30px;width:310px}
  html.hn-pos-left #hn-tab{left:0;top:80px;border-radius:0 6px 6px 0}html.hn-pos-left #hn-panel{left:0;top:0;bottom:30px;width:310px}
  html.hn-pos-top #hn-tab{top:0;right:20px;border-radius:0 0 6px 6px}html.hn-pos-top #hn-panel{left:0;right:0;top:0;max-height:45vh}
  html.hn-pos-bottom #hn-tab{bottom:30px;right:20px;border-radius:6px 6px 0 0}html.hn-pos-bottom #hn-panel{left:0;right:0;bottom:30px;max-height:45vh}
  html.hn-open #hn-panel{display:block}
  html.hn-open.hn-pos-right body{padding-right:310px}html.hn-open.hn-pos-left body{padding-left:310px}
  @media (max-width:700px){#hn-panel{left:0!important;right:0!important;top:auto!important;bottom:30px!important;width:auto!important;max-height:55vh}
    #hn-tab{top:auto!important;bottom:30px!important;right:8px!important;left:auto!important;border-radius:6px 6px 0 0!important}
    html.hn-open body{padding-left:0!important;padding-right:0!important}}
  .hn-badge{font-size:10px;padding:0 5px;margin-left:5px;border-radius:8px;white-space:nowrap}
  .hn-bk{background:#c9a227;color:#000}.hn-bw{background:#9b59d0;color:#fff}.hn-bf{background:#ff6600;color:#fff;font-weight:700}
  .hn-bg{color:#fff}
  .hn-star{cursor:pointer;font-size:15px;background:none!important;color:#e0a100!important;padding:0 3px;user-select:none}
  #hn-panel .bm{max-height:34vh;overflow:auto;border:1px solid #444;border-radius:4px;padding:4px 6px;margin:4px 0}
  #hn-panel .bm b{color:#ffb27a}#hn-panel .bd{color:#8a8a8a;margin:4px 0 2px;font-size:11px}
  #hn-panel .be{margin:2px 0 6px;line-height:1.3}#hn-panel .be a{color:#8ab4f8}#hn-panel .be input[type=text]{width:100%;margin-top:2px}
  #hn-panel .be select{max-width:90px;margin-left:4px}#hn-panel .be button{padding:0 6px;margin:0 0 0 4px}
  #hn-panel .bm-meta{color:#8a8a8a;font-size:11px}
  tr.hn-muted{display:none}.hn-queued{color:#ff6600!important}tr.hn-mshown{opacity:.4}
  tr.hn-alert>td:first-child{box-shadow:inset 8px 0 0 #ff2d95}.hn-bn{background:#ff2d95;color:#fff}
  .hn-mutebtn,.hn-art{cursor:pointer;background:none!important;color:#999!important;padding:0 3px}.hn-art[data-article]:hover,.hn-mutebtn:hover{color:#ff6600!important}
  tr.hn-k1>td:first-child{box-shadow:inset 4px 0 0 #c9a227}tr.hn-k2>td:first-child{box-shadow:inset 6px 0 0 #e8761f}
  tr.hn-k3>td:first-child{box-shadow:inset 8px 0 0 #e03a2f}tr.hn-kw>td:first-child{box-shadow:inset 6px 0 0 #9b59d0}
  #hnmain tr.hn-g>td{background-color:color-mix(in srgb,var(--hn-gc) 15%,transparent)}
  .hn-newb{background:#2a9d8f;color:#fff}.hn-pickb{background:#d4a017;color:#000}tr.hn-seen{opacity:.5}tr.hn-hide{display:none}
  html.hn-nofoot .yclinks,html.hn-nofoot #hnmain>tbody>tr:last-child{display:none}
  html.hn-wide #hnmain{width:96%!important}
  html.hn-guides td.ind{background:repeating-linear-gradient(90deg,transparent 0 39px,var(--hn-line,#c9c9c0) 39px 40px)}
  tr.hn-op>td.default{box-shadow:inset 3px 0 0 #ff6600}tr.hn-good>td.default{box-shadow:inset 3px 0 0 #3fa35b}
  tr.hn-collapsed:not(.hn-open) .comment{display:none}
  .hn-stub{font-size:11px;color:#888;padding:2px 0}.hn-stub a{margin-left:6px;color:#4a8fd6!important;cursor:pointer}
  .hn-flash{outline:2px solid #ff6600;outline-offset:2px}
  #hn-sum{margin:8px 0 14px;padding:10px 12px;background:#f0efe5;color:#222;border-left:4px solid #ff6600;font:13px/1.45 system-ui,sans-serif;max-width:900px}
  #hn-sum h3{margin:0 0 4px;font-size:14px}#hn-sum li{margin:4px 0}#hn-sum a{color:#b34700;cursor:pointer}
  #hn-sum .m{font-size:11px;color:#777;margin-top:6px}
  html.hn-t-dark{--hn-line:#555;--hn-bar:#8a3d00;--hn-bar-fg:#fff}
  html.hn-t-sepia{--hn-bar:#c8691f;--hn-bar-fg:#fff}
  html.hn-t-dark body,html.hn-t-dark #hnmain{background:#1c1c1f!important;color:#d6d6d6}
  html.hn-t-dark #hnmain tr,html.hn-t-dark #hnmain td{background-color:transparent}
  html.hn-t-dark #hnmain td[bgcolor="#ff6600"]{background-color:#8a3d00}
  html.hn-t-dark a:link,html.hn-t-dark .titleline>a{color:#e6e6e6}html.hn-t-dark a:visited,html.hn-t-dark .titleline>a:visited{color:#8f8f8f}
  html.hn-t-dark .subtext,html.hn-t-dark .subtext a,html.hn-t-dark .comhead,html.hn-t-dark .comhead a,html.hn-t-dark .sitebit a,html.hn-t-dark .rank{color:#8a8a8a}
  html.hn-t-dark .commtext,html.hn-t-dark .commtext *{color:#d0d0d0!important}html.hn-t-dark .commtext a:link{color:#6fb1ff!important}
  html.hn-t-dark input,html.hn-t-dark textarea{background:#111;color:#eee}
  html.hn-t-dark #hn-sum{background:#2a2a2e;color:#ddd}html.hn-t-dark #hn-sum a{color:#ffa060}
  html.hn-t-sepia{--hn-line:#bfae8a}
  html.hn-t-sepia body,html.hn-t-sepia #hnmain{background:#f1e7d0!important}
  html.hn-t-sepia #hnmain tr,html.hn-t-sepia #hnmain td{background-color:transparent}
  html.hn-t-sepia #hnmain td[bgcolor="#ff6600"]{background-color:#c8691f}
  html.hn-t-sepia .commtext,html.hn-t-sepia .commtext *{color:#3b2f1e!important}
  `;

  // ---------------------------------------------------------------- data
  function stories() {
    return $$('tr.athing').filter((r) => !r.classList.contains('comtr')).map((r) => {
      const sub = r.nextElementSibling, spacer = sub?.nextElementSibling;
      const a = $('.titleline > a', r);
      const ageEl = $('.age', sub || r);
      const cm = $$('a', sub || r).filter((x) => /comment|discuss/.test(x.textContent)).pop();
      return {
        id: r.id, row: r, sub, spacer, title: a?.textContent || '', url: a?.href || '', domain: $('.sitestr', r)?.textContent || '',
        user: $('.hnuser', sub || r)?.textContent || '', score: parseInt($('.score', sub || r)?.textContent) || 0,
        t: ageEl ? Date.parse((ageEl.title || '').split(' ')[0] + 'Z') : NaN,
        comments: cm ? parseInt(cm.textContent) || 0 : 0,
      };
    });
  }
  const esc = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const rxCache = new Map();
  function matcher(g) {
    const kws = (g.keywords || []).filter(Boolean);
    const key = g.name + '|' + kws.join(',');
    if (!rxCache.has(key)) rxCache.set(key, kws.length ? new RegExp('(^|[^a-z0-9])(' + kws.map(esc).join('|') + ')($|[^a-z0-9])', 'i') : null);
    return rxCache.get(key);
  }
  function groupOf(s) {
    const o = S.options.list;
    const ann = S.annotations.stories[s.id];
    if (ann && String(o.classifier).includes('claude')) return ann;
    const text = s.title + ' ' + s.domain;
    for (const g of o.groups) {
      const rx = matcher(g);
      if ((rx && rx.test(text)) || (g.domains || []).some((d) => d && (s.domain === d || s.domain.endsWith('.' + d)))) return g.name;
    }
    return 'Others';
  }
  const colorOf = (name) => (S.options.list.groups.find((g) => g.name === name) || {}).color || '#888888';

  // ---------------------------------------------------------------- bookmarks (local file kept by the daemon; not HN favorites)
  let starred = new Set();
  const starEl = (kind, id) => {
    const on = starred.has(kind + ':' + id);
    return el('span', { class: 'hn-badge hn-star', 'data-star': id, 'data-kind': kind, text: on ? '★' : '☆', title: on ? 'remove this bookmark' : 'bookmark this (kept in a local file; not an HN favorite)' });
  };
  function itemGroupName() {
    return groupOf({ id: new URLSearchParams(location.search).get('id'), title: $('.fatitem .titleline')?.innerText || '', domain: $('.fatitem .sitestr')?.textContent || '' });
  }
  function entryFor(kind, id) {
    if (kind === 'story') {
      const s = pageType() === 'list' ? (window.__hnStories || []).find((x) => x.id === id) : null;
      if (s) return { title: s.title, url: s.url, domain: s.domain, user: s.user, points: s.score, comments: s.comments, postedAt: isNaN(s.t) ? '' : new Date(s.t).toISOString(), group: s.group };
      const fat = $('.fatitem'), a = $('.titleline > a', fat), age = ($('.age', fat)?.title || '').split(' ')[0];
      return { title: a?.textContent || '', url: a?.href || '', domain: $('.sitestr', fat)?.textContent || '', user: $('.hnuser', fat)?.textContent || '', points: parseInt($('.score', fat)?.textContent) || 0, comments: $$('tr.comtr').length, postedAt: age ? age + 'Z' : '', group: itemGroupName() };
    }
    const r = document.getElementById(id), text = ($('.commtext', r)?.innerText || '').replace(/\s+/g, ' ').trim(), user = $('.hnuser', r)?.textContent || '';
    return { title: user + ': ' + text.slice(0, 120), url: location.origin + '/item?id=' + id, domain: '', user, points: 0, comments: 0, postedAt: (($('.age', r)?.title || '').split(' ')[0] || '') + 'Z', group: itemGroupName() };
  }
  function bookmarksBlock() {
    const B = S.bookmarks || {}, q = (sessionStorage.getItem('hn-bq') || '').toLowerCase();
    const known = [...S.options.list.groups.map((g) => g.name), 'Others'];
    const rank = (g) => (known.indexOf(g) < 0 ? 99 : known.indexOf(g));
    const body = el('div', { class: 'bm' });
    let n = 0, total = 0;
    for (const g of Object.keys(B).sort((a, b) => rank(a) - rank(b))) {
      const gEl = el('div', {}, el('b', { text: g }));
      let count = 0;
      for (const d of Object.keys(B[g]).sort().reverse()) {
        const items = B[g][d];
        total += items.length;
        const shown = items.filter((e) => !q || (e.title + ' ' + e.note + ' ' + e.domain + ' ' + e.user + ' ' + (e.tags || []).join(' ')).toLowerCase().includes(q));
        if (!shown.length) continue;
        gEl.append(el('div', { class: 'bd', text: d }));
        for (const e of shown) {
          count++; n++;
          const move = el('select', { 'data-bmove': '1', 'data-bt': e.type, 'data-bid': e.id, title: 'move to another group' }, ...[...new Set([...known, g])].map((x) => el('option', { value: x, text: x, selected: x === g })));
          gEl.append(el('div', { class: 'be' },
            el('a', { href: e.url, text: (e.type === 'comment' ? '💬 ' : '') + (e.title || e.url) }), ' ',
            el('a', { href: '/item?id=' + e.id, text: 'discuss' }),
            el('span', { class: 'bm-meta', text: ' ' + [e.domain, e.user, e.points ? e.points + ' pts' : ''].filter(Boolean).join(' · ') }),
            move, el('button', { 'data-bdel': '1', 'data-bt': e.type, 'data-bid': e.id, title: 'remove', text: '×' }),
            el('input', { type: 'text', 'data-bnote': '1', 'data-bt': e.type, 'data-bid': e.id, value: e.note || '', placeholder: 'note' }),
            el('input', { type: 'text', 'data-btags': '1', 'data-bt': e.type, 'data-bid': e.id, value: (e.tags || []).join(', '), placeholder: 'tags, comma separated' })));
        }
      }
      if (count) body.append(gEl);
    }
    if (!n) body.append(el('div', { class: 'bm-meta', text: total ? 'nothing matches' : 'Star a story (☆) or a comment to keep it here.' }));
    return [
      el('h4', { text: 'Bookmarks (' + total + ')' }),
      el('input', { type: 'text', id: 'hn-bsearch', placeholder: 'search title, note, site, user', value: sessionStorage.getItem('hn-bq') || '' }),
      body,
      el('div', {}, el('button', { 'data-bmd': '1', text: 'Copy as Markdown' }), el('span', { class: 'bm-meta', text: ' a local file; HN is never written to' })),
    ];
  }
  function readEstimate(type) {
    const c = S.options.claude;
    if (type === 'list') return (window.__hnStories || []).length * 30;
    let rows = $$('tr.comtr');
    if (c.maxDepth > 0) rows = rows.filter((r) => Number($('td.ind', r)?.getAttribute('indent') || 0) < c.maxDepth);
    rows = rows.slice(0, c.maxComments);
    return Math.round(rows.reduce((a, r) => a + Math.min(($('.commtext', r)?.innerText || '').length, c.commentChars), 0) / 4 + rows.length * 8);
  }

  // ---------------------------------------------------------------- worth joining (candidates from the daemon, scored here with the user's groups)
  let OPP = { at: 0, items: null, loading: false, error: '' };
  function loadOpps(force) {
    if (!S || OPP.loading || !(S.options.modules || []).includes('opportunities') || !document.documentElement.classList.contains('hn-open')) return;
    if (!force && OPP.items && Date.now() - OPP.at < 300000) return;
    OPP.loading = true;
    bridge({ type: 'opps', force: !!force }).then((r) => { OPP = { at: Date.now(), items: r.items, loading: false, error: '' }; apply(); },
      (e) => { OPP = { at: Date.now(), items: OPP.items, loading: false, error: String(e.message || e).replace(/^.*Error: /, '') }; apply(); });
  }
  function opportunitiesBlock() {
    const O = S.options.opportunities, out = [el('h4', { text: 'Worth joining' })];
    out.push(field('Max comments so far', opt('opportunities.maxComments', 'num'), O.maxComments));
    out.push(field('Max age (hours)', opt('opportunities.maxAgeHours', 'num'), O.maxAgeHours));
    out.push(field('Any Ask HN, even outside my groups', opt('opportunities.includeAsk', 'bool'), O.includeAsk));
    if (OPP.error) out.push(el('div', { class: 'req', text: 'Could not read HN\'s search: ' + OPP.error }));
    if (!OPP.items) { out.push(el('div', { class: 'bm-meta', text: OPP.loading ? 'Looking…' : 'Open this panel to look.' })); return out; }
    const now = Date.now() / 1000, scored = [];
    for (const s of OPP.items) {
      const ageH = (now - s.at) / 3600;
      if (ageH > O.maxAgeHours || s.comments > O.maxComments) continue;
      const group = groupOf({ id: s.id, title: s.title, domain: s.domain });
      if (group === 'Others' && !(O.includeAsk && s.ask)) continue;
      const score = (ageH < 1 ? 3 : ageH < 3 ? 2 : 1) + (s.comments === 0 ? 3 : s.comments <= 3 ? 2 : 1) + (s.ask ? 2 : 0) + (s.ask && s.comments <= 2 ? 2 : 0) + (s.points >= 10 ? 1 : 0);
      scored.push({ s, group, ageH, score });
    }
    scored.sort((a, b) => b.score - a.score || a.ageH - b.ageH);
    const box = el('div', { class: 'bm' });
    if (!scored.length) box.append(el('div', { class: 'bm-meta', text: 'Nothing early and quiet in your groups right now (of ' + OPP.items.length + ' threads looked at).' }));
    for (const { s, group, ageH } of scored.slice(0, 8)) {
      const chip = el('span', { class: 'hn-badge hn-bg', text: group }); chip.style.background = colorOf(group);
      const age = ageH < 1 ? Math.max(1, Math.round(ageH * 60)) + ' min' : ageH.toFixed(1) + ' h';
      box.append(el('div', { class: 'be' }, chip, ' ', el('a', { href: '/item?id=' + s.id, text: s.title }), el('br'),
        el('span', { class: 'bm-meta', text: [age + ' old', s.comments ? s.comments + ' comment' + (s.comments === 1 ? '' : 's') : 'no comments yet', s.points + ' pts', s.ask ? 'Ask HN' : s.show ? 'Show HN' : s.domain].filter(Boolean).join(' · ') })));
    }
    out.push(box);
    out.push(el('div', {}, el('button', { 'data-oppref': '1', text: 'Refresh' }), el('span', { class: 'bm-meta', text: " free; reads HN's public search. It only finds the thread: you write your own words" })));
    return out;
  }

  // ---------------------------------------------------------------- apply
  function clear() {
    $$('.hn-badge,.hn-stub,#hn-sum').forEach((n) => n.remove());
    $$('tr[data-hn-id]').forEach((r) => { r.className = r.className.replace(/\bhn-(k\d|kw|g|seen|hide|muted|mshown|alert)\b/g, '').trim(); r.style.removeProperty('--hn-gc'); });
    $$('tr.comtr').forEach((r) => r.classList.remove('hn-op', 'hn-good', 'hn-collapsed'));
  }

  function apply() {
    if (!S || !S.options) return;
    const o = S.options, v = ++version, type = pageType(), root = document.documentElement;
    starred = new Set();
    for (const g of Object.keys(S.bookmarks || {})) for (const d of Object.keys(S.bookmarks[g])) for (const e of S.bookmarks[g][d]) starred.add(e.type + ':' + e.id);
    clear();
    root.classList.add('hn-on');
    root.className = root.className.replace(/\bhn-(t|pos)-\w+/g, '').trim();
    root.classList.add('hn-pos-' + o.dock);
    if (o.visual.theme !== 'default') root.classList.add('hn-t-' + o.visual.theme);
    root.classList.toggle('hn-wide', o.visual.width === 'wide');
    root.classList.toggle('hn-nofoot', !!o.visual.hideFooter);
    root.classList.toggle('hn-open', !!store.get('hn-open', false));
    const main = $('#hnmain'); if (main) main.style.zoom = String(o.visual.fontScale || 1);
    if (type === 'list') applyList(v);
    if (type === 'item') applyItem(o.discussion);
    renderChrome(type);
    loadOpps(false);
  }

  function applyList(v) {
    const o = S.options.list, ss = stories();
    const seen = new Set(store.get('hn-seen', []));
    // new since my last visit: a visit ends after 30 quiet minutes; a story is new if this browser had not listed it before the
    // current visit began (so the marks survive reloads inside a visit). The very first run only seeds the list: nothing is "new".
    const newIds = new Set();
    if (o.newMarks) {
      const now = Date.now(), vis = store.get('hn-visit', { start: now, last: now }), known = store.get('hn-known', null) || {}, first = !vis.seeded;
      if (now - vis.last > 30 * 60 * 1000) vis.start = now;
      vis.last = now; vis.seeded = true; store.set('hn-visit', vis);
      let changed = false;
      for (const s of ss) { if (known[s.id] === undefined) { known[s.id] = first ? vis.start - 1 : now; changed = true; } if (known[s.id] >= vis.start) newIds.add(s.id); }
      if (changed) { const keep = Object.entries(known).sort((a, b) => b[1] - a[1]).slice(0, 3000); store.set('hn-known', Object.fromEntries(keep)); }
    }
    window.__hnNew = newIds.size;
    const filter = sessionStorage.getItem('hn-filter');
    let alerts = 0, mutedCount = 0;
    const showMuted = sessionStorage.getItem('hn-showmuted') === '1';
    const users = [...new Set(ss.map((s) => s.user).filter(Boolean))];
    const need = users.filter((u) => !karma.has(u));
    if (o.karma.on && need.length) {
      bridge({ type: 'karma', users: need }).then((m) => { for (const u of need) karma.set(u, m[u] || null); if (v === version) apply(); }, () => { for (const u of need) karma.set(u, null); });
    }
    const top = o.karma.topN > 0 ? new Set(users.map((u) => [u, karma.get(u)?.karma || 0]).sort((a, b) => b[1] - a[1]).slice(0, o.karma.topN).map((x) => x[0])) : new Set();
    const watch = new Set((o.karma.watch || []).map((x) => x.toLowerCase()));
    for (const s of ss) {
      const trs = [s.row, s.sub, s.spacer].filter(Boolean);
      for (const r of trs) r.dataset.hnId = s.id;
      const group = groupOf(s);
      s.group = group;
      const line = $('.subline', s.sub || s.row) || s.sub;
      const add = (cls, text, title, css) => { if (line) { const b = el('span', { class: 'hn-badge ' + cls, text, title }); if (css) b.style.background = css; line.append(b); } };
      if (o.karma.on) {
        const k = karma.get(s.user)?.karma;
        const T = o.karma.threshold;
        const tier = k >= T * 10 ? 3 : k >= T * 5 ? 2 : k >= T ? 1 : top.has(s.user) ? 1 : 0;
        const isW = watch.has(s.user.toLowerCase());
        if (isW) { trs.forEach((r) => r.classList.add('hn-kw')); add('hn-bw', '★ ' + s.user + (k ? ' ' + kfmt(k) : ''), 'on your watch-list'); }
        else if (tier) { trs.forEach((r) => r.classList.add('hn-k' + tier)); add('hn-bk', s.user + ' ' + kfmt(k || 0), 'karma ' + (k || '?')); }
      }
      if (String(o.classifier) !== 'off' && group !== 'Others') {
        trs.forEach((r) => { r.classList.add('hn-g'); r.style.setProperty('--hn-gc', colorOf(group)); });
        add('hn-bg', group, 'topic group', colorOf(group));
      }
      if (o.velocity && s.score && s.t) {
        const hrs = (Date.now() - s.t) / 36e5;
        if (hrs > 0.25 && s.score / hrs >= 10) { const rate = s.score / hrs; add(rate >= 40 ? 'hn-bf' : 'hn-bk', '⚡' + Math.round(rate) + '/h', 'points per hour; posted ' + new Date(s.t).toLocaleString()); }
      }
      if (line) line.append(starEl('story', s.id));
      const lower = (a) => (a || []).map((x) => x.toLowerCase()), dom = s.domain.toLowerCase();
      const alertW = (o.alerts && o.alerts.words) || [], mutes = o.mute || {};
      const isAlert = alertW.length > 0 && matcher({ name: '#alert', keywords: alertW }).test(s.title + ' ' + s.domain);
      const muted = !isAlert && (lower(mutes.domains).some((d) => dom === d || dom.endsWith('.' + d)) || lower(mutes.users).includes(s.user.toLowerCase())
        || ((mutes.words || []).length > 0 && matcher({ name: '#mute', keywords: mutes.words }).test(s.title)));
      if (isAlert) { alerts++; trs.forEach((r) => r.classList.add('hn-alert')); add('hn-bn', '🔔', 'matches one of your alert words'); }
      if (muted) { mutedCount++; trs.forEach((r) => r.classList.add(showMuted ? 'hn-mshown' : 'hn-muted')); }
      else if (line) line.append(el('span', { class: 'hn-badge hn-mutebtn', 'data-kind': s.domain ? 'domain' : 'user', 'data-value': s.domain || s.user, text: '⊘', title: 'mute ' + (s.domain ? 'this site' : 'this user') + ' (local; undo in the dock)' }));
      const art = (S.annotations.articles || {})[s.id];
      if (line && s.domain) line.append(art ? el('span', { class: 'hn-badge hn-art', text: '📖 ' + art.minutes + ' min', title: art.tldr })
        : (S.queuedArticles || []).includes(s.id) ? el('span', { class: 'hn-badge hn-art hn-queued', text: '⏳ queued', title: 'asked: tell Claude "handle the HN requests" and the summary appears here' })
        : el('span', { class: 'hn-badge hn-art', 'data-article': s.id, text: '📖', title: 'ask Claude to summarise this article (≈ ' + kfmt(ARTICLE_TOK[S.options.claude.budget] || 1500) + ' tokens)' }));
      const pick = (S.annotations.picks || {})[s.id];
      if (pick) add('hn-pickb', '★ pick', "Claude's pick: " + pick.reason);
      if (newIds.has(s.id)) add('hn-newb', 'NEW', 'not on this page at your last visit');
      if (o.seen && seen.has(s.id)) trs.forEach((r) => r.classList.add('hn-seen'));
      if (filter && filter !== group) trs.forEach((r) => r.classList.add('hn-hide'));
    }
    window.__hnStories = ss; window.__hnAlerts = alerts; window.__hnMuted = mutedCount;
  }

  function applyItem(o) {
    const root = document.documentElement;
    root.classList.toggle('hn-guides', !!o.indentGuides);
    const rows = $$('tr.comtr');
    const op = $('.fatitem .hnuser')?.textContent;
    const level = Number(o.labels) || 0;
    const ann = S.annotations.comments;
    const words = (t) => new Set((t.toLowerCase().match(/[a-z]{4,}/g) || []));
    const seenSets = [];
    const itemId = new URLSearchParams(location.search).get('id');
    const headLine = $('.fatitem .subline');
    if (headLine && itemId) headLine.append(starEl('story', itemId));
    const artI = (S.annotations.articles || {})[itemId];
    if (headLine && itemId && $('.fatitem .sitestr')) headLine.append(artI ? el('span', { class: 'hn-badge hn-art', text: '📖 ' + artI.minutes + ' min', title: artI.tldr })
      : el('span', { class: 'hn-badge hn-art', 'data-article': itemId, text: '📖', title: 'ask Claude to summarise the linked article (≈ ' + kfmt(ARTICLE_TOK[S.options.claude.budget] || 1500) + ' tokens)' }));
    const mutedUsers = (((S.options.list || {}).mute || {}).users || []).map((x) => x.toLowerCase());
    for (const r of rows) {
      r.dataset.hnId = r.id;
      const ch = $('.comhead', r); if (ch) ch.append(starEl('comment', r.id));
      const user = $('.hnuser', r)?.textContent;
      const text = $('.commtext', r)?.innerText || '';
      if (o.highlightOp && op && user === op) r.classList.add('hn-op');
      let label = null, reason = '';
      const a = ann[r.id];
      if (a) { label = a.label; reason = a.reason || ''; }
      if (!label && user && mutedUsers.includes(user.toLowerCase())) { label = 'muted'; reason = 'you muted this user'; }
      if (!label && o.repeats) {
        const w = words(text);
        if (w.size >= 12) {
          const hit = seenSets.find((p) => { let i = 0; for (const x of w) if (p.w.has(x)) i++; return i / (w.size + p.w.size - i) >= 0.6; });
          if (hit) { label = 'repeat'; reason = 'near-duplicate of ' + hit.user + "'s earlier comment"; }
          seenSets.push({ w, user });
        }
      }
      if (label === 'good') { r.classList.add('hn-good'); const h = $('.comhead', r); if (h) h.append(el('span', { class: 'hn-badge hn-bw', text: '★ substantive', title: reason })); }
      const collapse = label === 'muted' || (label === 'attack' && level >= 1) || (label === 'snark' && level >= 2) || ((label === 'lowvalue' || label === 'repeat') && (level >= 3 || (label === 'repeat' && o.repeats)));
      if (collapse) {
        r.classList.add('hn-collapsed');
        const names = { attack: 'personal attack', snark: 'snark', lowvalue: 'low substance', repeat: 'repeat', muted: 'muted user' };
        const stub = el('div', { class: 'hn-stub' }, 'collapsed: ' + names[label] + (reason ? ' – ' + reason : '') + ' ', el('a', { class: 'hn-show', 'data-hn-id': r.id, text: 'show' }));
        const c = $('.comment', r); if (c) c.parentNode.insertBefore(stub, c);
      }
    }
    if (o.collapseDepth > 0 && !window.__hnCollapsed) {
      window.__hnCollapsed = true;
      rows.forEach((r, i) => {
        const d = Number($('td.ind', r)?.getAttribute('indent') || 0), nx = rows[i + 1];
        const tg = $('a.togg', r);
        if (d === o.collapseDepth && tg && tg.textContent.includes('–') && nx && Number($('td.ind', nx)?.getAttribute('indent') || 0) > d) tg.click();
      });
    }
    const sum = S.annotations.summaries[itemId];
    if (sum) {
      const box = el('div', { id: 'hn-sum' }, el('h3', { text: 'Discussion summary' }));
      if (sum.overview) box.append(el('div', { text: sum.overview }));
      const ul = el('ul');
      for (const p of sum.positions || []) {
        const li = el('li', {}, el('b', { text: p.title + ': ' }), p.text + ' ');
        for (const id of p.ids || []) li.append(el('a', { class: 'hn-goto', 'data-id': id, text: '[' + (($('.hnuser', document.getElementById(id) || document.body)?.textContent) || id) + '] ' }));
        ul.append(li);
      }
      box.append(ul);
      if (sum.disagreement) box.append(el('div', {}, el('b', { text: 'Where they disagree: ' }), sum.disagreement));
      if ((sum.best || []).length) { const d = el('div', {}, el('b', { text: 'Worth reading: ' })); for (const id of sum.best) d.append(el('a', { class: 'hn-goto', 'data-id': id, text: ($('.hnuser', document.getElementById(id) || document.body)?.textContent || id) + ' ' })); box.append(d); }
      box.append(el('div', { class: 'm', text: 'Written by Claude from ' + sum.covered + ' of ' + sum.total + ' comments at ' + (sum.ts || '') + '. It can be wrong: check the comments it cites.' }));
      const tree = $('table.comment-tree') || $('tr.comtr')?.closest('table');
      if (tree) tree.parentNode.insertBefore(box, tree);
    }
  }

  // ---------------------------------------------------------------- chrome (foot + dock)
  function costText() {
    const c = S.cost;
    if (!c) return { t: 'AI cost: measuring…', title: '' };
    if (c.error) return { t: 'AI cost: unavailable', title: c.error };
    const star = c.defaulted ? '*' : '';
    return { t: 'AI cost  session $' + c.session.toFixed(2) + star + '  |  group ' + c.group + ' $' + c.groupUsd.toFixed(2) + star, title: (c.defaulted ? '* estimate: default price for ' + c.defaulted + '. ' : '') + 'The newest session of this folder, and every session of its session group ' + c.group + ' (a launch joins a group with --group); transcripts lag a little. Folder total $' + c.usd.toFixed(2) };
  }

  function renderChrome(type) {
    let foot = $('#hn-foot');
    if (!foot) { foot = el('div', { id: 'hn-foot' }, el('span', { class: 'n', text: NOTICE }), el('span', { class: 'c' })); document.body.append(foot); }
    const ct = costText(), c = $('.c', foot); c.textContent = ct.t; c.title = ct.title;
    let rq = $('#hn-req', foot);
    if (!rq) { rq = el('span', { id: 'hn-req', class: 'n' }); foot.insertBefore(rq, $('.c', foot)); }
    rq.textContent = S.requests > 0 ? S.requests + ' request(s) for Claude waiting: tell Claude "handle the HN requests"' : '';
    let tab = $('#hn-tab');
    if (!tab) {
      tab = el('button', { id: 'hn-tab', type: 'button', text: 'HN ⚙' });
      tab.addEventListener('click', () => { const open = !document.documentElement.classList.contains('hn-open'); document.documentElement.classList.toggle('hn-open', open); store.set('hn-open', open); if (open) loadOpps(false); });
      document.body.append(tab);
    }
    tab.textContent = 'HN ⚙' + (window.__hnAlerts ? ' 🔔' + window.__hnAlerts : '') + (window.__hnNew ? ' ✦' + window.__hnNew : '');
    let panel = $('#hn-panel');
    if (!panel) { panel = el('div', { id: 'hn-panel' }); document.body.append(panel); }
    panel.replaceChildren(...buildPanel(type));
  }

  const opt = (path, kind, extra = {}) => ({ path, kind, ...extra });
  function field(label, o, v) {
    const attrs = { 'data-opt': o.path, 'data-kind': o.kind };
    let input;
    if (o.kind === 'bool') input = el('input', { type: 'checkbox', ...attrs, checked: !!v });
    else if (o.kind === 'num') input = el('input', { type: 'number', ...attrs, value: v, style: 'width:70px', step: o.step || 1, min: 0 });
    else if (o.kind === 'list') input = el('input', { type: 'text', ...attrs, value: (v || []).join(', ') });
    else { input = el('select', attrs, ...o.choices.map((c) => el('option', { value: c, text: c, selected: String(c) === String(v) }))); }
    if (o.kind === 'bool') input.checked = !!v;
    return el('label', {}, o.kind === 'bool' ? input : null, label, o.kind === 'bool' ? null : input);
  }

  // The dock shows only what the chosen persona turned on. The persona is picked once (here, or by Claude's first question) and is then fixed.
  function buildPanel(type) {
    const o = S.options, out = [];
    const cat = S.catalog || { modules: {}, personas: {} };
    const has = (m) => (o.modules || []).includes(m);
    if (!o.personaChosen) {
      out.push(el('div', {}, el('b', { text: 'Joint Hacker News' })));
      out.push(el('h4', { text: 'How do you use Hacker News?' }));
      out.push(el('div', { class: 'bm-meta', text: 'Pick once; it cannot be changed afterwards.' }));
      for (const [id, p] of Object.entries(cat.personas)) out.push(el('div', {}, el('button', { 'data-choose': id, text: p.title || id }), el('span', { class: 'bm-meta', text: p.about || '' })));
      return out;
    }
    const pm = cat.personas[o.persona] || {};
    out.push(el('div', {}, el('b', { text: 'Joint Hacker News' }), ' · ' + (pm.title || o.persona)));
    out.push(field('Dock', opt('dock', 'sel', { choices: ['right', 'left', 'top', 'bottom'] }), o.dock));
    if (has('triage')) out.push(field('Token use', opt('claude.budget', 'sel', { choices: ['frugal', 'balanced', 'thorough'] }), o.claude.budget));
    if (has('look')) {
      out.push(el('h4', { text: 'Look' }));
      out.push(field('Theme', opt('visual.theme', 'sel', { choices: ['dark', 'sepia', 'default'] }), o.visual.theme));
      out.push(field('Width', opt('visual.width', 'sel', { choices: ['wide', 'normal'] }), o.visual.width));
      if (type === 'item') {
        const D = o.discussion;
        out.push(field('Indent guides', opt('discussion.indentGuides', 'bool'), D.indentGuides));
        out.push(field('Highlight the poster (OP)', opt('discussion.highlightOp', 'bool'), D.highlightOp));
        out.push(field('Collapse near-duplicates', opt('discussion.repeats', 'bool'), D.repeats));
      }
    }
    if (type === 'list' && has('triage')) {
      const L = o.list;
      out.push(el('h4', { text: 'Stories' }));
      out.push(field('Highlight high-karma submitters', opt('list.karma.on', 'bool'), L.karma.on));
      out.push(field('Velocity (points/hour)', opt('list.velocity', 'bool'), L.velocity));
      out.push(field('Dim stories I opened', opt('list.seen', 'bool'), L.seen));
      out.push(field('Mark stories new since my last visit', opt('list.newMarks', 'bool'), L.newMarks));
      const counts = new Map(); for (const s of window.__hnStories || []) counts.set(s.group, (counts.get(s.group) || 0) + 1);
      const filter = sessionStorage.getItem('hn-filter');
      const chips = el('div');
      for (const name of [...L.groups.map((g) => g.name), 'Others']) {
        const n = counts.get(name) || 0;
        const chip = el('span', { class: 'chip' + (filter === name ? ' on' : ''), 'data-group': name, text: name + ' · ' + n, title: n + ' ' + (n === 1 ? 'story' : 'stories') + ' on this page in "' + name + '"; click to show only these' });
        chip.style.setProperty('--c', colorOf(name)); chips.append(chip);
      }
      out.push(el('h4', { text: 'Topics' }));
      out.push(chips);
      if (o.claude.budget !== 'frugal') out.push(el('div', {}, el('button', { 'data-ask': 'classify', text: 'Ask Claude to classify' }), el('span', { class: 'paid', text: 'reads ≈ ' + kfmt(readEstimate('list')) + ' tokens' })));
    }
    if (type === 'item' && has('triage')) {
      out.push(el('h4', { text: 'Claude on this thread' }));
      out.push(el('div', {}, el('button', { 'data-ask': 'summary', text: 'Summarise' }), el('button', { 'data-ask': 'labels', text: 'Label comments' }), el('span', { class: 'paid', text: 'reads ≈ ' + kfmt(readEstimate('item')) + ' tokens' })));
    }
    if (has('opportunities')) out.push(...opportunitiesBlock());
    if (has('bookmarks')) out.push(...bookmarksBlock());
    if (S.requests > 0) out.push(el('div', { class: 'req', text: S.requests + ' request(s) waiting. Tell Claude: "handle the HN requests".' }));
    return out;
  }

  // ---------------------------------------------------------------- events (delegated; no per-element listeners)
  document.addEventListener('change', async (e) => {
    const x = e.target;
    const bn = x.closest?.('[data-bnote]');
    if (bn) { S.bookmarks = await bridge({ type: 'bookmark', action: 'note', kind: bn.dataset.bt, id: bn.dataset.bid, note: bn.value }); apply(); return; }
    const bt = x.closest?.('[data-btags]');
    if (bt) { S.bookmarks = await bridge({ type: 'bookmark', action: 'tags', kind: bt.dataset.bt, id: bt.dataset.bid, tags: bt.value }); apply(); return; }
    const bm = x.closest?.('[data-bmove]');
    if (bm) { S.bookmarks = await bridge({ type: 'bookmark', action: 'move', kind: bm.dataset.bt, id: bm.dataset.bid, group: bm.value }); apply(); return; }
    if (x.id === 'hn-bsearch') { sessionStorage.setItem('hn-bq', x.value); apply(); return; }
    const t = x.closest?.('[data-opt]'); if (!t) return;
    if (t.dataset.opt === 'claude.budget') { S.options = await bridge({ type: 'budget', name: t.value }); apply(); return; }
    let val;
    if (t.dataset.kind === 'bool') val = t.checked;
    else if (t.dataset.kind === 'num') val = Number(t.value);
    else if (t.dataset.kind === 'list') val = t.value.split(',').map((x) => x.trim()).filter(Boolean);
    else val = isNaN(Number(t.value)) ? t.value : Number(t.value);
    const o = await bridge({ type: 'setOption', path: t.dataset.opt, value: val });
    S.options = o; apply();
  });
  document.addEventListener('click', async (e) => {
    const t = e.target;
    const star = t.closest?.('.hn-star');
    if (star) {
      const kind = star.dataset.kind, id = star.dataset.star, on = starred.has(kind + ':' + id);
      S.bookmarks = await bridge({ type: 'bookmark', action: on ? 'remove' : 'add', kind, id, entry: on ? undefined : entryFor(kind, id) });
      apply(); return;
    }
    const ch = t.closest?.('[data-choose]');
    if (ch) { S.options = await bridge({ type: 'choose', name: ch.dataset.choose }); apply(); return; }
    if (t.closest?.('[data-oppref]')) { loadOpps(true); return; }
    const mb = t.closest?.('.hn-mutebtn');
    if (mb) { S.options = await bridge({ type: 'mute', kind: mb.dataset.kind, value: mb.dataset.value, on: true }); apply(); return; }
    const am = t.closest?.('[data-article]');
    if (am) { const r = await bridge({ type: 'request', kind: 'article', id: am.dataset.article, url: location.href }); S.requests = r.pending; apply(); return; }
    if (t.closest?.('[data-showmuted]')) { sessionStorage.setItem('hn-showmuted', sessionStorage.getItem('hn-showmuted') === '1' ? '0' : '1'); apply(); return; }
    const del = t.closest?.('[data-bdel]');
    if (del) { S.bookmarks = await bridge({ type: 'bookmark', action: 'remove', kind: del.dataset.bt, id: del.dataset.bid }); apply(); return; }
    if (t.closest?.('[data-bmd]')) {
      const btn = t.closest('[data-bmd]');
      try { await navigator.clipboard.writeText(await bridge({ type: 'bookmarkMd' })); btn.textContent = 'Copied'; } catch { btn.textContent = 'Copy blocked by the browser'; }
      return;
    }
    const chip = t.closest?.('.chip');
    if (chip) { const n = chip.dataset.group; if (sessionStorage.getItem('hn-filter') === n) sessionStorage.removeItem('hn-filter'); else sessionStorage.setItem('hn-filter', n); apply(); return; }
    if (t.closest?.('[data-addgroup]')) {
      const name = $('#hn-gname').value.trim(), kw = $('#hn-gkw').value;
      if (name) {
        let bad = '';
        try { S.options = await bridge({ type: 'addGroup', name, keywords: kw }); } catch (err) { bad = String(err.message || err).replace(/^.*Error: /, ''); }
        apply();
        if (bad) { const f = $('#hn-gname'); if (f) { f.value = name; f.title = bad; f.style.outline = '2px solid #e03a2f'; } }
      }
      return;
    }
    const ask = t.closest?.('[data-ask]');
    if (ask) { const r = await bridge({ type: 'request', kind: ask.dataset.ask, url: location.href }); S.requests = r.pending; apply(); return; }
    const show = t.closest?.('.hn-show');
    if (show) { e.preventDefault(); const tr = document.getElementById(show.dataset.hnId); const open = tr.classList.toggle('hn-open'); show.textContent = open ? 'hide' : 'show'; return; }
    const go = t.closest?.('.hn-goto');
    if (go) { const tr = document.getElementById(go.dataset.id); if (tr) { tr.classList.remove('hn-collapsed'); tr.scrollIntoView({ block: 'center' }); tr.classList.add('hn-flash'); setTimeout(() => tr.classList.remove('hn-flash'), 2000); } return; }
    if (S?.options?.list?.seen && pageType() === 'list') {
      const a = t.closest?.('a'), row = a?.closest('tr[data-hn-id]');
      if (a && row && !a.closest('#hn-panel')) {
        const id = row.dataset.hnId, seen = store.get('hn-seen', []);
        if (!seen.includes(id)) { seen.push(id); store.set('hn-seen', seen.slice(-3000)); }
      }
    }
  }, true);
  document.addEventListener('mouseover', async (e) => {
    const a = e.target.closest?.('a.hnuser');
    if (!a || !S?.options?.discussion?.authorCard || a.dataset.hnCard) return;
    a.dataset.hnCard = '1';
    const u = a.textContent;
    try {
      if (!karma.has(u)) karma.set(u, (await bridge({ type: 'karma', users: [u] }))[u] || null);
      const k = karma.get(u);
      if (k) a.title = u + ': karma ' + k.karma + ', joined ' + new Date(k.created * 1000).toLocaleDateString();
    } catch { /* no bridge: no card */ }
  }, true);

  // ---------------------------------------------------------------- for the daemon
  window.__hnApply = (state) => { S = state; apply(); };
  window.__hnRead = (lim) => {
    const type = pageType(), L = lim || {};
    if (type === 'list') return { type, url: location.href, stories: stories().map((s) => ({ id: s.id, title: s.title, domain: s.domain, user: s.user, points: s.score, comments: s.comments, group: S ? groupOf(s) : null })) };
    if (type === 'item') {
      // what Claude reads costs tokens: the user's token-use level limits depth, count and length (0 depth = no limit)
      const all = $$('tr.comtr'), depthOf = (r) => Number($('td.ind', r)?.getAttribute('indent') || 0);
      const cap = L.maxComments || 400, chars = L.commentChars || 700;
      const rows = (L.maxDepth > 0 ? all.filter((r) => depthOf(r) < L.maxDepth) : all).slice(0, cap);
      return {
        type, url: location.href, id: new URLSearchParams(location.search).get('id'),
        title: $('.fatitem .titleline')?.innerText || '', op: $('.fatitem .hnuser')?.textContent || '', body: ($('.fatitem .toptext')?.innerText || '').slice(0, chars * 4),
        total: all.length, included: rows.length, truncated: rows.length < all.length, limits: { maxDepth: L.maxDepth || 0, maxComments: cap, commentChars: chars },
        comments: rows.map((r) => ({ id: r.id, depth: depthOf(r), user: $('.hnuser', r)?.textContent || '', text: ($('.commtext', r)?.innerText || '').slice(0, chars) })),
      };
    }
    return { type, url: location.href };
  };

  const boot = () => {
    document.documentElement.append(el('style', { id: 'hn-style', text: CSS }));
    // the logged-in name is in HN's own header (a#me): read it, never ask for or touch a login
    const detectMe = () => {
      const me = $('#me')?.textContent?.trim();
      if (me && S.options.identity.user !== me) bridge({ type: 'setOption', path: 'identity.user', value: me }).then((o) => { S.options = o; apply(); }, () => {});
    };
    bridge({ type: 'hello' }).then((st) => { S = st; apply(); detectMe(); }, () => { /* daemon gone: leave the page as HN made it */ });
  };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot, { once: true }); else boot();
})();
