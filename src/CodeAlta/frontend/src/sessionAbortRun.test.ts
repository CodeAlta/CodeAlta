import assert from "node:assert/strict";
import test from "node:test";
import type { SessionAbortRunRequest, SessionAdmission, SessionReceiptPage, SessionRuntimeStateResponse } from "#neoastra";
import { createMutationCapability } from "./sessionOperations";
import { captureAbortRun, createAbortRunSubmissions } from "./sessionAbortRun";

const observation: SessionRuntimeStateResponse = {
  status: "ok", hostEpoch: "epoch", sessionId: "session", runtimeInstanceId: "runtime", coordinatorTransitionInProgress: false,
  entry: { attachmentGeneration: "9223372036854775807", activeRunId: "run-A", isTerminated: false, isRetiring: false,
    queueDrainInProgress: false, providerId: "fake", providerKey: "fake", modelId: null, reasoningEffort: null, agentPromptId: null, pendingAgentPromptId: null },
};
function request() { return captureAbortRun("epoch", "session", observation, "key")!; }
function page(kind = "AbortRun", key = "key"): SessionReceiptPage {
  return { status: "ok", epoch: "epoch", next: null, rows: [{ clientRequestId: key, sessionId: "session", operationId: "operation",
    targetOperationId: null, kind, state: "pending", outcome: null, code: null, runId: null, queueInsertion: null }] };
}
function finiteJoin<T>(task: Promise<T>): Promise<T> {
  let timer: ReturnType<typeof setTimeout>;
  const deadline = new Promise<never>((_, reject) => { timer = setTimeout(() => reject(new Error("Fixture join timed out")), 5_000); });
  return Promise.race([task, deadline]).finally(() => clearTimeout(timer));
}

test("abort-run freezes only an explicit eligible observation, including the original run", () => {
  assert.equal(Object.isFrozen(request()), true);
  assert.deepEqual(request(), { expectedEpoch: "epoch", clientRequestId: "key", sessionId: "session", expectedRuntimeInstanceId: "runtime",
    expectedAttachmentGeneration: "9223372036854775807", expectedRunId: "run-A" });
  for (const unavailable of [undefined, { ...observation, hostEpoch: "old" }, { ...observation, sessionId: "other" },
    { ...observation, status: "unavailable" }, { ...observation, coordinatorTransitionInProgress: true }, { ...observation, entry: null },
    { ...observation, entry: { ...observation.entry!, activeRunId: null } }, { ...observation, entry: { ...observation.entry!, activeRunId: " " } },
    { ...observation, entry: { ...observation.entry!, queueDrainInProgress: true } },
    { ...observation, entry: { ...observation.entry!, isRetiring: true } }, { ...observation, entry: { ...observation.entry!, isTerminated: true } }])
    assert.equal(captureAbortRun("epoch", "session", unavailable, "key"), null);
});

test("abort-run remount retains exact object despite new observation; only its AbortRun row reconciles", async () => {
  const seen: SessionAbortRunRequest[] = [];
  const store = createAbortRunSubmissions(async value => { seen.push(value); throw new Error("uncertain"); });
  const capability = createMutationCapability("epoch");
  const signal = new AbortController().signal;
  const original = request();
  await store.submit(original, signal, capability, () => {});
  const remounted = store.pending("session")!;
  const newer = captureAbortRun("epoch", "session", { ...observation, entry: { ...observation.entry!, activeRunId: "run-B" } }, "new")!;
  await store.submit(newer, signal, capability, () => {});
  assert.equal(seen.length, 1);
  await store.submit(remounted.request, signal, capability, () => {});
  assert.equal(seen.length, 2);
  assert.equal(seen[0], seen[1]);
  for (const kind of ["Send", "Abort", "Steer", "Compact"]) assert.equal(store.reconcile("session", page(kind), capability), false);
  assert.equal(store.reconcile("session", page("AbortRun", "other"), capability), false);
  assert.equal(store.reconcile("session", page(), capability), true);
});

test("abort-run synchronous latch survives selection cancellation until original waiter joins", async () => {
  let resolve!: (value: SessionAdmission) => void;
  const response = new Promise<SessionAdmission>(done => { resolve = done; });
  const work: Promise<unknown>[] = [response];
  const failures: unknown[] = [];
  const selection = new AbortController();
  const capability = createMutationCapability("epoch");
  let calls = 0;
  const store = createAbortRunSubmissions(async () => { calls++; return response; });
  const original = request();
  try {
    work.push(store.submit(original, selection.signal, capability, () => assert.fail("Obsolete panel published")));
    work.push(store.submit(original, selection.signal, capability, () => assert.fail("Duplicate published")));
    assert.equal(calls, 1);
    assert.equal(store.pending("session")!.inFlight, true);
    selection.abort();
    assert.equal(store.reconcile("session", page(), capability), false);
    resolve({ status: "accepted", epoch: "epoch", receipt: page().rows[0] });
    await Promise.all(work.map(finiteJoin));
    assert.equal(store.pending("session")!.request, original);
    assert.equal(store.pending("session")!.inFlight, false);
    assert.equal(store.reconcile("session", page(), capability), true);
  } catch (error) { failures.push(error); }
  finally {
    selection.abort(); resolve({ status: "accepted", epoch: "epoch", receipt: page().rows[0] });
    const settled = await Promise.allSettled(work.map(finiteJoin));
    failures.push(...settled.filter((value): value is PromiseRejectedResult => value.status === "rejected").map(value => value.reason));
  }
  if (failures.length) throw new AggregateError(failures, "Fixture failed; original work retained", { cause: work });
});

test("abort-run late epoch closes shared mutation authority without stale publication or retry", async () => {
  const selection = new AbortController();
  const capability = createMutationCapability("epoch");
  let calls = 0;
  const store = createAbortRunSubmissions(async () => { calls++; selection.abort(); return { status: "stale_epoch", epoch: "new", receipt: null }; });
  const original = request();
  await store.submit(original, selection.signal, capability, () => assert.fail("Obsolete panel published"));
  assert.equal(capability.canMutate(), false);
  await store.submit(original, new AbortController().signal, capability, () => {});
  assert.equal(calls, 1);
  assert.equal(store.pending("session")!.request, original);
  assert.equal(store.reconcile("session", page(), capability), false);
});

test("abort-run pre-cancellation, capacity and wrong-kind admission fail closed", async () => {
  let calls = 0;
  const store = createAbortRunSubmissions(async () => { calls++; return { status: "accepted", epoch: "epoch", receipt: page("Abort").rows[0] }; });
  const capability = createMutationCapability("epoch");
  const cancelled = new AbortController(); cancelled.abort();
  await store.submit(request(), cancelled.signal, capability, () => {});
  assert.equal(calls, 0);
  const signal = new AbortController().signal;
  await store.submit(request(), signal, capability, () => {});
  assert.ok(store.pending("session"));
  for (let index = 1; index < 256; index++)
    await store.submit(Object.freeze({ ...request(), sessionId: `session-${index}`, clientRequestId: `key-${index}` }), signal, capability, () => {});
  await store.submit(Object.freeze({ ...request(), sessionId: "full" }), signal, capability, result => assert.equal(result.status, "capacity"));
  assert.equal(calls, 256);
});
