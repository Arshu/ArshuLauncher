# 24 --skill without a name is refused

## What it does
The person gives `--skill` and forgets the skill's name. The launcher says `--skill` needs a value, prints the usage and ends before opening anything.

## Who gets it
Anyone starting a skill from the command line.

## Cost
None: the launcher stops before it starts anything.

## How to verify
1. Open a terminal in the clone.
2. Run `dotnet run csharp/Tools/ArshuLauncher/runJointLauncher.cs -- --skill`.
3. Expect `Error: --skill needs a value.` followed by the usage.

## What must NOT happen
Nothing starts; no skill is chosen for the person.

## Evidence
- claude: [types](media/launcher-claude-12-types.gif) / [output](media/launcher-claude-12-output.gif) / [png](media/launcher-claude-12.png)
- codex: [types](media/launcher-codex-12-types.gif) / [output](media/launcher-codex-12-output.gif) / [png](media/launcher-codex-12.png)
- pi: [types](media/launcher-pi-12-types.gif) / [output](media/launcher-pi-12-output.gif) / [png](media/launcher-pi-12.png)

Recording: generated 2026-11-20 by runToolsLocal from CI/launcherScenarios.json (scenario T12): one film of the run, cut between the marks of the steps; only an agent whose every step passed is written.

## Automated check
runToolsLocal `launcher-published-terminal --tool runJointLauncher --scenario T12`: the command typed in a plain terminal; the terminal shows the expected text (that nothing starts is read from the screenshot and the film, not asserted).
