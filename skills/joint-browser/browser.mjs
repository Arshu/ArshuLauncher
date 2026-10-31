#!/usr/bin/env node
// joint-browser: one Playwright action per call on the joint session's shared browser (CDP, default
// http://127.0.0.1:9222). It attaches to the open window and never launches or closes a browser. Playwright comes from
// the project (node_modules/playwright or playwright-core under the current folder), else from ~/.joint-browser/node,
// where `node browser.mjs setup` installs playwright-core with npm. Every action prints a JSON report: what it did, the
// URL before and after, and every console error, page error, dialog and failed request it raised; --expect-* asserts
// the action's effect. Exit 0 = ok and clean, 2 = ok but something unexpected was raised, 1 = failed.
import { createRequire } from 'node:module';
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

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

// The connection is dropped by exiting, never by browser.close(): the shared window stays as it is.
main().then((code) => process.exit(code), (error) => {
  print({ ok: false, error: String(error.message ?? error).split('\n')[0] });
  process.exit(1);
});
