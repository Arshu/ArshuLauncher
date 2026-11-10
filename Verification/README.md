# Verification: Hacker News skill

The central index for checking, by hand and by recording, what the `joint-hackernews` skill does. It is read-only: nothing is ever written to Hacker News. Persona, token level and dock side are asked once at the first run; the persona cannot be changed afterwards. Media sits in [media/](media/).

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
| 10 | Contributors see early, quiet threads and unanswered Ask HN from HN's public search, free of Claude tokens. | [10](10-worth-joining.md) | [gif](media/hn-10-joining.gif) / [mp4](media/hn-10-joining.mp4) / [png](media/hn-10-joining.png) | recording pending; test not run | <date> |
| 11 | The AI cost bar is always visible (session and group, * = estimate) with the notice that AI-written posts are not welcome; the skill never posts, votes, replies or submits. | [11](11-cost-and-guard.md) | [gif](media/hn-11-cost.gif) / [mp4](media/hn-11-cost.mp4) / [png](media/hn-11-cost.png) | recorded (Reader); test not run | 2026-11-10 |

Which persona gets what (from modules.json): Reader = stories, look, bookmarks; Contributor = stories, look, worth joining; Maker = look, bookmarks; Researcher = bookmarks, stories, look. Features that cost Claude tokens: 04 (classify only), 05, 07, 08. The rest is free.

## How these were produced

- The recordings were made by `CI/runToolsRecord` in the ArshuTools repository: features only, scripted at human speed.
- The full behaviour tests are run by the `runToolsLocal` case `hackernews-features` in the same repository; each document names its section under "Automated check".
- The last verification date above and the Result and Date columns are filled in after each run; "not run" means it was not run, not that it passed.
