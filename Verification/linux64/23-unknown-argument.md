# 23 An unknown option is refused

## What it does
The person mistypes an option. The launcher names the argument it does not know, prints the usage, and ends before opening anything.

## Who gets it
Anyone who mistypes an option.

## Cost
None: the launcher stops before it starts anything.

## How to verify
1. Open a terminal in the clone.
2. Run `dotnet run csharp/Tools/ArshuLauncher/runJointLauncher.cs -- --bogus`.
3. Expect `Error: unknown argument '--bogus'.` followed by the usage.

## What must NOT happen
Nothing starts; the unknown option is not ignored.

## Evidence
- claude: [types](media/launcher-claude-11-types.gif) / [output](media/launcher-claude-11-output.gif) / [png](media/launcher-claude-11.png)
- codex: [types](media/launcher-codex-11-types.gif) / [output](media/launcher-codex-11-output.gif) / [png](media/launcher-codex-11.png)
- pi: [types](media/launcher-pi-11-types.gif) / [output](media/launcher-pi-11-output.gif) / [png](media/launcher-pi-11.png)

Recording: generated 2026-11-20 by runToolsLocal from CI/launcherScenarios.json (scenario T11): one film of the run, cut between the marks of the steps; only an agent whose every step passed is written.

## Automated check
runToolsLocal `launcher-published-terminal --tool runJointLauncher --scenario T11`: the command typed in a plain terminal; the terminal shows the expected text (that nothing starts is read from the screenshot and the film, not asserted).
