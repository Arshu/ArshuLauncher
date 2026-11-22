# 20 A session group: the cost bar names it

## What it does
The person names a session group on the command line and has the cost bar started. The launch is recorded under that group; the bar and the assistant name it, so every session of the group is totalled together.

## Who gets it
Anyone who wants the AI cost of several launches added up under one name.

## Cost
None: naming a group costs nothing; the cost bar only reads what the sessions already spent.

## How to verify
1. Run `dotnet run csharp/Tools/ArshuLauncher/runJointLauncher.cs -- --harness <claude|codex|pi> --group "Sprint 42" --skill joint-browser --skillargs "open news.ycombinator.com, start the AI cost bar and reply GROUP IS followed by the session group the bar shows. The bar re-reads every 60 seconds, so if it shows Default wait for it to refresh and read it again"` in a terminal opened in the clone.
2. Expect Hacker News to load in the shared browser window.
3. Expect the cost bar at the bottom of the page and the assistant to reply `GROUP IS Sprint 42`; the bar re-reads the cost every 60 s, so a launch's first minute may still show the group of the session before it (`Default`).

## What must NOT happen
The group name is not saved: the next launch without --group is tracked in Default.

## Evidence
- claude: [types](media/launcher-claude-08-types.gif) / [group](media/launcher-claude-08-group.gif) / [png](media/launcher-claude-08.png)
- codex: [types](media/launcher-codex-08-types.gif) / [group](media/launcher-codex-08-group.gif) / [png](media/launcher-codex-08.png)
- pi: [types](media/launcher-pi-08-types.gif) / [group](media/launcher-pi-08-group.gif) / [png](media/launcher-pi-08.png)

Recording: generated 2026-11-20 by runToolsLocal from CI/launcherScenarios.json (scenario T8): one film of the run, cut between the marks of the steps; only an agent whose every step passed is written.

## Automated check
runToolsLocal `launcher-published-terminal --tool runJointLauncher --scenario T8`: the layout, and the reply `GROUP IS Sprint 42` in the terminal (the typed command holds `Sprint 42` too, so the reply is matched by its fixed words; the bar is in a closed shadow root, so its text cannot be read from the page).
