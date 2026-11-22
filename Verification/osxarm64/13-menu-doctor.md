# 13 Menu: the launcher's doctor

## What it does
From the launcher's list of choices the person picks `joint-launcher-doctor` (claude: the entry is chosen in its menu; codex and pi: its name is typed). The assistant runs the launcher's doctor and reports every check with the fix for each problem. The doctor only reports: nothing is installed, started or changed.

## Who gets it
Everyone; the launcher's own troubleshooting skill.

## Cost
One assistant turn that runs the doctor and writes its report; the doctor itself costs nothing.

## How to verify
1. Make sure `DefaultSkill` is empty in `runJointLauncher.json` (the launcher writes the last `--skill` you used there).
2. Run `dotnet run csharp/Tools/ArshuLauncher/runJointLauncher.cs -- --harness <claude|codex|pi>` in a terminal opened in the clone and wait for the list of choices.
3. Choose `joint-launcher-doctor`.
4. Expect a report of the checks (terminal, browser, Node.js, Playwright, port 9222) with a fix for each problem, and no change to the machine.

## What must NOT happen
Nothing is installed or changed; the assistant does not close or kill a browser.

## Evidence
- claude: [types](media/launcher-claude-02-types.gif) / [opens](media/launcher-claude-02-opens.gif) / [menu](media/launcher-claude-02-menu.gif) / [doctor](media/launcher-claude-02-doctor.gif) / [png](media/launcher-claude-02.png)
- codex: [types](media/launcher-codex-02-types.gif) / [opens](media/launcher-codex-02-opens.gif) / [menu](media/launcher-codex-02-menu.gif) / [doctor](media/launcher-codex-02-doctor.gif) / [png](media/launcher-codex-02.png)
- pi: [types](media/launcher-pi-02-types.gif) / [opens](media/launcher-pi-02-opens.gif) / [menu](media/launcher-pi-02-menu.gif) / [doctor](media/launcher-pi-02-doctor.gif) / [png](media/launcher-pi-02.png)

Recording: generated 2026-11-20 by runToolsLocal from CI/launcherScenarios.json (scenario T2): one film of the run, cut between the marks of the steps; only an agent whose every step passed is written.

## Automated check
runToolsLocal `launcher-published-terminal --tool runJointLauncher --scenario T2`: the layout, the session file, the menu entry chosen by its name, the assistant's reply written and quiet for 10 s (its wording is not asserted).
