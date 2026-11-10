---
name: joint-hackernews
description: Browse Hacker News together with the user in the joint session's shared browser, with options injected into the pages - a dock, high-karma submitters, topic groups, velocity, comment indent guides, collapsing near-duplicate or toxic comments, a thread summary - and the AI cost always shown. Read-only: never posts, replies, votes or submits. Use for "browse hacker news", "joint hackernews", "/joint-hackernews".
---

# joint-hackernews

The user watches the shared browser (DevTools on 127.0.0.1:9222) and **navigates by clicking in it**. You inject
options into the Hacker News pages they visit and help read: classify stories, summarise and label a thread. Plan:
`PLAN_HACKERNEWS.md` in the repository root.

## The rule: this skill never writes to Hacker News

Never post, comment, reply, submit a link, edit, delete, vote, favorite, flag, hide or log in for the user, and never
type into an HN form field. Hacker News is a community of people writing in their own words; text written by an AI
and posted as theirs is not welcome there, and the user's name is on it. If asked, decline kindly and offer the
read-only alternative: summarise the thread, list the arguments, so the user writes their own reply. The page shows
this notice at the bottom; `hn.mjs` has no write command and `open` goes only to read-only pages.

Be honest about the limit: this is a recommendation and a good-faith guard, not a security boundary. The user owns
this skill and `joint-browser` is generic; what they do with them is theirs. On HN pages use only read commands of
`joint-browser` (`snapshot`, `text`, `count`, `attr`, `screenshot`) and nothing that clicks, fills or submits.

## Use on other sites, terms and law

This skill is built and tested for Hacker News only, which is a public site that allows normal reading. The same
technique (a Chrome with DevTools on 127.0.0.1:9222, scripts injected into the page) can technically change what any
site shows, but that does not make it allowed elsewhere:

- Sites such as Facebook, X, TikTok and Google have terms that prohibit accessing their service through automated
  means, and they do not exempt read-only or personal use. They can detect an automation-configured browser, and the
  usual consequence is a checkpoint, a temporary restriction or a suspended account.
- Do not use this skill to scrape, collect or store other people's content, to bypass a login, rate limit or access
  control, or to post, like, follow or message on a user's behalf. Laws on unauthorised access and data protection
  differ by country and can apply even to personal use.
- If the user asks to use it on another site: say that it was not made for that, point them to that site's current
  terms, and prefer a plain userscript or extension that only changes the page's own CSS/DOM. Do not add features
  that automate such a site.

This is a good-faith notice and not legal advice; the user is responsible for the sites they use it on.

## 0. Ready

**Never `cd` in a command of this skill.** Give `node` the absolute path of the script (`node "<this folder>/hn.mjs" start`).
The AI cost in the dock is read from this session's transcript, and a session that changes folder (even into a
subfolder) is not counted: the line would show $0.00 for the whole session.

1. `node <this folder>/../joint-browser/browser.mjs status` must name a `browser` (Node 20+, Playwright, the session's
   browser). If not, follow joint-browser's section 0 and ask the user to start the launcher.
2. `node <this folder>/hn.mjs start`: the daemon (it stays attached, so the options survive every navigation).
   It reads the AI cost with `runJointCostAST.cs`; `status` shows `cost` (an `error` there is shown in the dock as
   "AI cost: unavailable", never as a made-up number: tell the user the message).

## 1. First run: three questions, then browse

Check `node hn.mjs persona` first. If it says `chosen: false`, ask the three questions below. If the persona is already chosen,
say which one it is (it cannot change), skip the questions and go on to browsing.

Ask these three in one question-tool call, in this order, with exactly these texts and options in this order (never
reorder to put a saved or recommended one first; say the default in the option text):

1. "How do you use Hacker News?" (one): Reader, Contributor, Maker, Researcher/curator. Apply with
   `hn.mjs persona choose <reader|contributor|maker|researcher>`. **The persona is chosen once and cannot be changed**
   (the command refuses a second time; the dock offers no switch). Do not offer to change it; if the user insists,
   `hn.mjs reset` wipes the options and starts over.
2. "Token use level for Claude tasks?" (one): Frugal, Balanced (default), Thorough. Apply with `hn.mjs budget <level>`.
3. "Where should the dock go?" (one): Right (default), Left, Top, Bottom. Apply with `hn.mjs set dock <side>`.

The dock then shows only what the persona needs (data in `modules.json`): Reader = triage + look + bookmarks,
Contributor = triage + look + worth joining, Maker = look + bookmarks, Researcher = bookmarks + triage + look. Anything
finer (karma threshold, watch-list, mute, alerts, groups, classifier, collapse depth) is not in the dock: set it only
when the user asks, with `hn.mjs set <path> <value>` (`hn.mjs get` prints every option) or `hn.mjs group add`.

Then `node hn.mjs open` and tell the user to browse. Options that need no Claude call cost nothing; classify, summarise
and label cost tokens, shown at the bottom ("AI cost: session $x | group G $y"; `*` = an estimate).

## 2. Browse

The user clicks. You answer and act when asked, or when the dock shows requests: **check `node hn.mjs requests` when
the user says "handle the HN requests" or after they mention the dock** (the daemon cannot wake you).

- **Classify stories** (request `classify` or "classify these"): `node hn.mjs read`; assign each story one group name
  (the configured groups, or a new name the user wants; `Others` when none fits), judging title and domain. Write
  `{"stories": {"<id>": "<group>", ...}}` to a scratch file and `node hn.mjs annotate <file>`.
- **Groups.** Add one with `hn.mjs group add <name> --keywords a,b --domains x.com`; `group remove <name>`, `group list`. You may also
  **propose** one: when you classify and about four or more stories on the page share a theme that fits no group
  (they would all be `Others`), do not invent the group silently. Ask the user (your question tool): the name, the
  keywords you would use, the stories it would take. Only on a yes run `group add`, then classify again so the page
  uses it. Never assign a group name that is not configured; ask first. A name that is not configured shows grey.
- **Summarise a thread** (`summary`): `hn.mjs read` on the discussion page. Write `{"summaries": {"<itemId>":
  {"overview", "positions": [{"title","text","ids":["<comment id>"]}], "disagreement", "best": ["<comment id>"],
  "covered": <comments read>, "total": <comments in the thread>}}}`. Every position cites comment ids; only claim what
  the comments say; say if the thread was truncated (`truncated: true`).
- **Label comments** (`labels`): for each comment judge its tone, not the person: `attack` (personal attack / ad
  hominem, not engaging the argument), `snark`, `lowvalue` (adds nothing), `repeat` (repeats an earlier comment: add
  `reason` naming it), `good` (substantive: new information, a primary source, real expertise). Label only what is
  clear; unlabelled is the default. Write `{"comments": {"<id>": {"label": "...", "reason": "<=160 chars"}}}`. The page
  collapses labelled comments to a one-line stub with a "show" link (the user's sensitivity setting decides which); nothing
  is hidden for good and nothing is reported to HN.
- After `annotate`, look at the result (`browser.mjs screenshot` to the scratchpad, `snapshot`) and tell the user what
  changed. Costs show up in the dock.
- Reading only: `hn.mjs read`, `browser.mjs text|snapshot|count|attr|screenshot`. Every read of a long thread costs
  tokens; read once and say how much you read.

## Worth joining (the `opportunities` module; free, read-only)

For people who want to take part in a topic: the dock lists up to 8 threads that are early and quiet (default: under 6
hours old, at most 10 comments), in the user's topic groups, plus any Ask HN nobody has answered when "Any Ask HN" is on.
The daemon reads HN's public search (cached 5 minutes; `hn.mjs opps`); the page scores the threads with the user's own
groups (earlier, fewer comments, Ask HN with no answer rank higher). It only finds the thread: the user opens it and
writes their own words. Tune the thresholds in the dock; the keyword lists decide what counts as "in my groups".

## Bookmarks (local, free)

A ☆ on every story, on the discussion header and on every comment saves it to a local JSON file, grouped by topic
group and then by the day it was saved (the group is fixed at save time and can be moved in the dock). The dock has the
list, a search, a note per bookmark and "Copy as Markdown". It is not an HN favorite: nothing is written to HN.
`node hn.mjs bookmarks [--group AI] [--since 2026-11-01]` prints the JSON; `--md` prints Markdown. Use it when the user
asks what they saved ("what did I bookmark about AI this week?"): read it once and answer; a digest costs tokens, so
say so first.

Bookmarks take comma-separated **tags** in the dock (`bookmarks --tag rust`), which the search also finds.

**Digest of what they saved** (ask first: it costs tokens): `node hn.mjs bookmarks --md --since <date>` (add `--group`,
`--tag`), read it once, and write the digest in the conversation, or to a Markdown file at a path the user names (never
inside the repository unless they say so). Group it as the bookmarks are grouped; quote their notes.

## Mute, alerts, article summaries (all local; none writes to HN)

- **Mute** (`list.mute.domains|users|words`, or the ⊘ on a row = mute that site): the row disappears from the list; the
  dock counts them and "show them" reveals them dimmed. A muted user's comments collapse to a stub. This is not HN's
  own "hide" (that writes to HN). Alert words (`list.alerts.words`) win over a mute, get a bell and the strongest colour,
  and the dock tab shows how many.
- **Article summary** (the 📖 on a row or on the discussion header queues a request `article` with the story id): run
  `node hn.mjs article <id>`: it returns the linked page as readable text, capped by the token-use level, with
  `minutes` (reading time), `truncated` and `words`. Summarise only what it returned, in 2-3 sentences, say if it
  was `truncated`, and if it fails (`ok: false`: a PDF, a paywall, a video) say why and do not guess the content. Then
  `annotate` `{"articles": {"<id>": {"tldr": "<=500 chars", "minutes": <n>}}}`; the row then shows "📖 N min" with the
  summary as its tooltip. The daemon fetches only a story's own public link, never a private address. Say before you fetch
  that it costs about the tokens the dock showed.

## Keep the token cost down

The cost line is the whole session's spend (and the default group's), including everything you do. Browsing in a long
session is expensive because every call re-reads the whole conversation: tell the user a fresh launcher session is
the way to measure the true cost of browsing. And:
- Do the free things first (keyword groups, karma, duplicates, bookmarks need no Claude call).
- One `hn.mjs read` per task, never twice; it already honours the user's token-use level. If the reply says
  `truncated: true`, say what you did not read (`total` vs `included`) rather than reading more, unless asked.
- No screenshots or `snapshot` of HN pages unless the user asks or a change must be verified by eye (images and the
  accessibility tree cost far more than `hn.mjs read` or a small `eval`). Prefer a small `eval` that returns a count.
- Short answers; do not repeat what the page already shows; do not narrate each tool call.
- With `claude.cheapModel` on, give the reading to a small-model subagent with a tight brief and only the schema's
  JSON back; you then run `annotate`. Say that you are doing so.
- Before an expensive step (a 150+ comment thread at `thorough`) say the dock's estimate and let the user choose.

## 3. Fine-tune

Change an option with `hn.mjs set` first, the code only when an option cannot do it (`hn-inject.js` is the page,
`hn.mjs` the daemon; then `node hn.mjs restart` and reload the tab). Validate by effect in the page (an `eval` counting
badges, a screenshot), never by "no error". The dock's 📖 on a story and the Summarise button only **queue** a request
(the row shows "⏳ queued" and the footer says so): run `hn.mjs requests` when the user tells you to handle them.

## 4. End

`node hn.mjs stop` (the injections go with the daemon; reload to see plain HN). The window stays open. State lives in
`~/.joint-hackernews/` (options, annotations, karma cache, log); nothing is written into the repository.

Commands: `node hn.mjs help`.
