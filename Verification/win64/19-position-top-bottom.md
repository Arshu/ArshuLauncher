# 19 Layout: terminal above, browser below

## What it does
The person gives the layout on the command line. The terminal and the shared browser are placed with the terminal at the top and the browser below it, in the ratio 1:4, and the list of choices appears in the terminal.

## Who gets it
Anyone who wants the terminal and the browser laid out differently from the default.

## Cost
None: the launcher only places two windows.

## How to verify
1. Run `dotnet run csharp/Tools/ArshuLauncher/runJointLauncher.cs -- --harness <claude|codex|pi> --position top:bottom --ratio 1:4` in a terminal opened in the clone.
2. Expect the terminal and the browser with the terminal at the top and the browser below it, in the ratio 1:4.
3. Expect the list of choices in the terminal; a terminal one fifth of the screen high shows only its last lines, which end with "an option above, or describe what you would like to work on".

## What must NOT happen
The two windows do not overlap; the launcher does not fall back to the default layout.

## Evidence
- claude: [types](media/launcher-claude-07-types.gif) / [placed](media/launcher-claude-07-placed.gif) / [png](media/launcher-claude-07.png)
- codex: [types](media/launcher-codex-07-types.gif) / [placed](media/launcher-codex-07-placed.gif) / [png](media/launcher-codex-07.png)
- pi: [types](media/launcher-pi-07-types.gif) / [placed](media/launcher-pi-07-placed.gif) / [png](media/launcher-pi-07.png)

Recording: generated 2026-11-20 by runToolsLocal from CI/launcherScenarios.json (scenario T7): one film of the run, cut between the marks of the steps; only an agent whose every step passed is written.

## Automated check
runToolsLocal `launcher-published-terminal --tool runJointLauncher --scenario T7`: the terminal's and the browser's rectangles measured against the screen for top:bottom at 1:4. For codex the choices are read from codex's own transcript (its picker call and answer): in a five-row window codex draws only its composer and footer, so its terminal cannot show them; the placement is measured the same way for every agent.
