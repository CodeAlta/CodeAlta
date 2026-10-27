import assert from "node:assert/strict";
import test from "node:test";
import type { SessionAdmission, SessionCompactRequest, SessionReceiptPage, SessionRuntimeStateResponse } from "#neoastra";
import { createMutationCapability } from "./sessionOperations";
import { captureCompaction, compactControlBlock, compactControlBlockMessages, compactionAdmissionFailure, compactionBlock, createCompactionSubmissions, hasPendingCompaction,
  settledCompactionFailure, watchCompaction, watchedCompactionFailure } from "./sessionCompaction";
import { messages } from "./localization";

const observation: SessionRuntimeStateResponse = {
  status: "ok", hostEpoch: "epoch", sessionId: "session", runtimeInstanceId: "runtime", coordinatorTransitionInProgress: false,
  entry: { attachmentGeneration: "9223372036854775807", activeRunId: null, isTerminated: false, isRetiring: false,
    backgroundTasks: [], queueDrainInProgress: false, providerId: "fake", providerKey: "fake", modelId: null, reasoningEffort: null, agentPromptId: null, pendingAgentPromptId: null, activity: null },
};
function request(key = "key") { return captureCompaction("epoch", "session", observation, key)!; }
function page(kind = "Compact", key = "key"): SessionReceiptPage {
  return { status: "ok", epoch: "epoch", next: null, rows: [{ clientRequestId: key, sessionId: "session", operationId: "operation",
    targetOperationId: null, kind, state: "pending", outcome: null, code: null, runId: null, queueInsertion: null }] };
}
function finiteJoin<T>(task: Promise<T>): Promise<T> {
  let timer: ReturnType<typeof setTimeout>;
  const deadline = new Promise<never>((_, reject) => { timer = setTimeout(() => reject(new Error("Fixture join timed out")), 5_000); });
  return Promise.race([task, deadline]).finally(() => clearTimeout(timer));
}

test("compact captures immutable attachment eligibility only from an explicit idle observation", () => {
  assert.equal(Object.isFrozen(request()), true);
  assert.deepEqual(request(), { expectedEpoch: "epoch", clientRequestId: "key", sessionId: "session", expectedRuntimeInstanceId: "runtime",
    expectedAttachmentGeneration: "9223372036854775807" });
  for (const unavailable of [undefined, { ...observation, hostEpoch: "old" }, { ...observation, sessionId: "other" },
    { ...observation, status: "unavailable" }, { ...observation, coordinatorTransitionInProgress: true }, { ...observation, entry: null },
    { ...observation, entry: { ...observation.entry!, activeRunId: "run" } },
    { ...observation, entry: { ...observation.entry!, queueDrainInProgress: true } },
    { ...observation, entry: { ...observation.entry!, isRetiring: true } }, { ...observation, entry: { ...observation.entry!, isTerminated: true } }])
    assert.equal(captureCompaction("epoch", "session", unavailable, "key"), null);
});

test("the reason a session cannot be compacted names what it waits for, and capture follows it exactly", () => {
  const cases: [SessionRuntimeStateResponse | undefined, ReturnType<typeof compactionBlock>][] = [
    [undefined, "unknown"], [{ ...observation, status: "unavailable" }, "unknown"], [{ ...observation, hostEpoch: "old" }, "unknown"],
    [{ ...observation, sessionId: "other" }, "unknown"],
    // A saved session that has not attached to the runtime yet: nothing runs, and nothing can be compacted.
    [{ ...observation, runtimeInstanceId: null, entry: null }, "unattached"], [{ ...observation, entry: null }, "unattached"],
    [{ ...observation, coordinatorTransitionInProgress: true }, "changing"], [{ ...observation, coordinatorTransitionInProgress: null }, "changing"],
    [{ ...observation, entry: { ...observation.entry!, isRetiring: true } }, "closing"],
    [{ ...observation, entry: { ...observation.entry!, isTerminated: true } }, "closing"],
    [{ ...observation, entry: { ...observation.entry!, activeRunId: "run" } }, "running"],
    [{ ...observation, entry: { ...observation.entry!, queueDrainInProgress: true } }, "running"],
    [observation, null],
  ];
  for (const [value, block] of cases) {
    assert.equal(compactionBlock("epoch", "session", value), block);
    assert.equal(captureCompaction("epoch", "session", value, "key") === null, block !== null);
  }
});

test("the Compact control is enabled exactly when compact() would submit, and says why not otherwise", () => {
  const can = () => true, cannot = () => false;
  const busy = { ...observation, entry: { ...observation.entry!, activeRunId: "run" } };
  const block = (state: Partial<Parameters<typeof compactControlBlock>[0]>, value: SessionRuntimeStateResponse | undefined = observation) =>
    compactControlBlock({ invalidEpoch: false, compacting: false, retained: null, canSubmit: can, ...state }, "epoch", "session", value);
  assert.equal(block({}), null);
  assert.equal(block({}, busy), "running");
  assert.equal(block({}, { ...observation, entry: null }), "unattached");
  assert.equal(block({ compacting: true }), "compacting");
  assert.equal(block({ invalidEpoch: true }, busy), "reload");
  assert.equal(block({ canSubmit: cannot }), "reload");
  // A retained request is retried exactly, whatever the session is now observed to do; the host decides.
  assert.equal(block({ retained: request() }, busy), null);
  assert.equal(block({ retained: request(), canSubmit: cannot }, busy), "reload");
  assert.equal(block({ retained: request(), compacting: true }), "compacting");
  for (const key of Object.values(compactControlBlockMessages)) assert.ok(Object.hasOwn(messages, key) || key === "Reload required.", key);
});

test("a compaction that did not complete is told from its admission or its settled receipt, and a completed one is not", () => {
  const row = (outcome: string | null, code: string | null = null, state = "terminal") => ({ ...page().rows[0], state, outcome, code });
  assert.equal(settledCompactionFailure(row("Completed")), null);
  assert.equal(settledCompactionFailure(row(null, null, "pending")), null);
  assert.equal(settledCompactionFailure(row("Failed", "compact_failed"))?.key, "The compaction failed. The timeline says why.");
  assert.equal(settledCompactionFailure(row("Failed", "compact_unsuccessful"))?.key, "The compaction failed. The timeline says why.");
  assert.equal(settledCompactionFailure(row("Failed", "compact_busy"))?.key, "The session is busy: compact it once it is idle.");
  assert.equal(settledCompactionFailure(row("Failed", "compact_unsupported"))?.key, "This provider cannot compact the conversation.");
  assert.equal(settledCompactionFailure(row("Cancelled"))?.key, "The compaction was cancelled.");
  assert.equal(compactionAdmissionFailure({ status: "accepted", epoch: "epoch", receipt: page().rows[0] }), null);
  assert.equal(compactionAdmissionFailure({ status: "accepted", epoch: "epoch", receipt: row("Failed", "compact_failed") })?.key, "The compaction failed. The timeline says why.");
  assert.equal(compactionAdmissionFailure({ status: "busy", epoch: "epoch", receipt: null })?.key, "The session is busy: compact it once it is idle.");
  assert.equal(compactionAdmissionFailure({ status: "uncertain", epoch: "epoch", receipt: null })?.key, "CodeAlta did not confirm the compaction. Check the timeline before compacting again.");
  assert.equal(compactionAdmissionFailure({ status: "stale_epoch", epoch: "epoch", receipt: null })?.key, "CodeAlta restarted. Reload the window to continue.");
  assert.deepEqual(compactionAdmissionFailure({ status: "capacity", epoch: "epoch", receipt: null }), { key: "The compaction was not accepted ({status}).", parameters: { status: "capacity" } });
  // The watch of the composer finds the end of its own request only.
  const watch = { key: "key", listed: true };
  const settled = (value: ReturnType<typeof row>): SessionReceiptPage => ({ ...page(), rows: [value] });
  assert.equal(watchedCompactionFailure(watch, "session", settled(row("Failed", "compact_failed")))?.key, "The compaction failed. The timeline says why.");
  assert.equal(watchedCompactionFailure(watch, "session", settled({ ...row("Failed", "compact_failed"), clientRequestId: "other" })), null);
  assert.equal(watchedCompactionFailure(watch, "session", settled({ ...row("Failed", "compact_failed"), kind: "Send" })), null);
  assert.equal(watchedCompactionFailure(watch, "session", settled(row("Completed"))), null);
  assert.equal(watchedCompactionFailure(watch, "session", undefined), null);
});

test("uncertain compact survives remount, requires exact explicit retry and only Compact-kind reconciliation", async () => {
  const seen: SessionCompactRequest[] = [];
  const store = createCompactionSubmissions(async value => { seen.push(value); throw new Error("uncertain"); });
  const capability = createMutationCapability("epoch");
  const signal = new AbortController().signal;
  const original = request();
  await store.submit(original, signal, capability, () => {});
  const remounted = store.pending("session")!;
  assert.equal(remounted.request, original);
  await store.submit(request("new-key"), signal, capability, () => {});
  assert.equal(seen.length, 1);
  await store.submit(remounted.request, signal, capability, () => {});
  assert.equal(seen.length, 2);
  assert.equal(seen[0], seen[1]);
  for (const kind of ["Send", "Abort", "Steer"]) assert.equal(store.reconcile("session", page(kind), capability), false);
  assert.equal(store.reconcile("session", page("Compact", "other"), capability), false);
  assert.equal(store.reconcile("session", page(), capability), true);
  assert.equal(store.pending("session"), undefined);
});

test("compact double clicks latch synchronously and selection loss retains the original request", async () => {
  let resolve!: (value: SessionAdmission) => void;
  const response = new Promise<SessionAdmission>(done => { resolve = done; });
  const work: Promise<unknown>[] = [response];
  const failures: unknown[] = [];
  const selection = new AbortController();
  const capability = createMutationCapability("epoch");
  const published: unknown[] = [];
  let calls = 0;
  const store = createCompactionSubmissions(async () => { calls++; return response; });
  const original = request();
  try {
    work.push(store.submit(original, selection.signal, capability, value => published.push(value)));
    work.push(store.submit(original, selection.signal, capability, value => published.push(value)));
    assert.equal(calls, 1);
    assert.equal(store.pending("session")!.inFlight, true);
    assert.equal(store.reconcile("session", page(), capability), false);
    selection.abort();
    resolve({ status: "accepted", epoch: "epoch", receipt: page().rows[0] });
    await Promise.all(work.map(finiteJoin));
    assert.equal(store.pending("session")!.request, original);
    assert.equal(store.pending("session")!.inFlight, false);
    assert.deepEqual(published, []);
    assert.equal(store.reconcile("session", page(), capability), true);
  } catch (error) { failures.push(error); }
  finally {
    selection.abort();
    resolve({ status: "accepted", epoch: "epoch", receipt: page().rows[0] });
    const settled = await Promise.allSettled(work.map(finiteJoin));
    failures.push(...settled.filter((value): value is PromiseRejectedResult => value.status === "rejected").map(value => value.reason));
  }
  if (failures.length) throw new AggregateError(failures, "Fixture failed or cleanup unconfirmed");
});

test("wrong compact admission kind stays uncertain; busy clears only for a fresh explicit action", async () => {
  let status = "accepted", kind = "Steer", calls = 0;
  const store = createCompactionSubmissions(async () => { calls++; return { status, epoch: "epoch", receipt: page(kind).rows[0] }; });
  const capability = createMutationCapability("epoch");
  const signal = new AbortController().signal;
  const original = request();
  await store.submit(original, signal, capability, () => {});
  assert.equal(store.pending("session")!.request, original);
  kind = "Compact";
  await store.submit(original, signal, capability, () => {});
  assert.equal(store.pending("session"), undefined);
  status = "busy";
  await store.submit(request("busy"), signal, capability, () => {});
  assert.equal(store.pending("session"), undefined);
  assert.equal(calls, 3);
});

test("late compact stale epoch invalidates shared authority without retargeting", async () => {
  const selection = new AbortController();
  const capability = createMutationCapability("epoch");
  let calls = 0;
  const store = createCompactionSubmissions(async () => { calls++; selection.abort(); return { status: "stale_epoch", epoch: "new", receipt: null }; });
  const original = request();
  await store.submit(original, selection.signal, capability, () => assert.fail("Obsolete selection published"));
  assert.equal(capability.canMutate(), false);
  await store.submit(original, new AbortController().signal, capability, () => {});
  assert.equal(calls, 1);
  assert.equal(store.pending("session")!.request, original);
  assert.equal(store.reconcile("session", page(), capability), false);
});

test("compact retention is bounded and pre-cancelled attempts never invoke", async () => {
  let calls = 0;
  const store = createCompactionSubmissions(async () => { calls++; throw new Error("uncertain"); });
  const capability = createMutationCapability("epoch");
  const cancelled = new AbortController(); cancelled.abort();
  await store.submit(request(), cancelled.signal, capability, () => {});
  assert.equal(calls, 0);
  const signal = new AbortController().signal;
  for (let index = 0; index < 256; index++)
    await store.submit(Object.freeze({ ...request(), sessionId: `session-${index}`, clientRequestId: `key-${index}` }), signal, capability, () => {});
  await store.submit(request(), signal, capability, () => {});
  assert.equal(calls, 256);
  assert.equal(store.pending("session"), undefined);
});

test("a compaction key whose receipt the host no longer keeps is a definite answer", async () => {
  let status = "admission_failed";
  const store = createCompactionSubmissions(async () => ({ status, epoch: "epoch", receipt: null }));
  const capability = createMutationCapability("epoch");
  const signal = new AbortController().signal;
  const original = request();
  await store.submit(original, signal, capability, () => {});
  assert.equal(store.pending("session")!.request, original);
  status = "expired";
  const published: string[] = [];
  await store.submit(original, signal, capability, value => published.push(value.status));
  assert.deepEqual(published, ["expired"]);
  assert.equal(store.pending("session"), undefined);
});

test("a pending Compact receipt of the session is what says a compaction is going on", () => {
  assert.equal(hasPendingCompaction("session", page()), true);
  assert.equal(hasPendingCompaction("session", undefined), false);
  assert.equal(hasPendingCompaction("other", page()), false);
  assert.equal(hasPendingCompaction("session", page("Send")), false);
  assert.equal(hasPendingCompaction("session", { ...page(), status: "unavailable" }), false);
  const settled = page();
  assert.equal(hasPendingCompaction("session", { ...settled, rows: [{ ...settled.rows[0], state: "terminal", outcome: "Completed" }] }), false);
});

test("a submitted compaction is followed from its submission to the end its receipt reports", () => {
  const submitted = { key: "key", listed: false };
  const empty: SessionReceiptPage = { status: "ok", epoch: "epoch", next: null, rows: [] };
  const terminal: SessionReceiptPage = { ...empty, rows: [{ ...page().rows[0], state: "terminal", outcome: "Completed" }] };
  // A page read before the admission, or one that could not be read, says nothing of the compaction.
  assert.equal(watchCompaction(submitted, "session", undefined), submitted);
  assert.equal(watchCompaction(submitted, "session", empty), submitted);
  assert.equal(watchCompaction(submitted, "session", { ...terminal, status: "unavailable" }), submitted);
  assert.equal(watchCompaction(submitted, "session", page("Compact", "other")), submitted);
  assert.equal(watchCompaction(submitted, "session", page("Send")), submitted);
  const listed = watchCompaction(submitted, "session", page());
  assert.deepEqual(listed, { key: "key", listed: true });
  assert.equal(watchCompaction(listed!, "session", page()), listed);
  // The end: a settled receipt, also one never seen pending, or a receipt that is no longer listed.
  assert.equal(watchCompaction(listed!, "session", terminal), null);
  assert.equal(watchCompaction(submitted, "session", terminal), null);
  assert.equal(watchCompaction(listed!, "session", empty), null);
});
