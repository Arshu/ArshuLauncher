# 27 A skill named inside a harness is refused

## What it does
A person asks the harness they are in to run the launcher with `--skill`. The launcher refuses with `Error: this runs inside <harness>, which is the session's harness, so --skill, --skillargs and --group have nothing to start or record; load the skill here, or pass --newterminal for a terminal of its own.` and opens no browser.

## Who gets it
Anyone who expects `--skill` to start a skill inside a running harness.

## Cost
None: the launcher stops before it opens anything.

## How to verify
1. Open a terminal in the ArshuLauncher clone and type `claude` (or `codex`, `pi`); wait for the harness screen.
2. Ask it: `Run dotnet run csharp/Tools/ArshuLauncher/runJointLauncher.cs -- --skill joint-browser and reply with its exact output, word for word, in a code block`.
3. Expect the error above, and no browser window.

## What must NOT happen
A browser or a second terminal opening; the skill being loaded silently.

## Evidence
- claude: [types](media/launcher-claude-15-types.gif) / [instruction](media/launcher-claude-15-instruction.gif) / [refused](media/launcher-claude-15-refused.gif) / [png](media/launcher-claude-15.png)
- codex: [types](media/launcher-codex-15-types.gif) / [instruction](media/launcher-codex-15-instruction.gif) / [refused](media/launcher-codex-15-refused.gif) / [png](media/launcher-codex-15.png)
- pi: [types](media/launcher-pi-15-types.gif) / [instruction](media/launcher-pi-15-instruction.gif) / [refused](media/launcher-pi-15-refused.gif) / [png](media/launcher-pi-15.png)

Recording: generated 2026-11-20 by runToolsLocal from CI/launcherScenarios.json (scenario H3): one film of the run, cut between the marks of the steps; only an agent whose every step passed is written.

## Automated check
runToolsLocal `launcher-published-terminal --tool runJointLauncher --scenario H3`: the harness's terminal shows 'nothing to start or record' and the DevTools port does not answer.
