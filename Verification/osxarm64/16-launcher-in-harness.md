# 16 Launcher started from inside a harness

## What it does
A person opens a plain terminal in the launcher repository, types the harness (claude, codex or pi), and asks it to run `dotnet run csharp/Tools/ArshuLauncher/runJointLauncher.cs`. The launcher moves that terminal to the left third of the work area and opens the shared browser on the right two thirds ("Joint session ... DevTools protocol on 127.0.0.1:9222", with the AI cost bar). The harness keeps running in its own terminal; the launcher starts no second one.

## Who gets it
Everyone who starts the launcher from inside a harness; the start from a plain terminal (rows 12-15) is the other way in.

## Cost
One short assistant turn to run the command; the launch and the browser cost nothing.

## How to verify
1. Open a terminal in the ArshuLauncher clone and type `claude` (or `codex`, `pi`); wait for the harness screen.
2. Ask it: `Run dotnet run csharp/Tools/ArshuLauncher/runJointLauncher.cs`.
3. Expect the harness's terminal on the left edge of the work area and the browser on the right, filling the rest; the browser stays open and shows its page.

## What must NOT happen
The browser covering the whole screen ("the browser alone"), the harness's terminal staying where it was, or a second harness terminal opening.

## Evidence
- claude: [types](media/launcher-claude-05-types.gif) / [instruction](media/launcher-claude-05-instruction.gif) / [placed](media/launcher-claude-05-placed.gif) / [png](media/launcher-claude-05.png)
- codex: [types](media/launcher-codex-05-types.gif) / [instruction](media/launcher-codex-05-instruction.gif) / [placed](media/launcher-codex-05-placed.gif) / [png](media/launcher-codex-05.png)
- pi: [types](media/launcher-pi-05-types.gif) / [instruction](media/launcher-pi-05-instruction.gif) / [placed](media/launcher-pi-05-placed.gif) / [png](media/launcher-pi-05.png)

Recording: generated 2026-11-20 by runToolsLocal from CI/launcherScenarios.json (scenario H1): one film of the run, cut between the marks of the steps; only an agent whose every step passed is written.

## Automated check
runToolsLocal `launcher-published-harness --tool runJointLauncher`: the terminal and the browser placement (24 px slack), the session file, the DevTools port, both windows drawn, the AI cost bar shown with a Session and a Group value (0.00 is fine), the end of the session.
