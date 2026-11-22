# 25 The doctor for one harness

## What it does
The person asks the doctor about one harness. It lists each prerequisite as `[ok]`, `[missing]` or `[optional]`, names the harness's skills folder, and ends without opening a terminal, a browser or a harness.

## Who gets it
Anyone setting the launcher up or troubleshooting it.

## Cost
None: the launcher stops before it starts anything.

## How to verify
1. Open a terminal in the clone.
2. Run `dotnet run csharp/Tools/ArshuLauncher/runJointLauncher.cs -- --doctor --harness <claude|codex|pi>`.
3. Expect a list of `[ok]` lines, among them `<harness> on PATH`.

## What must NOT happen
No window opens; nothing is installed.

## Evidence
- claude: [types](media/launcher-claude-13-types.gif) / [output](media/launcher-claude-13-output.gif) / [png](media/launcher-claude-13.png)
- codex: [types](media/launcher-codex-13-types.gif) / [output](media/launcher-codex-13-output.gif) / [png](media/launcher-codex-13.png)
- pi: [types](media/launcher-pi-13-types.gif) / [output](media/launcher-pi-13-output.gif) / [png](media/launcher-pi-13.png)

Recording: generated 2026-11-20 by runToolsLocal from CI/launcherScenarios.json (scenario T13): one film of the run, cut between the marks of the steps; only an agent whose every step passed is written.

## Automated check
runToolsLocal `launcher-published-terminal --tool runJointLauncher --scenario T13`: the command typed in a plain terminal; the terminal shows the expected text (that nothing starts is read from the screenshot and the film, not asserted).
