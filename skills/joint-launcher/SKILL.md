---
name: joint-launcher
description: Start a joint session by asking the user what to do - load one of the skills configured in runJointLauncher.json, install the launcher's prerequisites (joint-launcher-install) or troubleshoot them (joint-launcher-doctor) - then do it. The joint-session launcher (runJointLauncher.cs) starts the harness on this skill when no skill is given.
---

# joint-launcher

The user opened a joint session with the launcher (`{{launch}}`, run from this folder): this
terminal plus a shared browser whose DevTools protocol answers on http://127.0.0.1:9222. Any browser tool that can
attach to a running Chromium over CDP (e.g. a Playwright MCP server started with `--cdp-endpoint
http://127.0.0.1:9222`) acts on the window the user watches. The launcher records the session in
`<local app data>/joint-launcher/session.json` (`cdp`, `harness`, `skill`). No skill was named, so ask what to do,
then do it.

## 1. Ask

Read `runJointLauncher.json` in the working directory, the folder this harness was started in (not this skill's own
folder under the harness's skills folder; it may not exist yet: then no skills are configured). Its `Skills` object
maps each configured skill's name to its SKILL.md path (relative to that working directory unless absolute). A skill is available
only while that file exists: check each one, do not assume.

Ask the user to choose (with your question tool if you have one, else as a numbered list): one option per available
skill (the description from its SKILL.md frontmatter), then `joint-launcher-install` (install or update the
prerequisites), `joint-launcher-doctor` (check the prerequisites and troubleshoot a launch that went wrong),
`joint-browser` (drive the shared browser with Playwright, or review a page or flow together) and "something else"
(the user says what to work on). Name configured skills whose file is missing
in the question text as "not available", never as options.

## 2. Do it

- A configured skill: read its SKILL.md and follow it from its start; it asks for its own arguments.
- `joint-browser`, `joint-launcher-install` or `joint-launcher-doctor`: skills beside this one in the same skills
  folder; load the chosen one and follow it.
- Something else: do what the user asks, using the shared browser on 127.0.0.1:9222 where it helps.

To skip this question next time, the user passes `--skill <name>` to the launcher (it then becomes `DefaultSkill` in
`runJointLauncher.json`).

## From a harness already running

Run from a harness, the launcher opens no terminal and no new harness: `{{launch --harness claude}}` (or `codex`, `pi`,
which also installs `joint-browser` for it) opens the shared browser beside this terminal and returns. `--position` and
`--ratio` place them; when this terminal's window cannot be moved, the browser takes the whole work area. Run the
launcher directly, not through a `.bat` wrapper: its `pause` would hang your shell. Then drive it with `joint-browser`.
