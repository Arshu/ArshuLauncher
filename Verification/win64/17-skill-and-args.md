# 17 A skill and its arguments on the command line

## What it does
The person gives the skill and its request on the command line. The launcher opens the terminal and the browser, starts the harness on `joint-browser` with `open news.ycombinator.com` as its first message, and the page opens in the shared window with no list of choices in between.

## Who gets it
Anyone who knows what they want to start and does not need the list of choices.

## Cost
One short assistant turn; the browser costs nothing.

## How to verify
1. Run `dotnet run csharp/Tools/ArshuLauncher/runJointLauncher.cs -- --harness <claude|codex|pi> --skill joint-browser --skillargs "open news.ycombinator.com"` in a terminal opened in the clone.
2. Expect no list of choices: the harness starts on the skill and acts on the request.
3. Expect Hacker News to load in the shared browser window, to the right of the terminal.

## What must NOT happen
No list of choices is shown; no second browser opens; nothing is written to the website.

## Evidence
- claude: [types](media/launcher-claude-05-types.gif) / [opens-page](media/launcher-claude-05-opens-page.gif) / [png](media/launcher-claude-05.png)
- codex: [types](media/launcher-codex-05-types.gif) / [opens-page](media/launcher-codex-05-opens-page.gif) / [png](media/launcher-codex-05.png)
- pi: [types](media/launcher-pi-05-types.gif) / [opens-page](media/launcher-pi-05-opens-page.gif) / [png](media/launcher-pi-05.png)

Recording: generated 2026-11-20 by runToolsLocal from CI/launcherScenarios.json (scenario T5): one film of the run, cut between the marks of the steps; only an agent whose every step passed is written.

## Automated check
runToolsLocal `launcher-published-terminal --tool runJointLauncher --scenario T5`: the layout, and a page of news.ycombinator.com in the shared browser (read over its DevTools port).
