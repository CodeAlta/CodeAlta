import assert from "node:assert/strict";
import test from "node:test";
import type { SessionAdmission, SessionCompactRequest, SessionReceiptPage, SessionRuntimeStateResponse } from "#neoastra";
import { createMutationCapability } from "./sessionOperations";
import { captureCompaction, createCompactionSubmissions } from "./sessionCompaction";

const observation: SessionRuntimeStateResponse = {
  status: "ok", hostEpoch: "epoch", sessionId: "session", runtimeInstanceId: "runtime", coordinatorTransitionInProgress: false,
  entry: { attachmentGeneration: "9223372036854775807", activeRunId: null, isTerminated: false, isRetiring: false,
    queueDrainInProgress: false, providerId: "fake", providerKey: "fake", modelId: null, reasoningEffort: null, agentPromptId: null, pendingAgentPromptId: null, activity: null },
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
