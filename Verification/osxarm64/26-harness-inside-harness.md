# 26 Another harness named inside a harness

## What it does
A person runs a harness (claude, codex or pi) and asks it to run the launcher with `--harness` naming a different harness. The launcher does not take over the terminal the person is in: it opens a terminal of its own, starts the named harness there, places it on the left and opens the shared browser on the right.

## Who gets it
Anyone who wants a second harness (for example codex beside the claude they are using) on the same shared browser.

## Cost
The named harness's own usage; the launcher costs nothing.

## How to verify
1. Open a terminal in the ArshuLauncher clone and type `claude` (or `codex`, `pi`); wait for the harness screen.
2. Ask it: `Run dotnet run csharp/Tools/ArshuLauncher/runJointLauncher.cs -- --harness <another harness>`.
3. Expect a new terminal running the other harness on the left and the browser on the right; the terminal you were in is unchanged.

## What must NOT happen
The harness you are in being replaced or moved; no browser opening.

## Evidence
- claude: [types](media/launcher-claude-14-types.gif) / [instruction](media/launcher-claude-14-instruction.gif) / [placed](media/launcher-claude-14-placed.gif) / [png](media/launcher-claude-14.png)
- codex: [types](media/launcher-codex-14-types.gif) / [instruction](media/launcher-codex-14-instruction.gif) / [placed](media/launcher-codex-14-placed.gif) / [png](media/launcher-codex-14.png)
- pi: [types](media/launcher-pi-14-types.gif) / [instruction](media/launcher-pi-14-instruction.gif) / [placed](media/launcher-pi-14-placed.gif) / [png](media/launcher-pi-14.png)

Recording: generated 2026-11-20 by runToolsLocal from CI/launcherScenarios.json (scenario H2): one film of the run, cut between the marks of the steps; only an agent whose every step passed is written.

## Automated check
runToolsLocal `launcher-published-harness --tool runJointLauncher --scenario H2`: the session file says skipHarness false, the DevTools port answers, a terminal of its own opens and shows the other harness's screen, it and the browser are placed left:right, and the AI cost bar is shown with a Session and a Group value (the unchanged first terminal is read from the film, not asserted).
