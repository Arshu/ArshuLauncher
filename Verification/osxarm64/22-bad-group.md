# 22 A bad session group name is refused

## What it does
The person gives a group name with a character a group name may not hold (`/`). The launcher says which names are allowed (1-40 ASCII letters, digits, spaces or . _ & + -) and ends before opening anything.

## Who gets it
Anyone naming a session group.

## Cost
None: the launcher stops before it starts anything.

## How to verify
1. Open a terminal in the clone.
2. Run `dotnet run csharp/Tools/ArshuLauncher/runJointLauncher.cs -- --harness <claude|codex|pi> --group "bad/name"`.
3. Expect `Error: --group 'bad/name' is not a session group name: 1-40 ASCII letters, digits, spaces or . _ & + -.`

## What must NOT happen
Nothing starts and nothing is written to the launch ledger.

## Evidence
- claude: [types](media/launcher-claude-10-types.gif) / [output](media/launcher-claude-10-output.gif) / [png](media/launcher-claude-10.png)
- codex: [types](media/launcher-codex-10-types.gif) / [output](media/launcher-codex-10-output.gif) / [png](media/launcher-codex-10.png)
- pi: [types](media/launcher-pi-10-types.gif) / [output](media/launcher-pi-10-output.gif) / [png](media/launcher-pi-10.png)

Recording: generated 2026-11-20 by runToolsLocal from CI/launcherScenarios.json (scenario T10): one film of the run, cut between the marks of the steps; only an agent whose every step passed is written.

## Automated check
runToolsLocal `launcher-published-terminal --tool runJointLauncher --scenario T10`: the command typed in a plain terminal; the terminal shows the expected text (that nothing starts is read from the screenshot and the film, not asserted).
