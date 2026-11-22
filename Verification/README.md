# Verification: ArshuLauncher

The central index for what was checked, by hand and by recording. Two parts: the Hacker News skill ([below](#hacker-news-skill)), and the launcher scenarios on each platform ([win64, linux64, osxarm64](#launcher-scenarios-on-each-platform-win64-linux64-osxarm64)).

## Hacker News skill

The index for checking, by hand and by recording, what the `joint-hackernews` skill does. It is read-only: nothing is ever written to Hacker News. Persona, token level and dock side are asked once at the first run; the persona cannot be changed afterwards. Media sits in [media/](media/).

Last recording: 2026-11-10 (Reader persona). Automated test case `hackernews-features`: not run yet.

| # | Task being verified | Document | Media | Result | Date |
|---|---|---|---|---|---|
| 01 | Launching with the skill asks three questions (persona, token use, dock side) and opens Hacker News with the dock. The persona is then fixed and a second choice is refused. | [01](01-persona-and-launch.md) | [launch gif](media/hn-01-launch.gif) / [mp4](media/hn-01-launch.mp4) / [png](media/hn-01-launch.png), [persona gif](media/hn-01-persona.gif) / [mp4](media/hn-01-persona.mp4) / [png](media/hn-01-persona.png), [opens gif](media/hn-01-opens.gif) / [mp4](media/hn-01-opens.mp4) / [png](media/hn-01-opens.png) | recorded (Reader); test not run | 2026-11-10 |
| 02 | The HN ⚙ tab opens a dock that can sit on any of four sides and shows only the sections of the chosen persona. | [02](02-dock.md) | [gif](media/hn-02-dock.gif) / [mp4](media/hn-02-dock.mp4) / [png](media/hn-02-dock.png) | recorded (Reader); test not run | 2026-11-10 |
| 03 | The front page marks high-karma submitters, stories gaining points per hour, stories new since the last visit, and dims stories already opened. | [03](03-front-page-signals.md) | [gif](media/hn-03-signals.gif) / [mp4](media/hn-03-signals.mp4) / [png](media/hn-03-signals.png) | recorded (Reader); test not run | 2026-11-10 |
| 04 | Stories get free keyword topic badges; the dock chips count them and filter the page. Claude can classify the rest on request. | [04](04-topic-groups.md) | [groups gif](media/hn-04-groups.gif) / [mp4](media/hn-04-groups.mp4) / [png](media/hn-04-groups.png), [classify gif](media/hn-04-classify.gif) / [mp4](media/hn-04-classify.mp4) / [png](media/hn-04-classify.png) | recorded (Reader); test not run | 2026-11-10 |
| 05 | The 📖 on a story queues an article summary ("⏳ queued"); after Claude reads the linked article the row shows "📖 N min" with the summary as tooltip. | [05](05-article-summary.md) | [gif](media/hn-05-article.gif) / [mp4](media/hn-05-article.mp4) / [png](media/hn-05-article.png) | recorded (Reader); test not run | 2026-11-10 |
| 06 | Discussion pages get indent guides, a highlighted poster, near-duplicate comments folded to a stub, and dark or sepia themes. | [06](06-thread-reading.md) | [gif](media/hn-06-thread.gif) / [mp4](media/hn-06-thread.mp4) / [png](media/hn-06-thread.png) | recorded (Reader); test not run | 2026-11-10 |
| 07 | Summarise produces a box of positions citing comments, with jump links and an honest "written by Claude from X of Y comments" line. | [07](07-thread-summary.md) | [gif](media/hn-07-summary.gif) / [mp4](media/hn-07-summary.mp4) / [png](media/hn-07-summary.png) | recorded (Reader); test not run | 2026-11-10 |
| 08 | Label comments folds attacks, snark and low-value comments to a stub with a "show" link, according to the sensitivity level. | [08](08-label-comments.md) | [gif](media/hn-08-labels.gif) / [mp4](media/hn-08-labels.mp4) / [png](media/hn-08-labels.png) | recorded (Reader); test not run | 2026-11-10 |
| 09 | The ☆ saves stories and comments to a local file, grouped by topic and day, with notes, tags, search and Copy as Markdown. | [09](09-bookmarks.md) | [gif](media/hn-09-bookmarks.gif) / [mp4](media/hn-09-bookmarks.mp4) / [png](media/hn-09-bookmarks.png) | recorded (Reader); test not run | 2026-11-10 |
| 10 | Contributors see early, quiet threads and unanswered Ask HN from HN's public search, free of Claude tokens. | [10](10-worth-joining.md) | [gif](media/hn-10-joining.gif) / [mp4](media/hn-10-joining.mp4) / [png](media/hn-10-joining.png) | recorded (Contributor); test not run | 2026-11-10 |
| 11 | The AI cost bar is always visible (session and group, * = estimate) with the notice that AI-written posts are not welcome; the skill never posts, votes, replies or submits. | [11](11-cost-and-guard.md) | [gif](media/hn-11-cost.gif) / [mp4](media/hn-11-cost.mp4) / [png](media/hn-11-cost.png) | recorded (Reader); test not run | 2026-11-10 |
| 12 | Started without a skill, the launcher opens the terminal and the shared browser and the assistant asks what to do (install, doctor, browser, something else). | [12](12-launcher-without-skill.md) | opening [gif](media/launcher-01-opens.gif) / [mp4](media/launcher-01-opens.mp4) / [png](media/launcher-01-opens.png), choices [gif](media/launcher-01-choices.gif) / [mp4](media/launcher-01-choices.mp4) / [png](media/launcher-01-choices.png) | recorded; test not written | 2026-11-10 |

Which persona gets what (from modules.json): Reader = stories, look, bookmarks; Contributor = stories, look, worth joining; Maker = look, bookmarks; Researcher = bookmarks, stories, look. Features that cost Claude tokens: 04 (classify only), 05, 07, 08. The rest is free.

## How these were produced

- The recordings were made by `CI/runToolsRecord` in the ArshuTools repository: features only, scripted at human speed.
- The full behaviour tests are run by the `runToolsLocal` case `hackernews-features` in the same repository; each document names its section under "Automated check".
- The last verification date above and the Result and Date columns are filled in after each run; "not run" means it was not run, not that it passed.

## Launcher scenarios on each platform (win64, linux64, osxarm64)

How the tool is verified: one row per scenario, written by `runToolsLocal` from `CI/launcherScenarios.json` when a scenario ran with a recording. Each platform that ran
keeps its own folder (`win64`, `linux64`, `osxarm64`) with the same table, the scenario pages, the recordings (`media/`) and the seconds each scenario took (`timings.json`).

| # | Task | Document | Media | Result | Date |
|---|------|----------|-------|--------|------|
| 12 | Started without a skill, the launcher puts its terminal (claude, codex or pi starts in it) and the shared browser side by side and the assistant lists what to do (Hacker News, browser, doctor, install, something else). Claude shows an arrow-key menu; codex and pi print a list and wait for typed input. | [12 win64](win64/12-launcher-without-skill.md) | | not recorded | |
| 13 | Choosing `joint-launcher-doctor` in the menu runs the launcher's doctor and explains each problem with its fix; nothing is changed. | [13 win64](win64/13-menu-doctor.md) | | not recorded | |
| 14 | Choosing `joint-launcher-install` checks what the session still needs (terminal, browser, Node.js, Playwright) and asks before installing anything. | [14 win64](win64/14-menu-install.md) | | not recorded | |
| 15 | Choosing `joint-browser` attaches Playwright to the shared browser; "open news.ycombinator.com" then opens Hacker News in the shared window. | [15 linux64](linux64/15-menu-joint-browser.md) | [media](#media-15) | linux64: recorded (claude, codex, pi); asserted by runToolsLocal | linux64: 2026-11-20 |
| 16 | Started from inside a harness (claude, codex or pi typed in a plain terminal, then asked to run the launcher), the launcher puts the harness's terminal on the left and the shared browser on the right. | [16 linux64](linux64/16-launcher-in-harness.md), [16 osxarm64](osxarm64/16-launcher-in-harness.md), [16 win64](win64/16-launcher-in-harness.md) | | not recorded | |
| 17 | `--skill joint-browser --skillargs "open news.ycombinator.com"` starts the harness on that skill with that request; no menu, and Hacker News opens in the shared window. | [17 osxarm64](osxarm64/17-skill-and-args.md) | | not recorded | |
| 18 | `--position bottom:top --ratio 1:2` places the terminal and the browser with the terminal at the bottom and the browser above it, in the ratio 1:2. | [18 win64](win64/18-position-bottom-top.md) | | not recorded | |
| 19 | `--position top:bottom --ratio 1:4` places the terminal and the browser with the terminal at the top and the browser below it, in the ratio 1:4. | [19 win64](win64/19-position-top-bottom.md) | | not recorded | |
| 20 | `--group "Sprint 42"` tracks the launch in that session group; the cost bar names the group. | [20 linux64](linux64/20-group.md) | [media](#media-20) | linux64: recorded (claude, codex, pi); asserted by runToolsLocal | linux64: 2026-11-20 |
| 21 | `--help` prints the usage and starts nothing. | [21 win64](win64/21-usage.md) | | not recorded | |
| 22 | `--group "bad/name"` is refused with the rule for group names; nothing starts. | [22 win64](win64/22-bad-group.md) | | not recorded | |
| 23 | `--bogus` is refused by name and the usage follows; nothing starts. | [23 win64](win64/23-unknown-argument.md) | | not recorded | |
| 24 | `--skill` with no name is refused and the usage follows; nothing starts. | [24 win64](win64/24-skill-no-value.md) | | not recorded | |
| 25 | `--doctor --harness <harness>` checks that harness and the rest, and opens no window. | [25 win64](win64/25-doctor-harness.md) | | not recorded | |
| 26 | Inside a harness, `--harness <other>` starts that other harness in a terminal of its own, placed beside the shared browser; the harness the person is in stays where it is. | [26 linux64](linux64/26-harness-inside-harness.md), [26 osxarm64](osxarm64/26-harness-inside-harness.md), [26 win64](win64/26-harness-inside-harness.md) | | not recorded | |
| 27 | Inside a harness, `--skill` (and `--skillargs`, `--group`) has nothing to start or record: the launcher says so and opens nothing. | [27 linux64](linux64/27-skill-inside-harness.md), [27 osxarm64](osxarm64/27-skill-inside-harness.md), [27 win64](win64/27-skill-inside-harness.md) | | not recorded | |

### Media

<!-- media:start -->

#### Media 15

| OS | Harness | Links |
|----|---------|-------|
| **linux64** | `claude` | [types](linux64/media/launcher-claude-04-types.gif) / [opens](linux64/media/launcher-claude-04-opens.gif) / [menu](linux64/media/launcher-claude-04-menu.gif) / [opens-page](linux64/media/launcher-claude-04-opens-page.gif) / [png](linux64/media/launcher-claude-04.png) |
| **linux64** | `codex` | [types](linux64/media/launcher-codex-04-types.gif) / [opens](linux64/media/launcher-codex-04-opens.gif) / [menu](linux64/media/launcher-codex-04-menu.gif) / [opens-page](linux64/media/launcher-codex-04-opens-page.gif) / [png](linux64/media/launcher-codex-04.png) |
| **linux64** | `pi` | [types](linux64/media/launcher-pi-04-types.gif) / [opens](linux64/media/launcher-pi-04-opens.gif) / [menu](linux64/media/launcher-pi-04-menu.gif) / [opens-page](linux64/media/launcher-pi-04-opens-page.gif) / [png](linux64/media/launcher-pi-04.png) |

#### Media 20

| OS | Harness | Links |
|----|---------|-------|
| **linux64** | `claude` | [types](linux64/media/launcher-claude-08-types.gif) / [group](linux64/media/launcher-claude-08-group.gif) / [png](linux64/media/launcher-claude-08.png) |
| **linux64** | `codex` | [types](linux64/media/launcher-codex-08-types.gif) / [group](linux64/media/launcher-codex-08-group.gif) / [png](linux64/media/launcher-codex-08.png) |
| **linux64** | `pi` | [types](linux64/media/launcher-pi-08-types.gif) / [group](linux64/media/launcher-pi-08-group.gif) / [png](linux64/media/launcher-pi-08.png) |

<!-- media:end -->

### Timings

Seconds each scenario took (the sum of its steps), per platform and harness; written by runToolsLocal after every run.

<!-- timings:start -->

| Scenario | linux64 claude | linux64 codex | linux64 pi | osxarm64 claude | osxarm64 codex | osxarm64 pi | win64 claude | win64 codex | win64 pi |
|---|---|---|---|---|---|---|---|---|---|
| T1 | not measured | not measured | not measured | not measured | not measured | not measured | 40.0 s | 31.0 s | 22.8 s |
| T2 | not measured | not measured | not measured | not measured | not measured | not measured | 40.1 s | 66.8 s | 46.5 s |
| T3 | not measured | not measured | not measured | not measured | not measured | not measured | 44.0 s | 50.4 s | 39.6 s |
| T4 | 62.6 s | 79.2 s | 55.2 s | not measured | not measured | not measured | 58.1 s | 62.1 s | 49.1 s |
| T5 | not measured | not measured | not measured | 84.6 s | 47.2 s | 40.5 s | 34.7 s | 33.4 s | 30.7 s |
| T6 | not measured | not measured | not measured | not measured | not measured | not measured | 22.7 s | 30.3 s | 20.6 s |
| T7 | not measured | not measured | not measured | not measured | not measured | not measured | 27.2 s | 28.0 s | 20.8 s |
| T8 | 104.7 s | 147.7 s | 111.8 s | not measured | not measured | not measured | 96.7 s | 109.3 s | 40.7 s |
| T9 | not measured | not measured | not measured | not measured | not measured | not measured | 10.9 s | 11.0 s | 10.9 s |
| T10 | not measured | not measured | not measured | not measured | not measured | not measured | 12.3 s | 12.2 s | 12.1 s |
| T11 | not measured | not measured | not measured | not measured | not measured | not measured | 10.9 s | 10.9 s | 10.9 s |
| T12 | not measured | not measured | not measured | not measured | not measured | not measured | 10.9 s | 10.9 s | 11.0 s |
| T13 | not measured | not measured | not measured | not measured | not measured | not measured | 13.0 s | 13.0 s | 12.8 s |
| HN | not measured | not measured | not measured | 334.8 s | 174.0 s FAIL | 362.2 s | 389.1 s | 340.7 s | 448.5 s |
| HNA | not measured | not measured | not measured | 188.7 s | 175.1 s FAIL | 172.2 s | 153.9 s | 198.0 s | 142.2 s |
| HNC | not measured | not measured | not measured | 66.3 s | 95.8 s | 54.1 s | 48.5 s | 73.6 s | 38.4 s |
| **total** | 2.8 min (2 of 16) | 3.8 min (2 of 16) | 2.8 min (2 of 16) | 11.2 min (4 of 16) | 8.2 min (4 of 16) | 10.5 min (4 of 16) | 16.9 min (16 of 16) | 18.0 min (16 of 16) | 16.0 min (16 of 16) |
| measured | 2026-11-20 sri-MS-A2 | 2026-11-20 sri-MS-A2 | 2026-11-20 sri-MS-A2 | 2026-11-20 mini | 2026-11-20 mini | 2026-11-20 mini | 2026-11-20 DESKTOP-QFJ19UG | 2026-11-20 DESKTOP-QFJ19UG | 2026-11-20 DESKTOP-QFJ19UG |

<!-- timings:end -->

The recordings are GIFs and screenshots only: the MP4 files stay in the ArshuToolsTest clone to keep this repository small.
