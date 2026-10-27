import assert from "node:assert/strict";
import test from "node:test";
import type { SessionResumeActionRequest, SessionResumeResult, SessionResumeState } from "#neoastra";
import { createResumeHub, readPendingResume, readResumeOffer, resumeLine, type ResumeApi, type ResumeTimers } from "./resumeHub";

const id = "0123456789abcdef0123456789abcdef";
const other = "fedcba9876543210fedcba9876543210";

// A host that answers what the test sets, and counts what it is asked.
function fakeHost(pending: SessionResumeState["resume"]) {
  const calls: string[] = [];
  let current = pending;
  let status = "ok";
  const api: ResumeApi = {
    pendingResume: async request => { calls.push(`read ${request.sessionId}`); return { status: "ok", epoch: "epoch", sessionId: request.sessionId, resume: current }; },
    cancelResume: async (request: SessionResumeActionRequest): Promise<SessionResumeResult> => {
      calls.push(`cancel ${request.resumeId}`);
      if (status === "ok") current = null;
      return { status, epoch: "epoch", sessionId: request.sessionId };
    },
    resumeNow: async (request: SessionResumeActionRequest): Promise<SessionResumeResult> => {
      calls.push(`resume ${request.resumeId}`);
      if (status === "ok") current = null;
      return { status, epoch: "epoch", sessionId: request.sessionId };
    },
  };
  return { api, calls, set: (value: SessionResumeState["resume"]) => { current = value; }, refuse: (value: string) => { status = value; } };
}

function fakeTimers() {
  const waits: Array<{ delay: number; callback: () => void; cleared: boolean }> = [];
  const timers: ResumeTimers = {
    setTimeout: (callback, delay) => { waits.push({ delay, callback, cleared: false }); return (waits.length - 1) as unknown as ReturnType<typeof setTimeout>; },
    clearTimeout: handle => { const wait = waits[handle as unknown as number]; if (wait) wait.cleared = true; },
  };
  return { timers, waits, live: () => waits.filter(wait => !wait.cleared) };
}

const flush = () => new Promise(resolve => setTimeout(resolve, 0));
const item = (resumeId: string, resumeAt: string) => ({ resumeId, resumeAt, providerKey: "claude-code", providerName: "Claude Code", attempt: 1 });

test("the details of a card give the resume it scheduled; any other card gives none", () => {
  const details = { resume: "scheduled", resumeId: id, resumeAt: "2026-10-27T13:05:30Z", resumeProviderName: "Claude Code" };
  assert.deepEqual(readResumeOffer(details), { id, at: "2026-10-27T13:05:30Z", providerName: "Claude Code" });
  assert.equal(readResumeOffer({ ...details, resume: "cancelled" }), null);
  assert.equal(readResumeOffer({ ...details, resumeId: "not an id" }), null);
  assert.equal(readResumeOffer({ ...details, resumeAt: "soon" }), null);
  assert.equal(readResumeOffer(null), null);
  assert.deepEqual(readResumeOffer({ ...details, resumeProviderName: 7 }), { id, at: "2026-10-27T13:05:30Z", providerName: "" });
  // What the host says: a resume, none, or no reading at all.
  assert.deepEqual(readPendingResume({ status: "ok", epoch: "e", sessionId: "s", resume: item(id, "2026-10-27T13:05:30Z") }), { id, at: "2026-10-27T13:05:30Z", providerName: "Claude Code" });
  assert.equal(readPendingResume({ status: "ok", epoch: "e", sessionId: "s", resume: null }), null);
  assert.equal(readPendingResume({ status: "stale_epoch", epoch: "e", sessionId: "s", resume: null }), undefined);
});

test("the line of the card says the time, with the day when it is not today", () => {
  const now = new Date(2026, 9, 27, 9, 0);
  const today = resumeLine({ id, at: new Date(2026, 9, 27, 14, 5).toISOString(), providerName: "Claude Code" }, now, "en-GB");
  assert.equal(today.key, "Resumes at {time} with {provider}");
  assert.deepEqual(today.values, { time: "14:05", provider: "Claude Code" });
  const later = resumeLine({ id, at: new Date(2026, 9, 29, 8, 30).toISOString(), providerName: "" }, now, "en-GB");
  assert.equal(later.key, "Resumes on {date} at {time}");
  assert.equal(later.values.time, "08:30");
  assert.match(later.values.date, /29/);
});

test("the hub reads the resume once a card shows it, follows the run of the session, and wakes after its time", async () => {
  const host = fakeHost(item(id, new Date(Date.now() + 60_000).toISOString()));
  const clock = fakeTimers();
  const hub = createResumeHub(host.api, "epoch", "session-1", clock.timers);
  hub.noteRunning(false);
  assert.deepEqual(host.calls, [], "Nothing is read while no card shows a resume.");

  let changes = 0;
  const unsubscribe = hub.subscribe(() => { changes++; });
  await flush();
  assert.deepEqual(host.calls, ["read session-1"]);
  assert.equal(hub.getSnapshot().pending?.id, id);
  assert.equal(changes, 1);
  // A little after its time, the hub reads again to see it started.
  assert.equal(clock.live().length, 1);
  assert.ok(clock.live()[0].delay >= 60_000 && clock.live()[0].delay <= 63_000, String(clock.live()[0].delay));

  // The run of the session starts: the resume started, or another prompt took its place.
  host.set(null);
  hub.noteRunning(true);
  await flush();
  assert.equal(host.calls.length, 2);
  assert.equal(hub.getSnapshot().pending, null);
  assert.equal(clock.live().length, 0, "Nothing waits for a resume that no longer waits.");

  host.set(item(other, new Date(Date.now() + 120_000).toISOString()));
  clock.waits[0].callback();
  await flush();
  assert.equal(hub.getSnapshot().pending?.id, other);
  unsubscribe();
  assert.equal(clock.live().length, 0, "A hub no card shows waits for nothing.");
  hub.noteRunning(false);
  await flush();
  assert.equal(host.calls.length, 3, "Nor does it read.");
});

test("an action is sent once, then the resume is read again; a refusal is said until the next action", async () => {
  const host = fakeHost(item(id, new Date(Date.now() + 60_000).toISOString()));
  const hub = createResumeHub(host.api, "epoch", "session-1", fakeTimers().timers);
  hub.subscribe(() => {});
  await flush();

  host.refuse("closed");
  const first = hub.act("resume", id);
  assert.equal(hub.getSnapshot().acting, "resume");
  void hub.act("cancel", id);
  await first;
  assert.deepEqual(host.calls.filter(call => !call.startsWith("read")), [`resume ${id}`], "A second action waits for the first.");
  assert.equal(hub.getSnapshot().refused, "closed");
  assert.equal(hub.getSnapshot().pending?.id, id);

  host.refuse("ok");
  await hub.act("cancel", id);
  assert.equal(hub.getSnapshot().refused, null);
  assert.equal(hub.getSnapshot().acting, null);
  assert.equal(hub.getSnapshot().pending, null);

  // A resume that no longer waits is not a refusal.
  host.refuse("unknown");
  await hub.act("cancel", id);
  assert.equal(hub.getSnapshot().refused, null);
  await hub.act("cancel", "not an id");
  assert.equal(host.calls.filter(call => call.startsWith("cancel")).length, 2);
});
