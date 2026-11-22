# 14 Menu: the launcher's install

## What it does
From the launcher's list of choices the person picks `joint-launcher-install`. The assistant checks what is missing on the machine (terminal, browser, Node.js, Playwright, ffmpeg) and asks before every install; when nothing is missing it says so.

## Who gets it
Everyone setting the launcher up on a new machine.

## Cost
One assistant turn for the check; installs, if the person agrees, are the machine's own.

## How to verify
1. Make sure `DefaultSkill` is empty in `runJointLauncher.json` (the launcher writes the last `--skill` you used there).
2. Run `dotnet run csharp/Tools/ArshuLauncher/runJointLauncher.cs -- --harness <claude|codex|pi>` in a terminal opened in the clone and wait for the list of choices.
3. Choose `joint-launcher-install`.
4. Expect a list of what is present and what is missing, and a question before anything is installed.

## What must NOT happen
Nothing is installed without the person answering yes.

## Evidence
- claude: [types](media/launcher-claude-03-types.gif) / [opens](media/launcher-claude-03-opens.gif) / [menu](media/launcher-claude-03-menu.gif) / [install](media/launcher-claude-03-install.gif) / [png](media/launcher-claude-03.png)
- codex: [types](media/launcher-codex-03-types.gif) / [opens](media/launcher-codex-03-opens.gif) / [menu](media/launcher-codex-03-menu.gif) / [install](media/launcher-codex-03-install.gif) / [png](media/launcher-codex-03.png)
- pi: [types](media/launcher-pi-03-types.gif) / [opens](media/launcher-pi-03-opens.gif) / [menu](media/launcher-pi-03-menu.gif) / [install](media/launcher-pi-03-install.gif) / [png](media/launcher-pi-03.png)

Recording: generated 2026-11-20 by runToolsLocal from CI/launcherScenarios.json (scenario T3): one film of the run, cut between the marks of the steps; only an agent whose every step passed is written.

## Automated check
runToolsLocal `launcher-published-terminal --tool runJointLauncher --scenario T3`: the layout, the session file, the menu entry chosen by its name, the assistant's reply written and quiet for 10 s (its wording is not asserted).
