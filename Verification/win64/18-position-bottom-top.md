# 18 Layout: terminal below, browser above

## What it does
The person gives the layout on the command line. The terminal and the shared browser are placed with the terminal at the bottom and the browser above it, in the ratio 1:2, and the list of choices appears in the terminal.

## Who gets it
Anyone who wants the terminal and the browser laid out differently from the default.

## Cost
None: the launcher only places two windows.

## How to verify
1. Run `dotnet run csharp/Tools/ArshuLauncher/runJointLauncher.cs -- --harness <claude|codex|pi> --position bottom:top --ratio 1:2` in a terminal opened in the clone.
2. Expect the terminal and the browser with the terminal at the bottom and the browser above it, in the ratio 1:2.
3. Expect the list of choices in the terminal.

## What must NOT happen
The two windows do not overlap; the launcher does not fall back to the default layout.

## Evidence
- claude: [types](media/launcher-claude-06-types.gif) / [placed](media/launcher-claude-06-placed.gif) / [png](media/launcher-claude-06.png)
- codex: [types](media/launcher-codex-06-types.gif) / [placed](media/launcher-codex-06-placed.gif) / [png](media/launcher-codex-06.png)
- pi: [types](media/launcher-pi-06-types.gif) / [placed](media/launcher-pi-06-placed.gif) / [png](media/launcher-pi-06.png)

Recording: generated 2026-11-20 by runToolsLocal from CI/launcherScenarios.json (scenario T6): one film of the run, cut between the marks of the steps; only an agent whose every step passed is written.

## Automated check
runToolsLocal `launcher-published-terminal --tool runJointLauncher --scenario T6`: the terminal's and the browser's rectangles measured against the screen for bottom:top at 1:2.
