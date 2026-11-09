# runJointCost - what the harness sessions of a folder cost

`runJointCost.cs` lists **every** harness session (Claude Code, codex, pi) whose working folder is the given folder,
whoever started it (the joint tools or you, by hand), as a markdown table: newest first, one row per (session, model)
so a session that switched models shows several rows, then a **TOTAL** row. One file, no package; it needs .NET 10+ and,
for Claude and codex costs, **Node** (it runs the pinned `ccusage@20.0.17` through `bunx`, else `npx -y`).

```
dotnet run csharp/Tools/ArshuCost/runJointCost.cs -- [--folder <path>] [--harness claude,codex,pi] [--json] [--no-snapshot]
dotnet run csharp/Tools/ArshuCost/runJointCost.cs -- --groups [--json]
dotnet run csharp/Tools/ArshuCost/runJointCost.cs -- --watch [--folder <path>] --harness <one> [--interval-minutes 5] [--idle-minutes 10]
```

`--folder` defaults to the current folder. `--json` prints the rows and the total as JSON instead of the table.

## Columns

| Column | Meaning |
|---|---|
| Session id | the harness's own id: Claude's uuid file name, codex's rollout uuid, pi's uuid |
| Harness, Model | `claude`/`codex`/`pi` and the model; `-` is a session that recorded no usage |
| Input, Output, Cache read, Cache write | tokens. codex Input is the uncached part (codex counts cached input inside input) |
| Thinking | thinking (Claude) / reasoning (codex, pi) tokens, a subset of Output; blank when the harness does not record them |
| Approx cost (USD) | see Cost; `unpriced` when ccusage has no price for the model |
| Start, End | first and last record time of the session (UTC) |
| Started by | `Launcher (tool)` or `Session (tool)` when the session matches a launch ledger entry, else `manual` |
| Group | the session group the session belongs to (see Session groups) |
| Source | `live` (read from the transcript this run) or `snapshot only` (taken from `usage.jsonl`) |

## Session groups

Cost is tracked per **session group**, so several launches of one piece of work add up. The launcher and session tools take
`--group <name>` (1-40 letters, digits, spaces or `. _ & + -`) and write it in the launch's ledger line; a launch that names none is
in `Default`. A session belongs to the group of the ledger entry it matches (see the ledger below); a session that matches none
(started by hand, or its ledger line is gone) is in `Default`, and so is a ledger line written before groups existed. `--groups` lists
each group with its session count and approximate cost (`--json` too).

`--json` adds a `group` field to every row, the top-level `group` (`name`, `sessions`, `costUsd`: the newest session's group and its
total) and `session` (the newest session: `harness`, `sessionId`, `group`, `first`, `costUsd`), which is what the joint browser's bottom
bar shows: `AI cost  session $x | group G $y`.

## Sources

- Claude Code: `~/.claude/projects/<folder, every non-alphanumeric character as ->/<uuid>.jsonl` (assistant records: `message.model`,
  `message.usage`; a response streamed as several records counts once, the last). Sessions whose `cwd` differs are skipped.
- codex: `~/.codex/sessions/YYYY/MM/DD/rollout-*-<uuid>.jsonl`; `session_meta.cwd` selects the folder, the growth of the cumulative
  `token_count` counter is charged to the model of the current `turn_context`.
- pi: `~/.pi/agent/sessions/--<folder>--/<ts>_<uuid>.jsonl`; assistant `message.usage` with `cost.total`.

An unreadable log, a malformed line (the error names the file and line), a missing `ccusage`/Node or a ccusage run that fails is an
error that stops the run; nothing is skipped silently.

## Cost (approximate)

pi: its recorded `cost.total` (source `pi-recorded`). Claude and codex: `ccusage session --json --breakdown --offline` (source
`ccusage`). Claude has a per-model cost; ccusage prices a codex session as a whole, so that cost sits on the session's largest model
row and the others show 0. Numbers are **approximate and tied to the price table of the pinned ccusage at snapshot time**: a model that
table does not know (a new model) comes back as 0 and is shown as `unpriced` and left out of TOTAL (a note under the table counts them).
The five models the pinned ccusage leaves unpriced (claude-sonnet-5-5, claude-opus-5-5, claude-fable-5-1, gpt-6-sol, gpt-6.1-sol) get a built-in list price (LiteLLM, USD per million tokens, thinking counted as output) added to their row.
A model with no price anywhere is charged its harness's default model (claude: claude-haiku-5-5, codex: gpt-6-luna, pi: deepseek-flash; also listed: deepseek-v4-pro/-flash, glm-4.7 to 5.3) and the Source records `+default:<model>`; a note under the table says those costs may not be accurate.
A stored record keeps the cost it was snapshotted with; it is never re-priced.

## The history: `<folder>/.joint-cost/`

The history lives in the folder the tools are launched from, so cost stays trackable even when a transcript is lost. It is
machine-written data; commit it or git-ignore it as you prefer (nothing edits `.gitignore`).

- `launches.jsonl`, written by the launcher and session tools, one object per line:
  `{"startUtc":"2026-11-01T22:10:05.123Z","kind":"Launcher|Session","tool":"runJointLauncher","harness":"claude","folder":"<absolute folder>","skill":"<name or empty>","group":"<session group>"}`.
  A session belongs to an entry when harness and folder are equal and the session's first message is within 120 s after `startUtc`
  (closest pair first; one entry matches at most one session). A missing file is normal (everything is `manual`); a malformed line is an error.
- `usage.jsonl`, written by this reader: one line per (harness, session id, model) with `input`, `output`, `cacheRead`, `cacheWrite`,
  `thinking`, `costUsd`, `costSource` (`pi-recorded`|`ccusage`), `first`, `last`, `folder`, `snapshotUtc`.

Every run snapshots: it reads the live transcripts, merges them into `usage.jsonl`, writes it back (temp file in the same folder,
then rename, so it is never half written) and prints the table from the merged data. Per (harness, session, model) a live record replaces
the stored one when its token total is greater or equal; the stored one stays when its transcript is gone or holds less, so a trimmed
transcript never lowers a stored number. A session only in the store is still listed, with `snapshot only` in Source.
`--no-snapshot` merges in memory and writes nothing.

## Watch mode

`--watch --harness <one>` is what the tools start detached. It snapshots every `--interval-minutes` (default 5) without printing a table,
and ends with exit 0 when no transcript of the harness for the folder changed for `--idle-minutes` (default 10) and a snapshot was
taken after the last change; on SIGINT/SIGTERM/Ctrl+C it takes a final snapshot and exits 0. One watcher per folder and harness:
`.joint-cost/watch-<harness>.lock` holds its pid; a lock whose pid is dead is replaced, a live one makes a second watcher print
`already watching, pid N` and exit 0. Every snapshot appends a line (UTC time, sessions, total cost) to `.joint-cost/watch.log`. Any error
(unreadable transcript, ccusage failing) is written there with its cause and ends the watcher with exit 1: no retries, no skipping.

## Limits

Costs are approximate (above). ccusage needs Node and, offline pricing aside, runs for a few seconds per harness. The Source of a
row whose transcript was trimmed is `snapshot only`.
