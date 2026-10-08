import assert from "node:assert/strict";
import test from "node:test";
import type { SessionAdmission, SessionReceiptPage, SessionRuntimeStateResponse, SessionSteerRequest } from "#neoastra";
import { createMutationCapability } from "./sessionOperations";
import { captureSteering, createSteeringSubmissions } from "./sessionSteering";

const observation: SessionRuntimeStateResponse = {
  status: "ok", hostEpoch: "epoch", sessionId: "session", runtimeInstanceId: "runtime", coordinatorTransitionInProgress: false,
  entry: { attachmentGeneration: "9223372036854775807", activeRunId: "run", isTerminated: false, isRetiring: false,
    backgroundTasks: [], queueDrainInProgress: false, providerId: "fake", providerKey: "fake", modelId: null, reasoningEffort: null, agentPromptId: null, pendingAgentPromptId: null, activity: null },
};
const signal = new AbortController().signal;
function request(key = "key") { return captureSteering("epoch", "session", observation, "  exact\ntext  ", key)!; }
function page(kind = "Steer", key = "key"): SessionReceiptPage {
  return { status: "ok", epoch: "epoch", next: null, rows: [{ clientRequestId: key, sessionId: "session", operationId: "operation",
    targetOperationId: null, kind, state: "pending", outcome: null, code: null, runId: null, queueInsertion: null }] };
}
function finiteJoin<T>(task: Promise<T>): Promise<T> {
  let timer: ReturnType<typeof setTimeout>;
  const deadline = new Promise<never>((_, reject) => { timer = setTimeout(() => reject(new Error("Fixture join timed out")), 5_000); });
  return Promise.race([task, deadline]).finally(() => clearTimeout(timer));
}

test("steering captures only an explicit available observation with immutable exact identities", () => {
  const captured = request();
  assert.equal(Object.isFrozen(captured), true);
  assert.deepEqual(captured, { expectedEpoch: "epoch", clientRequestId: "key", sessionId: "session", expectedRuntimeInstanceId: "runtime",
    expectedAttachmentGeneration: "9223372036854775807", expectedRunId: "run", text: "  exact\ntext  " });
  for (const unavailable of [undefined, { ...observation, hostEpoch: "old" }, { ...observation, sessionId: "other" },
    { ...observation, coordinatorTransitionInProgress: true }, { ...observation, entry: null },
    { ...observation, entry: { ...observation.entry!, activeRunId: null } },
    { ...observation, entry: { ...observation.entry!, isRetiring: true } },
    { ...observation, entry: { ...observation.entry!, isTerminated: true } }])
    assert.equal(captureSteering("epoch", "session", unavailable, "text", "key"), null);
});

test("uncertain steering retains exact object across remount and refuses a newly observed target", async () => {
  const seen: SessionSteerRequest[] = [];
  const store = createSteeringSubmissions(async value => { seen.push(value); throw new Error("uncertain"); });
  const capability = createMutationCapability("epoch");
  const original = request();
  await store.submit(original, signal, capability, () => {});
  const remounted = store.pending("session")!;
  assert.equal(remounted.request, original);
  await store.submit(request("different-key"), signal, capability, () => {});
  assert.equal(seen.length, 1);
  await store.submit(remounted.request, signal, capability, () => {});
  assert.equal(seen.length, 2);
  assert.equal(seen[0], seen[1]);
  assert.equal(store.reconcile("session", page("Send"), capability), false);
  assert.equal(store.reconcile("session", page("Abort"), capability), false);
  assert.equal(store.reconcile("session", page("Steer", "other"), capability), false);
  assert.equal(store.reconcile("session", page("Steer"), capability), true);
  assert.equal(store.pending("session"), undefined);
});

test("double clicks latch synchronously and selection-lost admission stays retained", async () => {
  let resolve!: (value: SessionAdmission) => void;
  const response = new Promise<SessionAdmission>(done => { resolve = done; });
  const work: Promise<unknown>[] = [];
  const selection = new AbortController();
  const capability = createMutationCapability("epoch");
  const published: unknown[] = [];
  let calls = 0;
  const store = createSteeringSubmissions(async () => { calls++; return response; });
  const original = request();
  const failures: unknown[] = [];
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
    const settled = await Promise.allSettled([...work, response].map(finiteJoin));
    failures.push(...settled.filter((value): value is PromiseRejectedResult => value.status === "rejected").map(value => value.reason));
  }
  if (failures.length) throw new AggregateError(failures, "Fixture failed or cleanup unconfirmed");
});

test("late stale epoch invalidates the shared capability across selections without losing the request", async () => {
  const selection = new AbortController();
  const capability = createMutationCapability("epoch");
  let calls = 0;
  const store = createSteeringSubmissions(async () => {
    calls++; selection.abort();
    return { status: "stale_epoch", epoch: "new", receipt: null };
  });
  const original = request();
  await store.submit(original, selection.signal, capability, () => assert.fail("Obsolete selection published"));
  assert.equal(capability.canMutate(), false);
  await store.submit(original, signal, capability, () => {});
  assert.equal(calls, 1);
  assert.equal(store.pending("session")!.request, original);
  assert.equal(store.reconcile("session", page(), capability), false);
});

test("wrong admission kind is uncertain; accepted steering receipt clears only its exact request", async () => {
  let kind = "Send";
  const store = createSteeringSubmissions(async () => ({ status: "accepted", epoch: "epoch", receipt: page(kind).rows[0] }));
  const capability = createMutationCapability("epoch");
  const original = request();
  await store.submit(original, signal, capability, () => {});
  assert.equal(store.pending("session")!.request, original);
  kind = "Steer";
  await store.submit(original, signal, capability, () => {});
  assert.equal(store.pending("session"), undefined);
});

test("uncertain retention is bounded and pre-cancelled attempts do not invoke", async () => {
  let calls = 0;
  const store = createSteeringSubmissions(async () => { calls++; throw new Error("uncertain"); });
  const capability = createMutationCapability("epoch");
  const cancelled = new AbortController(); cancelled.abort();
  await store.submit(request(), cancelled.signal, capability, () => {});
  assert.equal(calls, 0);
  for (let index = 0; index < 256; index++)
    await store.submit(Object.freeze({ ...request(), sessionId: `session-${index}`, clientRequestId: `key-${index}` }), signal, capability, () => {});
  await store.submit(request(), signal, capability, () => {});
  assert.equal(calls, 256);
  assert.equal(store.pending("session"), undefined);
});

test("a steering key whose receipt the host no longer keeps is a definite answer", async () => {
  let status = "admission_failed";
  const store = createSteeringSubmissions(async () => ({ status, epoch: "epoch", receipt: null }));
  const capability = createMutationCapability("epoch");
  const original = request();
  await store.submit(original, signal, capability, () => {});
  assert.equal(store.pending("session")!.request, original);
  status = "expired";
  const published: string[] = [];
  await store.submit(original, signal, capability, value => published.push(value.status));
  assert.deepEqual(published, ["expired"]);
  assert.equal(store.pending("session"), undefined);
});
