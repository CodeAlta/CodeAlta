import assert from "node:assert/strict";
import test from "node:test";
import { createSessionDisplayStore, displayRowKey, displayToolActivityKey } from "./sessionDisplay";
import { createMutationCapability } from "./sessionOperations";
import type { SessionDisplayItem, SessionDisplayText, SessionDisplayToolActivity, SessionDisplayView } from "#neoastra";

const row: SessionDisplayText = { runId: "run", contentId: "content", kind: "Assistant", text: "before", isComplete: false, isTruncated: false, startedWithDelta: true };
function tool(overrides: Partial<SessionDisplayToolActivity> = {}): SessionDisplayToolActivity {
  return { providerId: "provider", runId: "run", activityId: "tool", phase: "Started", name: null, isNameTruncated: false, ...overrides };
}
function item(revision = "0", overrides: Partial<SessionDisplayItem> = {}): SessionDisplayItem {
  const session: SessionDisplayView = { sessionId: "selected", revision, lifecycle: null, queuedPromptCount: null, configuration: null,
    statusKind: null, statusMessage: null, text: [row], metadataTruncated: false, transportTruncated: false, evictedTextItems: "0", unsupportedEvents: "0",
    toolActivities: [], evictedToolActivities: "0" };
  return { status: "ok", hostEpoch: "host", sessionId: "selected", projectionEpoch: "projection", revision,
    previousRevision: null, isInitial: true, hasGap: false, isClosed: false, isPartial: true,
    evictedSessions: "0", omittedSessionEvents: "0", session, ...overrides };
}
async function* sequence(items: SessionDisplayItem[]) { yield* items; }

type Store = ReturnType<typeof createSessionDisplayStore>;
type Open = Parameters<typeof createSessionDisplayStore>[0];
type Fixture = {
  observe(open: Open, expected?: Partial<Record<"open" | "next" | "return", unknown>>): Store;
  gate<T>(cleanupValue: T): { promise: Promise<T>; resolve(value: T): void };
};

// Every case retains setup/body, original open/next/return, their wrappers/observers, and each
// selected drain promise before assertions. No scheduling tick is a readiness/termination proof.
async function runDisplayFixture(action: (fixture: Fixture) => Promise<void>) {
  const originals: { stage: string; promise: Promise<unknown>; original?: Promise<unknown>; observer: Promise<void>;
    outcome: "pending" | "success" | "failure"; error?: unknown; expected?: { error: unknown; seen: boolean } }[] = [];
  const expectations: { stage: string; error: unknown; seen: boolean }[] = [];
  const stores: Store[] = [];
  const iterators: AsyncIterator<SessionDisplayItem>[] = [];
  const signals: AbortSignal[] = [];
  const releases: (() => void)[] = [];
  const unsubscribes: (() => void)[] = [];
  let stopping = false;
  let expired = false;
  let deadlineFailure: Error | undefined;
  let body: Promise<void> | undefined;
  let bodyWait: Promise<void> | undefined;
  let joined: Promise<void> | undefined;
  let joinWait: Promise<void> | undefined;
  const stopFailures: unknown[] = [];
  function launch<T>(stage: string, start: () => Promise<T>, expected?: { error: unknown; seen: boolean }): Promise<T> {
    let resolve!: (value: T | PromiseLike<T>) => void, reject!: (error: unknown) => void;
    const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
    const record: (typeof originals)[number] = { stage, promise, observer: Promise.resolve(), outcome: "pending", expected };
    record.observer = promise.then(() => { record.outcome = "success"; }, error => {
      record.outcome = "failure"; record.error = error;
      if (expected && error === expected.error) expected.seen = true;
    });
    originals.push(record); // Publish ownership and observation before invoking user code.
    try { record.original = start(); resolve(record.original as Promise<T>); } catch (error) { reject(error); }
    return promise;
  }
  function retain<T>(promise: Promise<T>): Promise<T> { return launch("work", () => promise); }
  function stop() {
    stopping = true;
    for (const release of releases) { try { release(); } catch (error) { stopFailures.push(error); } }
    for (const store of stores) { try { store.detach(); } catch (error) { stopFailures.push(error); } }
  }
  const fixture: Fixture = {
    observe(open: Open, expected = {}): Store {
      if (stopping) throw new Error("Fixture already stopping");
      const stages = Object.entries(expected).map(([stage, error]) => ({ stage, error, seen: false }));
      expectations.push(...stages);
      const store = createSessionDisplayStore((request, options) => {
        signals.push(options.signal);
        const expectation = (stage: "open" | "next" | "return") => stages.find(value => value.stage === stage);
        const opening = launch("open", () => open(request, options), expectation("open"));
        return launch("open-wrapper", () => opening.then(stream => ({ [Symbol.asyncIterator]() {
          const iterator = stream[Symbol.asyncIterator]();
          iterators.push(iterator);
          const returned = iterator.return?.bind(iterator);
          return { next: () => launch("next", () => iterator.next(), expectation("next")),
            return: returned ? () => launch("return", () => returned(), expectation("return")) : undefined };
        } })), expectation("open"));
      });
      stores.push(store);
      return { ...store,
        select(...args: Parameters<Store["select"]>) {
          if (stopping) throw new Error("Fixture already stopping");
          const selection = store.select(...args);
          retain(store.settled());
          return selection;
        },
        subscribe(listener: () => void) {
          const unsubscribe = store.subscribe(listener);
          unsubscribes.push(unsubscribe);
          return unsubscribe;
        },
      };
    },
    gate<T>(cleanupValue: T) {
      if (stopping) throw new Error("Fixture already stopping");
      let resolve!: (value: T) => void;
      const promise = retain(new Promise<T>(done => { resolve = done; }));
      releases.push(() => resolve(cleanupValue));
      return { promise, resolve };
    },
  };
  let finishDeadline!: () => void;
  let timer!: ReturnType<typeof setTimeout>;
  const deadline = new Promise<void>((resolve, reject) => {
    finishDeadline = resolve;
    timer = setTimeout(() => {
      expired = true; // Permanent failure; late completion never permits unsubscribe/release of uncertain resources.
      deadlineFailure = new Error("Display fixture deadline; uncertain work retained, not proven terminated", {
        cause: { originals, stores, iterators, signals, releases, unsubscribes, body, bodyWait, joined, joinWait, fixture },
      });
      stop();
      reject(deadlineFailure);
    }, 5_000);
  });
  const deadlineObserver = deadline.then(() => {}, () => {});
  let primary: unknown;
  let failed = false;
  try {
    const setup = launch("setup", () => Promise.resolve());
    body = launch("body", () => setup.then(() => action(fixture)));
    bodyWait = Promise.race([body, deadline]);
    await bodyWait;
  } catch (error) { primary = error; failed = true;
  } finally {
    stop();
    try {
      if (!expired) {
        // Include originals added while earlier originals settle (e.g. late open -> iterator return).
        joined = (async () => { for (let index = 0; index < originals.length; index++) await originals[index].observer; })();
        joinWait = Promise.race([joined, deadline]);
        try { await joinWait; if (expired) throw deadlineFailure; } catch (error) {
          throw new AggregateError(failed ? [primary, error] : [error], "Display cleanup uncertain", {
            cause: { originals, stores, iterators, signals, releases, unsubscribes, body, bodyWait, joined, joinWait, fixture, expectations },
          });
        }
        const failures = originals.filter(record => record.outcome === "failure" &&
          (!record.expected || record.error !== record.expected.error)).map(record => record.error);
        failures.push(...stopFailures);
        if (failed) failures.unshift(primary);
        const cleanupFailures = originals.filter(record => record.stage === "return" && record.outcome === "failure");
        const cause = { originals, stores, iterators, signals, releases, unsubscribes, body, bodyWait, joined, joinWait, fixture, expectations };
        for (const expected of expectations) if (!expected.seen) failures.push(new Error(`Expected ${expected.stage} failure was not observed`, { cause: expected }));
        if (failures.length) failures.push(...cleanupFailures.map(record => record.error));
        if (failures.length) throw new AggregateError([...new Set(failures)], "Display body or original cleanup failed", { cause });
        if (cleanupFailures.length) {
          for (const record of cleanupFailures) assert.equal(stores.some(store => {
            const blocked = store.retainedCleanupFailure();
            return blocked?.error === record.error && blocked?.owner.returning === record.promise && store.getSnapshot().cleanupBlocked;
          }), true, "Every failed return must retain its exact blocked owner");
          console.log(new Error("Expected return failure: blocked owner and dependents retained, not released", { cause }));
        } else {
          if (stores.some(store => store.retainedCleanupFailure() !== null)) throw new Error("Unexpected unproven cleanup; owners retained", { cause });
          if (!expired) for (const unsubscribe of unsubscribes) unsubscribe();
        }
      }
    } finally {
      clearTimeout(timer);
      finishDeadline();
      await deadlineObserver;
    }
  }
  if (expired) throw new AggregateError([...new Set(failed ? [primary, deadlineFailure] : [deadlineFailure])], "Display deadline permanently failed", {
    cause: { originals, stores, iterators, signals, releases, unsubscribes, body, bodyWait, joined, joinWait, fixture, expectations },
  });
  if (failed) throw primary;
}

test("lossless revision ordering, full replacements, removals and closed state", () => runDisplayFixture(async fixture => {
  const baseline = item("9007199254740992");
  const replaced = item("9007199254740993", { isInitial: false, previousRevision: baseline.revision,
    session: { ...baseline.session!, text: [{ ...row, text: "final", isComplete: true, startedWithDelta: false }] } });
  const removed = item("9007199254740996", { isInitial: false, previousRevision: replaced.revision, session: null, hasGap: true, isClosed: true, evictedSessions: "1" });
  const store = fixture.observe(async () => sequence([baseline, replaced, baseline, removed]));
  const snapshots: SessionDisplayItem[] = [];
  store.subscribe(() => { const snapshot = store.getSnapshot().snapshot; if (snapshot) snapshots.push(snapshot); });
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
}));

test("empty text replacement removes rows while retaining latest status", () => runDisplayFixture(async fixture => {
  const first = item();
  const empty = item("1", { isInitial: false, previousRevision: "0", isClosed: true,
    session: { ...first.session!, queuedPromptCount: 3, text: [], evictedTextItems: "8", unsupportedEvents: "2", metadataTruncated: true } });
  const store = fixture.observe(async () => sequence([first, empty]));
  store.select("host", "selected"); await store.settled();
  assert.deepEqual(store.getSnapshot().snapshot!.session!.text, []);
  assert.equal(store.getSnapshot().snapshot!.session!.queuedPromptCount, 3);
  assert.equal(store.getSnapshot().snapshot!.session!.metadataTruncated, true);
}));

test("wrong host/projection/selection and malformed revision fail closed", () => runDisplayFixture(async fixture => {
  for (const [values, code] of [
    [[item("0", { hostEpoch: "old" })], "stale_epoch"],
    [[item(), item("1", { isInitial: false, previousRevision: "0", projectionEpoch: "other" })], "stale_projection"],
    [[item("0", { sessionId: "another" })], "invalid_update"],
    [[item("01")], "invalid_update"],
    [[item("-1")], "invalid_update"],
    [[item("0", { isInitial: false })], "invalid_update"],
  ] as const) {
    const store = fixture.observe(async () => sequence([...values]));
    store.select("host", "selected"); await store.settled();
    assert.equal(store.getSnapshot().code, code);
    assert.equal(store.getSnapshot().snapshot, null);
  }
}));

test("capacity/errors require explicit retry, do not leak messages or automatically mutate", () => runDisplayFixture(async fixture => {
  let opens = 0;
  const failure = new Error("SECRET raw transport detail");
  const store = fixture.observe(async () => {
    opens++;
    if (opens === 1) return sequence([item("0", { status: "capacity" })]);
    if (opens === 2) throw failure;
    return sequence([item("0", { isClosed: true, session: null })]);
  }, { open: failure });
  store.select("host", "selected"); await store.settled();
  assert.equal(opens, 1); assert.equal(store.getSnapshot().code, "capacity");
  store.select("host", "selected"); await store.settled();
  assert.equal(opens, 2); assert.equal(store.getSnapshot().code, "transport_failed");
  assert.equal(JSON.stringify(store.getSnapshot()).includes("SECRET"), false);
  store.select("host", "selected"); await store.settled();
  assert.equal(opens, 3); assert.equal(store.getSnapshot().kind, "closed");
}));

test("known stale host requires reload even if channel cleanup fails", () => runDisplayFixture(async fixture => {
  const failure = new Error("cleanup failed");
  const store = fixture.observe(async () => (async function* () {
    try { yield item("0", { status: "stale_epoch" }); }
    finally { throw failure; }
  })(), { return: failure });
  store.select("host", "selected"); await store.settled();
  assert.equal(store.getSnapshot().code, "stale_epoch");
  assert.equal(store.getSnapshot().snapshot, null);
  assert.equal(store.getSnapshot().cleanupBlocked, true);
  assert.equal(store.retainedCleanupFailure()?.error, failure);
}));

test("selection and unmount suppress late callbacks and join iterator return before reopening", () => runDisplayFixture(async fixture => {
  const next = fixture.gate<IteratorResult<SessionDisplayItem>>({ done: true, value: undefined });
  const reading = fixture.gate<void>(undefined);
  const calls: string[] = [];
  const signals: AbortSignal[] = [];
  let active = 0;
  let maxActive = 0;
  let returned = 0;
  const store = fixture.observe(async (request, options) => {
    calls.push(request.sessionId); signals.push(options.signal);
    active++; maxActive = Math.max(maxActive, active);
    return { [Symbol.asyncIterator]() { return {
      next: () => { reading.resolve(undefined); return next.promise; }, // Deliberately delivers after cancellation.
      return: async () => { returned++; active--; return { done: true, value: undefined }; },
    }; } };
  });
  store.select("host", "old"); await reading.promise;
  store.select("host", "new");
  assert.equal(signals[0].aborted, true);
  assert.equal(store.getSnapshot().snapshot, null);
  assert.deepEqual(calls, ["old"]);
  next.resolve({ done: false, value: item("0", { sessionId: "old" }) });
  await store.settled(); // Second selection rejects the deliberately wrong identity, then closes.
  assert.deepEqual(calls, ["old", "new"]);
  assert.equal(maxActive, 1);
  assert.equal(returned, 2);
  assert.equal(store.getSnapshot().code, "invalid_update");
  store.detach();
  assert.equal(store.getSnapshot().kind, "idle");
  assert.equal(signals[1].aborted, true);
}));

test("unmount during opening disposes the late channel without publishing", () => runDisplayFixture(async fixture => {
  let returned = 0;
  let read = 0;
  const late: AsyncIterable<SessionDisplayItem> = { [Symbol.asyncIterator]() { return {
    next: async () => { read++; return { done: false, value: item() }; },
    return: async () => { returned++; return { done: true, value: undefined }; },
  }; } };
  const opening = fixture.gate<AsyncIterable<SessionDisplayItem>>(late);
  const entered = fixture.gate<void>(undefined);
  const store = fixture.observe(() => { entered.resolve(undefined); return opening.promise; });
  store.select("host", "selected"); await entered.promise;
  store.detach();
  opening.resolve(late);
  await store.settled();
  assert.equal(store.getSnapshot().kind, "idle");
  assert.equal(read, 0); assert.equal(returned, 1);
}));

test("superseding a held open retains the late iterator until its own return succeeds", () => runDisplayFixture(async fixture => {
  const entered = fixture.gate<void>(undefined), returning = fixture.gate<void>(undefined);
  const finishReturn = fixture.gate<IteratorResult<SessionDisplayItem>>({ done: true, value: undefined });
  let reads = 0, returns = 0; const opens: string[] = [];
  const late: AsyncIterable<SessionDisplayItem> = { [Symbol.asyncIterator]: () => ({
    next: async () => { reads++; return { done: false, value: item() }; },
    return: () => { returns++; returning.resolve(undefined); return finishReturn.promise; },
  }) };
  const opening = fixture.gate(late);
  const store = fixture.observe(request => {
    opens.push(request.sessionId); entered.resolve(undefined);
    return opens.length === 1 ? opening.promise : Promise.resolve(sequence([item("0", { sessionId: request.sessionId, session: null, isClosed: true })]));
  });
  const first = store.select("host", "first"); await entered.promise;
  store.select("host", "skipped"); store.select("host", "last"); first.detach();
  opening.resolve(late); await returning.promise;
  assert.deepEqual(opens, ["first"]); assert.equal(reads, 0); assert.equal(returns, 1);
  finishReturn.resolve({ done: true, value: undefined }); await store.settled();
  assert.deepEqual(opens, ["first", "last"]); assert.equal(store.getSnapshot().kind, "closed");
  assert.equal(reads, 0); assert.equal(returns, 1);
}));

test("normal terminal return and missing terminal are distinguished", () => runDisplayFixture(async fixture => {
  let cleaned = 0;
  const store = fixture.observe(async () => (async function* () {
    try { yield item("0", { isClosed: true }); } finally { cleaned++; }
  })());
  store.select("host", "selected"); await store.settled();
  assert.equal(cleaned, 1); assert.equal(store.getSnapshot().kind, "closed");
  store.detach();
  const disconnected = fixture.observe(async () => sequence([item()]));
  disconnected.select("host", "selected"); await disconnected.settled();
  assert.equal(disconnected.getSnapshot().code, "ended_without_close");
}));

test("row identity includes session, run, content and channel without delimiter collisions", () => {
  const keys = [displayRowKey("selected", row), displayRowKey("other", row),
    displayRowKey("selected", { ...row, runId: "other" }), displayRowKey("selected", { ...row, contentId: "other" }),
    displayRowKey("selected", { ...row, kind: "Reasoning" }), displayRowKey("selected", { ...row, runId: null })];
  assert.equal(new Set(keys).size, keys.length);
});

test("one latest selection waits for real return; stale detach cannot cancel its successor", () => runDisplayFixture(async fixture => {
  const reading = fixture.gate<void>(undefined), returning = fixture.gate<void>(undefined);
  const newReading = fixture.gate<void>(undefined);
  const newNext = fixture.gate<IteratorResult<SessionDisplayItem>>({ done: true, value: undefined });
  let newSignal: AbortSignal | undefined;
  const next = fixture.gate<IteratorResult<SessionDisplayItem>>({ done: true, value: undefined });
  const releaseReturn = fixture.gate<IteratorResult<SessionDisplayItem>>({ done: true, value: undefined });
  const calls: string[] = [];
  const store = fixture.observe(async (request, options) => {
    calls.push(request.sessionId);
    if (calls.length > 1) {
      newSignal = options.signal;
      return { [Symbol.asyncIterator]: () => ({ next: () => { newReading.resolve(undefined); return newNext.promise; },
        return: async () => ({ done: true, value: undefined }),
      }) };
    }
    return { [Symbol.asyncIterator]: () => ({
      next: () => { reading.resolve(undefined); return next.promise; },
      return: () => { returning.resolve(undefined); return releaseReturn.promise; },
    }) };
  });
  const old = store.select("host", "first"); await reading.promise;
  store.select("host", "second");
  next.resolve({ done: true, value: undefined }); await returning.promise;
  for (let index = 0; index < 32; index++) store.select("host", `skipped-${index}`);
  store.select("host", "latest"); old.detach();
  assert.deepEqual(calls, ["first"]);
  releaseReturn.resolve({ done: true, value: undefined }); await newReading.promise;
  old.detach(); assert.equal(newSignal?.aborted, false);
  newNext.resolve({ done: false, value: item("0", { sessionId: "latest", session: null, isClosed: true }) }); await store.settled();
  assert.deepEqual(calls, ["first", "latest"]);
  assert.equal(store.getSnapshot().sessionId, "latest");
  assert.equal(store.getSnapshot().kind, "closed");
}));

test("late correlated epoch evidence revokes authority before obsolete presentation fencing", () => runDisplayFixture(async fixture => {
  for (const patch of [
    { hostEpoch: "other" },
    { status: "stale_epoch" },
    { hostEpoch: "other", sessionId: "wrong" },
    { hostEpoch: "\ud800" },
    { hostEpoch: "other", projectionEpoch: "" },
    { hostEpoch: "other", revision: "9223372036854775808" },
    { status: "stale_epoch", projectionEpoch: "\ud800" },
    {},
  ]) {
    const entered = fixture.gate<void>(undefined);
    const next = fixture.gate<IteratorResult<SessionDisplayItem>>({ done: true, value: undefined });
    const capability = createMutationCapability("host");
    const subscriberFailure = new Error("notification failed");
    let returned = 0, notifications = 0;
    capability.subscribe(() => { throw subscriberFailure; });
    capability.subscribe(() => { notifications++; });
    const store = fixture.observe(async () => ({ [Symbol.asyncIterator]: () => ({
      next: () => { entered.resolve(undefined); return next.promise; },
      return: async () => { returned++; return { done: true, value: undefined }; },
    }) }));
    const selected = store.select("host", "selected", capability.observe); await entered.promise;
    selected.detach();
    next.resolve({ done: false, value: item("0", { ...patch, session: null }) });
    await store.settled();
    const validRevocation = Object.keys(patch).length === 1 && (patch.hostEpoch === "other" || patch.status === "stale_epoch");
    assert.equal(capability.canMutate(), !validRevocation);
    assert.equal(notifications, validRevocation ? 1 : 0);
    assert.equal(capability.notificationFailure(), validRevocation ? subscriberFailure : undefined);
    assert.equal(returned, 1);
    assert.equal(store.getSnapshot().snapshot, null);
  }
}));

test("return rejection including AbortError blocks reopen and retains the failed owner", () => runDisplayFixture(async fixture => {
  for (const error of [new Error("private return failure"), Object.assign(new Error("cancel-shaped cleanup"), { name: "AbortError" })]) {
    let opens = 0;
    const store = fixture.observe(async () => {
      opens++;
      return { [Symbol.asyncIterator]: () => ({
        next: async () => ({ done: false, value: item("0", { isClosed: true }) }),
        return: async () => { throw error; },
      }) };
    }, { return: error });
    store.select("host", "selected"); await store.settled();
    store.select("host", "other"); await store.settled();
    assert.equal(opens, 1);
    assert.equal(store.getSnapshot().cleanupBlocked, true);
    assert.equal(store.getSnapshot().code, "cleanup_failed");
    assert.equal(store.retainedCleanupFailure()?.error, error);
    assert.equal(JSON.stringify(store.getSnapshot()).includes(error.message), false);
  }
}));

test("expected next failure still requires successful return before explicit retry", () => runDisplayFixture(async fixture => {
  const error = new Error("private next failure"); let opens = 0, returns = 0;
  const store = fixture.observe(async () => {
    if (++opens > 1) return sequence([item("0", { isClosed: true })]);
    return { [Symbol.asyncIterator]: () => ({ next: async () => { throw error; },
      return: async () => { returns++; return { done: true, value: undefined }; } }) };
  }, { next: error });
  store.select("host", "selected"); await store.settled();
  assert.equal(returns, 1); assert.equal(store.getSnapshot().code, "transport_failed");
  store.select("host", "selected"); await store.settled();
  assert.equal(opens, 2); assert.equal(store.getSnapshot().kind, "closed");
}));

test("superseded cancellation-shaped next and return failures retain both originals and block latest", () => runDisplayFixture(async fixture => {
  const nextFailure = Object.assign(new Error("next canceled"), { name: "AbortError" });
  const returnFailure = Object.assign(new Error("return canceled"), { name: "AbortError" });
  const reading = fixture.gate<void>(undefined), releaseNext = fixture.gate<void>(undefined);
  const returning = fixture.gate<void>(undefined), releaseReturn = fixture.gate<void>(undefined);
  let opens = 0;
  const store = fixture.observe(async () => { opens++; return { [Symbol.asyncIterator]: () => ({
    next: async () => { reading.resolve(undefined); await releaseNext.promise; throw nextFailure; },
    return: async () => { returning.resolve(undefined); await releaseReturn.promise; throw returnFailure; },
  }) }; }, { next: nextFailure, return: returnFailure });
  store.select("host", "old"); await reading.promise;
  store.select("host", "new"); releaseNext.resolve(undefined); await returning.promise;
  store.select("host", "latest"); assert.equal(opens, 1);
  releaseReturn.resolve(undefined); await store.settled();
  assert.equal(store.retainedCleanupFailure()?.owner.failure, nextFailure);
  assert.equal(store.retainedCleanupFailure()?.error, returnFailure);
  assert.equal(store.getSnapshot().code, "cleanup_failed"); assert.equal(store.getSnapshot().sessionId, "latest");
  store.select("host", "retry"); await store.settled(); assert.equal(opens, 1);
}));

test("fixture failure reporting preserves primary and cancellation-shaped cleanup failures", () => runDisplayFixture(async () => {
  const primary = new Error("primary assertion failure");
  const cleanup = Object.assign(new Error("cleanup cancellation"), { name: "AbortError" });
  await assert.rejects(runDisplayFixture(async fixture => {
    const reading = fixture.gate<void>(undefined), release = fixture.gate<IteratorResult<SessionDisplayItem>>({ done: true, value: undefined });
    const store = fixture.observe(async () => ({ [Symbol.asyncIterator]: () => ({
      next: () => { reading.resolve(undefined); return release.promise; }, return: async () => { throw cleanup; },
    }) }), { return: cleanup });
    store.select("host", "selected"); await reading.promise; throw primary;
  }), error => {
    assert.ok(error instanceof AggregateError);
    assert.ok(error.errors.includes(primary)); assert.ok(error.errors.includes(cleanup));
    assert.ok(error.cause); console.log(error); return true;
  });
}));

test("revisions are exact canonical nonnegative Int64 strings", () => runDisplayFixture(async fixture => {
  for (const revision of ["0", "9007199254740993", "9223372036854775807", "9223372036854775808", "9999999999999999999", "1\n", "1\r\n", "01", "-1", "+1", " 1", "1 "]) {
    const store = fixture.observe(async () => sequence([item(revision, { isClosed: true })]));
    store.select("host", "selected"); await store.settled();
    assert.equal(store.getSnapshot().kind, ["0", "9007199254740993", "9223372036854775807"].includes(revision) ? "closed" : "error");
  }
}));

test("previous and embedded session revisions reject noncanonical or overflowing Int64 values", () => runDisplayFixture(async fixture => {
  for (const revision of ["9223372036854775808", "1\n", "01", "-1"]) {
    for (const malformed of [
      item("9223372036854775807", { isInitial: false, previousRevision: revision }),
      item("1", { isInitial: false, previousRevision: "0", session: { ...item().session!, revision } }),
    ]) {
      const store = fixture.observe(async () => sequence([item(), malformed]));
      store.select("host", "selected"); await store.settled();
      assert.equal(store.getSnapshot().code, "invalid_update"); assert.equal(store.getSnapshot().snapshot, null);
    }
  }
}));

test("nullable error identity envelope and legacy opaque/case-insensitive embedded identities stay valid", () => runDisplayFixture(async fixture => {
  const capability = createMutationCapability("host");
  const error = fixture.observe(async () => sequence([item("0", { status: "stale_epoch", revision: null,
    previousRevision: null, projectionEpoch: null, session: null })]));
  error.select("host", "selected", capability.observe); await error.settled(); assert.equal(capability.canMutate(), false);
  for (const sessionId of ["selected", "legacy/session", "\ufeff", "a\0b"]) {
    const first = item("9223372036854775807", { sessionId, projectionEpoch: "legacy-projection", isClosed: true });
    const store = fixture.observe(async () => sequence([{ ...first, session: { ...first.session!, sessionId: sessionId.toUpperCase() } }]));
    store.select("host", sessionId); await store.settled(); assert.equal(store.getSnapshot().kind, "closed");
  }
}));

test("reported tools are frozen full replacements, including regressed phase, eviction, gap and closure", () => runDisplayFixture(async fixture => {
  const first = item();
  const baseline = item("0", { session: { ...first.session!, toolActivities: [tool({ phase: "Completed", runId: null }), tool({ providerId: "Provider" })] } });
  const replacement = item("3", { isInitial: false, previousRevision: "0", session: { ...first.session!,
    toolActivities: [tool({ runId: null, name: "x".repeat(127), isNameTruncated: true })], evictedToolActivities: "9223372036854775807" } });
  const empty = item("4", { isInitial: false, previousRevision: "3", session: first.session });
  const closed = item("5", { isInitial: false, previousRevision: "4", isClosed: true, session: null });
  const store = fixture.observe(async () => sequence([baseline, replacement, empty, closed]));
  const snapshots: SessionDisplayItem[] = [];
  store.subscribe(() => { const value = store.getSnapshot().snapshot; if (value) snapshots.push(value); });
  store.select("host", "selected"); await store.settled();
  assert.equal(snapshots.length, 4);
  assert.equal(snapshots[0].session!.toolActivities.length, 2);
  assert.equal(snapshots[0].session!.toolActivities[0].phase, "Completed");
  assert.equal(snapshots[0].session!.evictedToolActivities, "0");
  assert.equal(snapshots[1].session!.toolActivities.length, 1);
  assert.equal(snapshots[1].session!.toolActivities[0].phase, "Started");
  assert.equal(snapshots[1].session!.toolActivities[0].runId, null);
  assert.equal(snapshots[1].session!.evictedToolActivities, "9223372036854775807");
  assert.equal(snapshots[1].hasGap, true);
  assert.deepEqual(snapshots[2].session!.toolActivities, []);
  assert.equal(snapshots[3].session, null);
  assert.equal(store.getSnapshot().kind, "closed");
  assert.equal(Object.isFrozen(snapshots[0].session!.toolActivities), true);
  assert.equal(Object.isFrozen(snapshots[0].session!.toolActivities[0]), true);
  assert.notEqual(snapshots[0].session!.toolActivities[0], baseline.session!.toolActivities[0]);
  assert.throws(() => Object.assign(snapshots[0].session!.toolActivities[0], { phase: "Failed" }), TypeError);
}));

test("reported tool validation accepts only the six phases and exact bounded well-formed values", () => runDisplayFixture(async fixture => {
  const bound = "x".repeat(254) + "😀";
  for (const phase of ["Requested", "Started", "Progressed", "Completed", "Failed", "Canceled"]) {
    const base = item();
    const value = item("0", { isClosed: true, session: { ...base.session!, toolActivities: [
      tool({ providerId: bound, runId: bound, activityId: bound, name: "x".repeat(126) + "😀", phase }),
      tool({ providerId: "\ufeff", runId: " run ", activityId: " tool ", name: "", phase }),
    ] } });
    const store = fixture.observe(async () => sequence([value]));
    store.select("host", "selected"); await store.settled();
    assert.equal(store.getSnapshot().kind, "closed");
    assert.deepEqual(store.getSnapshot().snapshot!.session!.toolActivities, value.session!.toolActivities);
  }
}));

test("malformed reported tool data fails invalid_update without publishing a fallback window", () => runDisplayFixture(async fixture => {
  const invalid: Record<string, unknown>[] = [
    { toolActivities: undefined }, { toolActivities: null }, { toolActivities: {} }, { toolActivities: [null] },
    { toolActivities: [tool(), tool()] }, { toolActivities: [tool(), tool({ activityId: "b" }), tool({ activityId: "c" })] },
  ];
  for (const field of ["providerId", "runId", "activityId"]) {
    for (const value of [undefined, "", " \t", "\u0085", "x".repeat(257), "\ud800", "\udc00", "x\ud800x", 1])
      invalid.push({ toolActivities: [{ ...tool(), [field]: value }] });
    if (field !== "runId") invalid.push({ toolActivities: [{ ...tool(), [field]: null }] });
  }
  for (const phase of ["Selected", "Deselected", "started", "Unknown", "0", 0, null, undefined])
    invalid.push({ toolActivities: [{ ...tool(), phase }] });
  for (const name of [undefined, 1, "x".repeat(129), "\ud800", "x\udc00x"])
    invalid.push({ toolActivities: [{ ...tool(), name }] });
  for (const isNameTruncated of [undefined, null, 0, "false"])
    invalid.push({ toolActivities: [{ ...tool(), isNameTruncated }] });
  invalid.push({ toolActivities: [tool({ name: null, isNameTruncated: true })] });
  for (const evictedToolActivities of [undefined, null, 0, "", "00", "01", "+1", "-1", " 1", "1 ", "1\n", "1.0", "1e0", "9223372036854775808", "18446744073709551615"])
    invalid.push({ evictedToolActivities });
  for (const patch of invalid) {
    const first = item();
    const malformed = item("1", { isInitial: false, previousRevision: "0", isClosed: true,
      session: { ...first.session!, ...patch } as unknown as SessionDisplayView });
    const store = fixture.observe(async () => sequence([first, malformed]));
    const snapshots: SessionDisplayItem[] = [];
    store.subscribe(() => { const value = store.getSnapshot().snapshot; if (value) snapshots.push(value); });
    store.select("host", "selected"); await store.settled();
    assert.equal(store.getSnapshot().code, "invalid_update", JSON.stringify(patch));
    assert.equal(store.getSnapshot().snapshot, null);
    assert.equal(snapshots.length, 1);
  }
}));

test("reported tool keys include the full session provider nullable-run activity tuple without collisions", () => {
  const rows = [tool(), tool({ providerId: "Provider" }), tool({ runId: "Run" }), tool({ activityId: "Tool" }),
    tool({ runId: null }), tool({ runId: "null" }), tool({ providerId: "a|b", runId: "c" }), tool({ providerId: "a", runId: "b|c" }),
    tool({ providerId: 'a","b', activityId: 'c\\d' })];
  const keys = rows.map(value => displayToolActivityKey("selected", value));
  keys.push(displayToolActivityKey("other", rows[0]));
  assert.equal(new Set(keys).size, keys.length);
});
