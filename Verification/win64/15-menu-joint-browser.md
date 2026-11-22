# 15 Menu: the joint browser

## What it does
From the launcher's list of choices the person picks `joint-browser`; the assistant attaches to the shared browser on port 9222. The person then asks "open news.ycombinator.com" and the page opens in the shared window, to the right of the terminal.

## Who gets it
Everyone who wants the assistant to drive the shared browser.

## Cost
Two short assistant turns; the browser costs nothing.

## How to verify
1. Make sure `DefaultSkill` is empty in `runJointLauncher.json` (the launcher writes the last `--skill` you used there).
2. Run `dotnet run csharp/Tools/ArshuLauncher/runJointLauncher.cs -- --harness <claude|codex|pi>` in a terminal opened in the clone and wait for the list of choices.
3. Choose `joint-browser`.
4. Ask: `open news.ycombinator.com`.
5. Expect Hacker News to load in the shared browser window.

## What must NOT happen
No second browser opens; nothing is written to the website.

## Evidence
- claude: [types](media/launcher-claude-04-types.gif) / [opens](media/launcher-claude-04-opens.gif) / [menu](media/launcher-claude-04-menu.gif) / [opens-page](media/launcher-claude-04-opens-page.gif) / [png](media/launcher-claude-04.png)
- codex: [types](media/launcher-codex-04-types.gif) / [opens](media/launcher-codex-04-opens.gif) / [menu](media/launcher-codex-04-menu.gif) / [opens-page](media/launcher-codex-04-opens-page.gif) / [png](media/launcher-codex-04.png)
- pi: [types](media/launcher-pi-04-types.gif) / [opens](media/launcher-pi-04-opens.gif) / [menu](media/launcher-pi-04-menu.gif) / [opens-page](media/launcher-pi-04-opens-page.gif) / [png](media/launcher-pi-04.png)

Recording: generated 2026-11-20 by runToolsLocal from CI/launcherScenarios.json (scenario T4): one film of the run, cut between the marks of the steps; only an agent whose every step passed is written.

## Automated check
runToolsLocal `launcher-published-terminal --tool runJointLauncher --scenario T4`: the layout, the menu entry chosen by its name, and a page of news.ycombinator.com in the shared browser (read over its DevTools port).
