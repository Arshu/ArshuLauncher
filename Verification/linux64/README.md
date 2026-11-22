# Verification

How the tool is verified: one row per scenario, written by `runToolsLocal` from `CI/launcherScenarios.json` when a scenario ran with a recording. Each platform that ran
keeps its own folder (`win64`, `linux64`, `osxarm64`) with the same table, the scenario pages, the recordings (`media/`) and the seconds each scenario took (`timings.json`).

| # | Task | Document | Media | Result | Date |
|---|------|----------|-------|--------|------|
| 12 | Started without a skill, the launcher puts its terminal (claude, codex or pi starts in it) and the shared browser side by side and the assistant lists what to do (Hacker News, browser, doctor, install, something else). Claude shows an arrow-key menu; codex and pi print a list and wait for typed input. | [12 linux64](12-launcher-without-skill.md) | | not recorded on this platform | |
| 13 | Choosing `joint-launcher-doctor` in the menu runs the launcher's doctor and explains each problem with its fix; nothing is changed. | [13 linux64](13-menu-doctor.md) | | not recorded on this platform | |
| 14 | Choosing `joint-launcher-install` checks what the session still needs (terminal, browser, Node.js, Playwright) and asks before installing anything. | [14 linux64](14-menu-install.md) | | not recorded on this platform | |
| 15 | Choosing `joint-browser` attaches Playwright to the shared browser; "open news.ycombinator.com" then opens Hacker News in the shared window. | [15](15-menu-joint-browser.md) | [media](#media-15) | recorded (claude, codex, pi); asserted by runToolsLocal | 2026-11-20 |
| 16 | Started from inside a harness (claude, codex or pi typed in a plain terminal, then asked to run the launcher), the launcher puts the harness's terminal on the left and the shared browser on the right. | [16 linux64](16-launcher-in-harness.md) | | not recorded on this platform | |
| 17 | `--skill joint-browser --skillargs "open news.ycombinator.com"` starts the harness on that skill with that request; no menu, and Hacker News opens in the shared window. | [17 linux64](17-skill-and-args.md) | | not recorded on this platform | |
| 18 | `--position bottom:top --ratio 1:2` places the terminal and the browser with the terminal at the bottom and the browser above it, in the ratio 1:2. | [18 linux64](18-position-bottom-top.md) | | not recorded on this platform | |
| 19 | `--position top:bottom --ratio 1:4` places the terminal and the browser with the terminal at the top and the browser below it, in the ratio 1:4. | [19 linux64](19-position-top-bottom.md) | | not recorded on this platform | |
| 20 | `--group "Sprint 42"` tracks the launch in that session group; the cost bar names the group. | [20](20-group.md) | [media](#media-20) | recorded (claude, codex, pi); asserted by runToolsLocal | 2026-11-20 |
| 21 | `--help` prints the usage and starts nothing. | [21 linux64](21-usage.md) | | not recorded on this platform | |
| 22 | `--group "bad/name"` is refused with the rule for group names; nothing starts. | [22 linux64](22-bad-group.md) | | not recorded on this platform | |
| 23 | `--bogus` is refused by name and the usage follows; nothing starts. | [23 linux64](23-unknown-argument.md) | | not recorded on this platform | |
| 24 | `--skill` with no name is refused and the usage follows; nothing starts. | [24 linux64](24-skill-no-value.md) | | not recorded on this platform | |
| 25 | `--doctor --harness <harness>` checks that harness and the rest, and opens no window. | [25 linux64](25-doctor-harness.md) | | not recorded on this platform | |
| 26 | Inside a harness, `--harness <other>` starts that other harness in a terminal of its own, placed beside the shared browser; the harness the person is in stays where it is. | [26 linux64](26-harness-inside-harness.md) | | not recorded on this platform | |
| 27 | Inside a harness, `--skill` (and `--skillargs`, `--group`) has nothing to start or record: the launcher says so and opens nothing. | [27 linux64](27-skill-inside-harness.md) | | not recorded on this platform | |

## Media

<!-- media:start -->

### Media 15

| OS | Harness | Links |
|----|---------|-------|
| **linux64** | `claude` | [types](media/launcher-claude-04-types.gif) / [opens](media/launcher-claude-04-opens.gif) / [menu](media/launcher-claude-04-menu.gif) / [opens-page](media/launcher-claude-04-opens-page.gif) / [png](media/launcher-claude-04.png) |
| **linux64** | `codex` | [types](media/launcher-codex-04-types.gif) / [opens](media/launcher-codex-04-opens.gif) / [menu](media/launcher-codex-04-menu.gif) / [opens-page](media/launcher-codex-04-opens-page.gif) / [png](media/launcher-codex-04.png) |
| **linux64** | `pi` | [types](media/launcher-pi-04-types.gif) / [opens](media/launcher-pi-04-opens.gif) / [menu](media/launcher-pi-04-menu.gif) / [opens-page](media/launcher-pi-04-opens-page.gif) / [png](media/launcher-pi-04.png) |

### Media 20

| OS | Harness | Links |
|----|---------|-------|
| **linux64** | `claude` | [types](media/launcher-claude-08-types.gif) / [group](media/launcher-claude-08-group.gif) / [png](media/launcher-claude-08.png) |
| **linux64** | `codex` | [types](media/launcher-codex-08-types.gif) / [group](media/launcher-codex-08-group.gif) / [png](media/launcher-codex-08.png) |
| **linux64** | `pi` | [types](media/launcher-pi-08-types.gif) / [group](media/launcher-pi-08-group.gif) / [png](media/launcher-pi-08.png) |

<!-- media:end -->

## Timings

Seconds each scenario took (the sum of its steps), per platform and harness; written by runToolsLocal after every run.

<!-- timings:start -->

| Scenario | linux64 claude | linux64 codex | linux64 pi |
|---|---|---|---|
| T4 | 62.6 s | 79.2 s | 55.2 s |
| T8 | 104.7 s | 147.7 s | 111.8 s |
| **total** | 2.8 min (2 of 2) | 3.8 min (2 of 2) | 2.8 min (2 of 2) |
| measured | 2026-11-20 sri-MS-A2 | 2026-11-20 sri-MS-A2 | 2026-11-20 sri-MS-A2 |

<!-- timings:end -->
