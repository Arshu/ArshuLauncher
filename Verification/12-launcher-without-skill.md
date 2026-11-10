# 12 Launcher without a skill

## What it does
Started with no skill named (and `DefaultSkill` empty in `runJointLauncher.json`), the launcher opens the terminal with the assistant and a shared browser beside it, and the assistant asks what to do: install the prerequisites, run the doctor, drive the shared browser, or something else. Skills configured in `runJointLauncher.json` are listed first when their files exist.

## Who gets it
Everyone; it is the launcher's own default (`joint-launcher`), not a persona feature.

## Cost
The one question the assistant asks is a normal short Claude turn; the browser and launch cost nothing.

## How to verify
1. Make sure `DefaultSkill` is empty in `runJointLauncher.json` (the launcher writes the last `--skill` you used there).
2. Run `dotnet run csharp/Tools/ArshuLauncher/runJointLauncher.cs -- --harness claude --newterminal` from the clone.
3. Expect a terminal on the left and a browser window on the right ("Joint session ... DevTools protocol on 127.0.0.1:9222").
4. Expect the assistant to read `joint-launcher` and ask a numbered question that includes `joint-launcher-install`, `joint-launcher-doctor`, `joint-browser` and "Something else".

## What must NOT happen
No skill starts by itself, nothing is installed before you answer, and nothing is written to any website.

## Evidence
- Opening: [gif](media/launcher-01-opens.gif) / [mp4](media/launcher-01-opens.mp4) / [png](media/launcher-01-opens.png)
- The choices: [gif](media/launcher-01-choices.gif) / [mp4](media/launcher-01-choices.mp4) / [png](media/launcher-01-choices.png)

Recording: recorded 2026-11-10 by runToolsRecord (CI/Recordings/launcher); the automated test has not been written yet.

## Automated check
None yet (listed in the ArshuTools BACKLOG).
