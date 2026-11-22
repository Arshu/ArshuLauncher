# 21 Usage: --help

## What it does
The person asks for the usage. The launcher prints the command line, what each option does and where it keeps its settings, and ends without opening a terminal, a browser or a harness.

## Who gets it
Anyone learning the options.

## Cost
None: the launcher stops before it starts anything.

## How to verify
1. Open a terminal in the clone.
2. Run `dotnet run csharp/Tools/ArshuLauncher/runJointLauncher.cs -- --help`.
3. Expect the usage text, starting with `Usage: dotnet run ...runJointLauncher.cs`.

## What must NOT happen
No browser or harness starts.

## Evidence
- claude: [types](media/launcher-claude-09-types.gif) / [output](media/launcher-claude-09-output.gif) / [png](media/launcher-claude-09.png)
- codex: [types](media/launcher-codex-09-types.gif) / [output](media/launcher-codex-09-output.gif) / [png](media/launcher-codex-09.png)
- pi: [types](media/launcher-pi-09-types.gif) / [output](media/launcher-pi-09-output.gif) / [png](media/launcher-pi-09.png)

Recording: generated 2026-11-20 by runToolsLocal from CI/launcherScenarios.json (scenario T9): one film of the run, cut between the marks of the steps; only an agent whose every step passed is written.

## Automated check
runToolsLocal `launcher-published-terminal --tool runJointLauncher --scenario T9`: the command typed in a plain terminal; the terminal shows the expected text (that nothing starts is read from the screenshot and the film, not asserted).
