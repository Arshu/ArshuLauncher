// joint-hackernews: the pure part of automatic request handling (PLAN/PLAN_HN_AUTOHANDLE.md), used by hn.mjs and hn.smoke.mjs.
// No file, no network, no clock of its own: the daemon passes the queue and the time, this file says who handles what.
//
//   worker   (article, summary)            a one-shot headless call on the cheap model; needs the page text, not the conversation
//   trigger  (labels, classify, picks)     the harness is woken by a typed line, because the answer needs the live session
//
// A request is eligible when it is `queued`, or `processing` for longer than STUCK_MS (the claimer died); the same rule as `hn.mjs wait`.

export const WORKER_KINDS = ['article', 'summary'];
export const TRIGGER_KINDS = ['labels', 'classify', 'picks'];
export const STUCK_MS = 600000;
export const HOUR_MS = 3600000;
export const DEFAULT_PER_HOUR = 20;

export function routeOf(kind) {
  if (WORKER_KINDS.includes(kind)) return 'worker';
  if (TRIGGER_KINDS.includes(kind)) return 'trigger';
  return null;
}

export function eligible(r, now) {
  if (r.status === 'queued') return true;
  if (r.status !== 'processing') return false;
  const at = Date.parse(r.claimedAt || '');
  return Number.isFinite(at) && now - at > STUCK_MS;
}

/**
 * queue: the requests; now: ms; auto: {on, perHour}; workerStarts: ms timestamps of the worker calls started so far.
 * Returns {on, worker, trigger, unknown, capped, capLeft}: `worker` is what may start now (oldest first, at most the calls left
 * in this hour), `capped` what waits for the cap, `unknown` what has a kind neither route handles (reported, never dropped).
 */
export function autoPlan(queue, now, auto, workerStarts = []) {
  const perHour = Number.isFinite(Number(auto && auto.perHour)) && Number(auto.perHour) >= 0 ? Number(auto.perHour) : DEFAULT_PER_HOUR;
  const used = workerStarts.filter((t) => now - t < HOUR_MS).length;
  const capLeft = Math.max(0, perHour - used);
  const out = { on: !!(auto && auto.on), worker: [], trigger: [], unknown: [], capped: [], capLeft };
  if (!out.on) return out;
  const byAge = (a, b) => Date.parse(a.at || '') - Date.parse(b.at || '') || 0;
  const open = (Array.isArray(queue) ? queue : []).filter((r) => eligible(r, now)).sort(byAge);
  for (const r of open) {
    const route = routeOf(r.kind);
    if (route === 'trigger') out.trigger.push(r);
    else if (route === 'worker') (out.worker.length < capLeft ? out.worker : out.capped).push(r);
    else out.unknown.push(r);
  }
  return out;
}

/** One line for `hn.mjs auto status` and the dock: what is waiting and why it has not started. */
export function describePlan(plan, dispatch) {
  if (!plan.on) return 'off';
  const parts = [];
  const built = (dispatch.worker && dispatch.worker.kinds) || [];
  const run = plan.worker.filter((r) => built.includes(r.kind)), notBuilt = plan.worker.filter((r) => !built.includes(r.kind));
  if (run.length) parts.push(`${run.length} for the worker`);
  if (notBuilt.length) parts.push(`${notBuilt.length} for the worker (${[...new Set(notBuilt.map((r) => r.kind))].join(', ')}: worker not built in this version)`);
  if (plan.trigger.length) parts.push(`${plan.trigger.length} for the trigger${dispatch.trigger ? '' : ' (trigger not built in this version)'}`);
  if (plan.capped.length) parts.push(`${plan.capped.length} waiting for the hourly cap`);
  if (plan.unknown.length) parts.push(`${plan.unknown.length} of an unknown kind`);
  return parts.length ? 'on: ' + parts.join(', ') : 'on: nothing waiting';
}
