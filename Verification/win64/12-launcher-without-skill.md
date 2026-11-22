# 12 Launcher without a skill

## What it does
Started with no skill named (and `DefaultSkill` empty in `runJointLauncher.json`), the launcher moves the terminal it was started in to the left, starts the harness in it, and opens the shared browser on the right ("Joint session ... DevTools protocol on 127.0.0.1:9222"), and the assistant asks what to do: Hacker News, the browser skill, the doctor, install, or something else. Done for the three harnesses.

## Who gets it
Everyone; it is the launcher's own default (`joint-launcher`), not a persona feature.

## Cost
One short assistant turn to list the choices; the browser and the launch cost nothing.

## How to verify
1. Make sure `DefaultSkill` is empty in `runJointLauncher.json` (the launcher writes the last `--skill` you used there).
2. Run `dotnet run csharp/Tools/ArshuLauncher/runJointLauncher.cs -- --harness <claude|codex|pi>` in a terminal opened in the clone.
3. Expect that same terminal to move to the left, the browser to open on the right, the harness to start in the terminal, and a list of choices within about 30 s (codex and pi take 15-25 s, claude 10-15 s).
4. claude shows an arrow-key menu; codex and pi print a numbered list and wait for you to type the answer. The wording of the list is written by the assistant and differs from run to run.

## What must NOT happen
No skill starts by itself, nothing is installed before you answer, and nothing is written to any website.

## Evidence
- claude: [types](media/launcher-claude-01-types.gif) / [opens](media/launcher-claude-01-opens.gif) / [choices](media/launcher-claude-01-choices.gif) / [png](media/launcher-claude-01.png)
- codex: [types](media/launcher-codex-01-types.gif) / [opens](media/launcher-codex-01-opens.gif) / [choices](media/launcher-codex-01-choices.gif) / [png](media/launcher-codex-01.png)
- pi: [types](media/launcher-pi-01-types.gif) / [opens](media/launcher-pi-01-opens.gif) / [choices](media/launcher-pi-01-choices.gif) / [png](media/launcher-pi-01.png)

Recording: generated 2026-11-20 by runToolsLocal from CI/launcherScenarios.json (scenario T1): one film of the run, cut between the marks of the steps; only an agent whose every step passed is written.

## Automated check
runToolsLocal `launcher-published-terminal --tool runJointLauncher`: the terminal and the browser placement, the session file, the DevTools port, the assistant's list on screen (its wording is not asserted: the assistant writes it), the end of the session.
