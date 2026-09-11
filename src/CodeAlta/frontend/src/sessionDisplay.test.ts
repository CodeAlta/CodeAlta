import assert from "node:assert/strict";
import test from "node:test";
import { setImmediate as tick } from "node:timers/promises";
import { createSessionDisplayStore, displayRowKey } from "./sessionDisplay";
import type { SessionDisplayItem, SessionDisplayText, SessionDisplayView } from "#neoastra";

const row: SessionDisplayText = { runId: "run", contentId: "content", kind: "Assistant", text: "before", isComplete: false, isTruncated: false, startedWithDelta: true };
function item(revision = "0", overrides: Partial<SessionDisplayItem> = {}): SessionDisplayItem {
  const session: SessionDisplayView = { sessionId: "selected", revision, lifecycle: null, queuedPromptCount: null, configuration: null,
    statusKind: null, statusMessage: null, text: [row], metadataTruncated: false, transportTruncated: false, evictedTextItems: "0", unsupportedEvents: "0" };
  return { status: "ok", hostEpoch: "host", sessionId: "selected", projectionEpoch: "projection", revision,
    previousRevision: null, isInitial: true, hasGap: false, isClosed: false, isPartial: true,
    evictedSessions: "0", omittedSessionEvents: "0", session, ...overrides };
}
async function* sequence(items: SessionDisplayItem[]) { yield* items; }

test("lossless revision ordering, full replacements, removals and closed state", async () => {
  const baseline = item("9007199254740992");
  const replaced = item("9007199254740993", { isInitial: false, previousRevision: baseline.revision,
    session: { ...baseline.session!, text: [{ ...row, text: "final", isComplete: true, startedWithDelta: false }] } });
  const removed = item("9007199254740996", { isInitial: false, previousRevision: replaced.revision, session: null, hasGap: true, isClosed: true, evictedSessions: "1" });
  const store = createSessionDisplayStore(async () => sequence([baseline, replaced, baseline, removed]));
  const snapshots: SessionDisplayItem[] = [];
  const unsubscribe = store.subscribe(() => { const snapshot = store.getSnapshot().snapshot; if (snapshot) snapshots.push(snapshot); });
  store.select("host", "selected");
  await store.settled();
  assert.equal(snapshots.length, 3); // Out-of-order baseline never applies, even above Number.MAX_SAFE_INTEGER.
  assert.equal(snapshots[0].session!.text[0].text, "before");
  assert.equal(snapshots[1].session!.text[0].text, "final");
  assert.equal(snapshots[1].session!.text.length, 1);
  assert.equal(snapshots[2].session, null);
  assert.equal(snapshots[2].hasGap, true);
  assert.equal(store.getSnapshot().kind, "closed");
  assert.equal(Object.isFrozen(snapshots[0]), true);
  assert.equal(Object.isFrozen(snapshots[0].session!.text), true);
  assert.equal(Object.isFrozen(snapshots[0].session!.text[0]), true);
  unsubscribe(); store.detach();
});

test("empty text replacement removes rows while retaining latest status", async () => {
  const first = item();
  const empty = item("1", { isInitial: false, previousRevision: "0", isClosed: true,
    session: { ...first.session!, queuedPromptCount: 3, text: [], evictedTextItems: "8", unsupportedEvents: "2", metadataTruncated: true } });
  const store = createSessionDisplayStore(async () => sequence([first, empty]));
  store.select("host", "selected"); await store.settled();
  assert.deepEqual(store.getSnapshot().snapshot!.session!.text, []);
  assert.equal(store.getSnapshot().snapshot!.session!.queuedPromptCount, 3);
  assert.equal(store.getSnapshot().snapshot!.session!.metadataTruncated, true);
  store.detach();
});

test("wrong host/projection/selection and malformed revision fail closed", async () => {
  for (const [values, code] of [
    [[item("0", { hostEpoch: "old" })], "stale_epoch"],
    [[item(), item("1", { isInitial: false, previousRevision: "0", projectionEpoch: "other" })], "stale_projection"],
    [[item("0", { sessionId: "another" })], "invalid_update"],
    [[item("01")], "invalid_update"],
    [[item("-1")], "invalid_update"],
    [[item("0", { isInitial: false })], "invalid_update"],
  ] as const) {
    const store = createSessionDisplayStore(async () => sequence([...values]));
    store.select("host", "selected"); await store.settled();
    assert.equal(store.getSnapshot().code, code);
    assert.equal(store.getSnapshot().snapshot, null);
    store.detach();
  }
});

test("capacity/errors require explicit retry, do not leak messages or automatically mutate", async () => {
  let opens = 0;
  const store = createSessionDisplayStore(async () => {
    opens++;
    if (opens === 1) return sequence([item("0", { status: "capacity" })]);
    if (opens === 2) throw new Error("SECRET raw transport detail");
    return sequence([item("0", { isClosed: true, session: null })]);
  });
  store.select("host", "selected"); await store.settled();
  assert.equal(opens, 1); assert.equal(store.getSnapshot().code, "capacity");
  store.select("host", "selected"); await store.settled();
  assert.equal(opens, 2); assert.equal(store.getSnapshot().code, "transport_failed");
  assert.equal(JSON.stringify(store.getSnapshot()).includes("SECRET"), false);
  store.select("host", "selected"); await store.settled();
  assert.equal(opens, 3); assert.equal(store.getSnapshot().kind, "closed");
  store.detach();
});

test("known stale host requires reload even if channel cleanup fails", async () => {
  const store = createSessionDisplayStore(async () => (async function* () {
    try { yield item("0", { status: "stale_epoch" }); }
    finally { throw new Error("cleanup failed"); }
  })());
  store.select("host", "selected"); await store.settled();
  assert.equal(store.getSnapshot().code, "stale_epoch");
  assert.equal(store.getSnapshot().snapshot, null);
  store.detach();
});

test("selection and unmount suppress late callbacks and join iterator return before reopening", { timeout: 5000 }, async () => {
  let resolve!: (item: IteratorResult<SessionDisplayItem>) => void;
  const next = new Promise<IteratorResult<SessionDisplayItem>>(done => { resolve = done; });
  const calls: string[] = [];
  const signals: AbortSignal[] = [];
  let active = 0;
  let maxActive = 0;
  let returned = 0;
  const store = createSessionDisplayStore(async (request, options) => {
    calls.push(request.sessionId); signals.push(options.signal);
    active++; maxActive = Math.max(maxActive, active);
    return { [Symbol.asyncIterator]() { return {
      next: () => next, // Deliberately delivers a callback after cancellation.
      return: async () => { returned++; active--; return { done: true, value: undefined }; },
    }; } };
  });
  try {
    store.select("host", "old"); await tick();
    store.select("host", "new");
    assert.equal(signals[0].aborted, true);
    assert.equal(store.getSnapshot().snapshot, null);
    assert.deepEqual(calls, ["old"]);
    resolve({ done: false, value: item("0", { sessionId: "old" }) });
    await store.settled(); // Second selection rejects the deliberately wrong identity, then closes.
    assert.deepEqual(calls, ["old", "new"]);
    assert.equal(maxActive, 1);
    assert.equal(returned, 2);
    assert.equal(store.getSnapshot().code, "invalid_update");
    store.detach();
    assert.equal(store.getSnapshot().kind, "idle");
    assert.equal(signals[1].aborted, true);
  } finally { resolve({ done: true, value: undefined }); store.detach(); await store.settled(); }
});

test("unmount during opening disposes the late channel without publishing", { timeout: 5000 }, async () => {
  let finish!: (value: AsyncIterable<SessionDisplayItem>) => void;
  const opening = new Promise<AsyncIterable<SessionDisplayItem>>(resolve => { finish = resolve; });
  let returned = 0;
  let read = 0;
  const store = createSessionDisplayStore(async () => opening);
  store.select("host", "selected"); await tick();
  store.detach();
  finish({ [Symbol.asyncIterator]() { return {
    next: async () => { read++; return { done: false, value: item() }; },
    return: async () => { returned++; return { done: true, value: undefined }; },
  }; } });
  await store.settled();
  assert.equal(store.getSnapshot().kind, "idle");
  assert.equal(read, 0); assert.equal(returned, 1);
});

test("normal terminal return and missing terminal are distinguished", async () => {
  let cleaned = 0;
  const store = createSessionDisplayStore(async () => (async function* () {
    try { yield item("0", { isClosed: true }); } finally { cleaned++; }
  })());
  store.select("host", "selected"); await store.settled();
  assert.equal(cleaned, 1); assert.equal(store.getSnapshot().kind, "closed"); store.detach();
  const disconnected = createSessionDisplayStore(async () => sequence([item()]));
  disconnected.select("host", "selected"); await disconnected.settled();
  assert.equal(disconnected.getSnapshot().code, "ended_without_close"); disconnected.detach();
});

test("row identity includes session, run, content and channel without delimiter collisions", () => {
  const keys = [displayRowKey("selected", row), displayRowKey("other", row),
    displayRowKey("selected", { ...row, runId: "other" }), displayRowKey("selected", { ...row, contentId: "other" }),
    displayRowKey("selected", { ...row, kind: "Reasoning" }), displayRowKey("selected", { ...row, runId: null })];
  assert.equal(new Set(keys).size, keys.length);
});
