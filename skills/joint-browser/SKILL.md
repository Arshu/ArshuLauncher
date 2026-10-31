---
name: joint-browser
description: Drive the joint session's shared browser (Chromium on DevTools port 127.0.0.1:9222) with Playwright together with the user - open pages, click, fill, read and check what changed - for any web app in any project, including a joint review of a flow (walk it step by step, validate every action, discuss, fix the code, re-run). Use for "drive the browser", "joint browser", "review this page/flow with me", "joint review" when no project-specific review skill applies.
---

# joint-browser

A joint session (started by `runJointLauncher.cs` or `runJointSession.cs`) is this terminal plus a shared
Chromium-based browser the user watches, with its DevTools protocol on http://127.0.0.1:9222. You drive that window
with Playwright through `browser.mjs`, the helper beside this SKILL.md (in `.claude/skills/joint-browser/`,
`.agents/skills/joint-browser/` or `.pi/skills/joint-browser/`): one action per call, each printing a JSON report. It
attaches to the open window and never launches or closes a browser.

## 0. Ready

1. `node <this folder>/browser.mjs status`. It needs Node.js 20 or later, as Playwright does (`node --version`;
   install from nodejs.org, `winget install --id OpenJS.NodeJS.LTS -e`, `brew install node`, or on Debian/Ubuntu
   NodeSource's LTS: `curl -fsSL https://deb.nodesource.com/setup_lts.x | sudo -E bash - && sudo apt install -y
   nodejs`; the distro's own `nodejs` can be older, e.g. 18 on Ubuntu 24.04). Ask before installing.
2. `"playwright": null`: the helper uses the project's own Playwright when the project has one (`playwright` or
   `playwright-core` in its `node_modules`); otherwise ask the user, then run `node <this folder>/browser.mjs setup`
   (installs `playwright-core` with npm into `~/.joint-browser/node`; no browser download, the shared one is used).
3. `"browser": null`: no joint session browser is open; ask the user to start the launcher (or to close whatever else
   holds port 9222).
4. Tell the user it is ready and ask what to do, unless the arguments already say it.

## 1. Drive

Do what the user asks, at human pace, in the shared window: they watch every step. The commands
(`node browser.mjs help` prints all of them with the options):

| Command | Does |
|---|---|
| `pages` | the open tabs; `--page <index>` picks one for any command (default: the newest tab) |
| `goto <url>` / `back` / `forward` / `reload` | navigate |
| `click`, `dblclick`, `hover`, `check`, `uncheck <selector>` | act on an element |
| `fill <selector> <text>` / `type <selector> <text>` / `press <key> [selector]` / `select <selector> <value>` / `upload <selector> <file>` | input |
| `snapshot [selector]` | url, title and the ARIA tree: roles, names, states; read it before choosing a selector |
| `text`, `attr`, `count <selector>` / `eval <expression>` | read the page |
| `screenshot <file> [selector]` (`--full`) | a PNG to look at; save it outside the project (a temp or scratch folder) |
| `wait <selector> [state]` / `console` | wait for an element / listen for console output |
| `newtab [url]` / `closetab` | tabs (the last tab, the shared window, is never closed) |

Selectors are Playwright's: prefer role and text (`"role=button[name='Sign in']"`, `"text=Forgot password"`) over
CSS, as a user names things; take the names from `snapshot`. Quote each argument for your shell.

**Every action is validated.** A click that did nothing must not pass silently:
- Before acting on an element, make sure it is the one meant (`snapshot` / `text`), and the state it acts on is
  what it should be (a selection, a checked box).
- Give the action the effect it should have, so the helper asserts it: `--expect-url <part>`, `--expect-text
  <text>`, `--expect-visible <selector>`, `--expect-hidden <selector>`. `"ok": false` with `expect_failed` = the
  effect did not happen.
- Read `"clean"`: console errors and warnings, page errors, unexpected dialogs (dismissed) and failed or 4xx/5xx
  requests raised by the action are listed; exit code 2. A dialog that IS the effect (a confirm before a delete) is
  accepted with `--accept-dialog [text]`, reported as `dialogs_accepted`, and fails the action when none appears. Each is a finding to show the user, unless the
  user agreed it is expected (`--allow <regex>` then stops counting it).

## 2. Joint review

When the user wants to review a page or a flow with you (a sign-up, a checkout, a settings page):

1. **Agree the scope**: the app's URL, the flow and its steps, the account or data to use. If the app is not running,
   ask how it starts. Look for its own run command in the project (README, AGENTS.md / CLAUDE.md, package.json
   scripts, a Makefile) and start it only when the user agrees, in the background, with its output in a log file.
   If the project has its own review skill or test harness for this, say so and offer it; this skill needs none.
2. **Walk the flow one step at a time**: say what the step will do, do it with its `--expect-*`, report what changed
   (`snapshot` / `text`, a screenshot when the look matters) and what was raised. Pause for the user's comments
   before the next step; they may also click in the window themselves, so `snapshot` again before acting after a
   pause.
3. **Keep the findings** as you go, in a short list in the conversation (step, what happened, what was expected,
   evidence: the report lines, a screenshot path). Each finding is an implementation or a test question: ask
   which.
4. **Fix with the user**: change the project's code only when the user agrees, rebuild or restart the app the
   project's way, then re-run the step (and the steps before it when they set it up) and show the result.
5. **Keep what passed**: when the user wants it, turn the agreed steps into a test in the project's own test setup
   (its Playwright tests if it has them: the same selectors and expectations), never a test that only passes here.

Nothing else: no server or test starts unless the user agrees, and the shared window stays open for the next step.
