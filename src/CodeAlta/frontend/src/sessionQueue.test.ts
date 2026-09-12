import assert from "node:assert/strict";
import test from "node:test";
import type { SessionAdmission, SessionQueueRequest, SessionReceiptPage, SessionReceiptView, SessionRuntimeStateResponse } from "#neoastra";
import { createMutationCapability } from "./sessionOperations";
import { captureQueue, captureQueueCancellation, createQueueSubmissions, queueReceiptPhases, queueCancellationStatus } from "./sessionQueue";

const operation = "abcdefab-1234-5678-9abc-abcdefabcdef";
const otherOperation = "abcdefab-1234-5678-9abc-abcdefabcdee";
function observation(): SessionRuntimeStateResponse {
  return { status: "ok", hostEpoch: "epoch", sessionId: "session", runtimeInstanceId: operation, coordinatorTransitionInProgress: false,
    entry: { attachmentGeneration: "9223372036854775807", activeRunId: "earlier", isTerminated: false, isRetiring: false,
      queueDrainInProgress: true, providerId: "inert", providerKey: "inert", modelId: null, reasoningEffort: null,
      agentPromptId: null, pendingAgentPromptId: null } };
}
function request(): SessionQueueRequest { return captureQueue("epoch", "session", observation(), " exact text \n", "key")!; }
function row(): SessionReceiptView {
  return { clientRequestId: "key", sessionId: "session", operationId: operation, targetOperationId: null, kind: "Queue",
    state: "pending", outcome: null, code: null, runId: null, queueInsertion: { state: "pending", accepted: null, code: null } };
}
function page(value = row()): SessionReceiptPage { return { status: "ok", epoch: "epoch", rows: [value], next: null }; }
function cancelIntent() { const value = page(); return captureQueueCancellation("epoch", "session", value, value.rows[0], "cancel")!; }
function cancelRow(): SessionReceiptView {
  return { ...row(), kind: "CancelQueue", clientRequestId: "cancel", operationId: otherOperation, targetOperationId: operation, queueInsertion: null };
}
async function retained(body: (work: Promise<unknown>[], release: (action: () => void) => void) => Promise<void>) {
  const work: Promise<unknown>[] = [];
  const releases: (() => void)[] = [];
  const failures: unknown[] = [];
  const bounded = async (task: Promise<unknown>) => {
    let timer: ReturnType<typeof setTimeout> | undefined;
    try { await Promise.race([task, new Promise<never>((_, reject) => { timer = setTimeout(() => reject(new Error("Permanent fixture timeout")), 5_000); })]); }
    finally { clearTimeout(timer); }
  };
  const original = body(work, action => releases.push(action));
  work.push(original);
  try { await bounded(original); } catch (error) { failures.push(error); }
  finally {
    for (const release of releases) { try { release(); } catch (error) { failures.push(error); } }
    const joins = await Promise.allSettled(work.map(bounded));
    failures.push(...joins.filter((value): value is PromiseRejectedResult => value.status === "rejected").map(value => value.reason));
  }
  if (failures.length) throw new AggregateError(failures, "Fixture failed; original work retained", { cause: { work, original, releases } });
}

test("queue capture freezes exact busy/draining intent and rejects unavailable or malformed observations", () => {
  const captured = request();
  assert.ok(captured); assert.ok(Object.isFrozen(captured));
  assert.equal(captured.text, " exact text \n"); assert.equal(captured.expectedAttachmentGeneration, "9223372036854775807");
  assert.equal("expectedRunId" in captured, false);
  const observed = observation();
  assert.ok(captureQueue("epoch", "session", { ...observed, entry: { ...observed.entry!, activeRunId: null } }, "text", "key"));
  assert.ok(captureQueue("epoch", "session", { ...observed, entry: { ...observed.entry!, queueDrainInProgress: false } }, "text", "key"));
  for (const invalid of [undefined, { ...observed, hostEpoch: "old" }, { ...observed, sessionId: "other" },
    { ...observed, status: "unavailable" }, { ...observed, coordinatorTransitionInProgress: true }, { ...observed, entry: null },
    { ...observed, runtimeInstanceId: "bad" }, { ...observed, runtimeInstanceId: operation.toUpperCase() },
    ...["0", "01", "+1", "9223372036854775808", "1 "].map(attachmentGeneration => ({ ...observed, entry: { ...observed.entry!, attachmentGeneration } })),
    { ...observed, entry: { ...observed.entry!, attachmentGeneration: 1 as unknown as string } },
    { ...observed, entry: { ...observed.entry!, isRetiring: true } }, { ...observed, entry: { ...observed.entry!, isTerminated: true } },
    { ...observed, entry: { ...observed.entry!, pendingAgentPromptId: "plan" } }])
    assert.equal(captureQueue("epoch", "session", invalid, "text", "key"), null);
  for (const text of [" ", "\ud800", "x".repeat(32769)]) assert.equal(captureQueue("epoch", "session", observed, text, "key"), null);
  assert.equal(captureQueue("epoch", "session", observed, "text", " padded "), null);
});

test("queue labels keep reservation insertion and execution separate including racing refusal snapshots", () => {
  assert.deepEqual(queueReceiptPhases(row()), ["Owner reservation accepted", "Host-only insertion pending", "Execution/cleanup pending"]);
  const accepted = { ...row(), queueInsertion: { state: "terminal", accepted: true, code: "queue_accepted" } };
  assert.deepEqual(queueReceiptPhases(accepted), ["Owner reservation accepted", "Retained IN THIS HOST ONLY; not durable", "Execution/cleanup pending"]);
  assert.equal(queueReceiptPhases({ ...accepted, state: "terminal", outcome: "Completed", code: "queue_dispatched", runId: "run" })?.[2],
    "Dispatch/cleanup settled; run completion is not confirmed");
  const refused = { ...row(), queueInsertion: { state: "terminal", accepted: false, code: "queue_target_unavailable" } };
  assert.ok(queueReceiptPhases(refused));
  assert.ok(queueReceiptPhases({ ...refused, state: "terminal", outcome: "Failed", code: "queue_cleanup_failed" }));
  assert.equal(queueReceiptPhases({ ...row(), state: "terminal", outcome: "Failed", code: "queue_failed" }), null);
  assert.equal(queueReceiptPhases({ ...accepted, state: "terminal", outcome: "Completed", code: "queue_accepted", runId: "run" }), null);
  assert.equal(queueCancellationStatus(cancelRow()), "Cancellation reserved; execution/cleanup pending");
  assert.equal(queueCancellationStatus({ ...cancelRow(), state: "terminal", outcome: "Completed", code: "queue_cancellation_signalled" }),
    "Cancellation signalled; not rollback, cleanup of the target, or run completion");
  assert.equal(queueCancellationStatus({ ...cancelRow(), state: "terminal", outcome: "Completed", code: "queue_dispatched" }), null);
});

test("queue uncertainty survives remount and only explicit exact retry reuses immutable text and target", async () => {
  const seen: SessionQueueRequest[] = [];
  const store = createQueueSubmissions(async value => { seen.push(value); throw new Error("lost"); }, async () => { throw new Error("unused"); });
  const capability = createMutationCapability("epoch"); const signal = new AbortController().signal;
  await store.submit(request(), signal, capability, () => {});
  const original = store.pending("session")!.request;
  await store.submit({ ...original, text: "different", clientRequestId: "different" }, signal, capability, () => {});
  assert.equal(seen.length, 1);
  await store.submit(store.pending("SESSION")!.request, signal, capability, () => {});
  assert.equal(seen.length, 2); assert.equal(seen[0], seen[1]); assert.equal(seen[1].text, " exact text \n");
});

test("queue cancellation captures the original operation and session rather than later selection or run", async () => {
  const intent = cancelIntent(); assert.ok(intent); assert.ok(Object.isFrozen(intent)); assert.ok(Object.isFrozen(intent.request));
  assert.equal(intent.sessionId, "session"); assert.equal(intent.request.targetOperationId, operation);
  const seen: unknown[] = [];
  const store = createQueueSubmissions(async () => { throw new Error("unused"); }, async value => { seen.push(value); throw new Error("lost"); });
  const capability = createMutationCapability("epoch"); const signal = new AbortController().signal;
  await store.cancel(intent, signal, capability, () => {});
  await store.cancel({ ...intent, sessionId: "different" }, signal, capability, () => {});
  assert.equal(seen.length, 1);
  await store.cancel(store.cancellations("session")[0].intent, signal, capability, () => {});
  assert.equal(seen[0], seen[1]);
  for (const kind of ["Send", "Abort", "AbortRun", "CancelQueue"]) {
    const value = page({ ...row(), kind });
    assert.equal(captureQueueCancellation("epoch", "session", value, value.rows[0], "key"), null);
  }
  assert.equal(captureQueueCancellation("epoch", "other", page(), row(), "key"), null);
});

test("queue and cancel synchronous latches survive selection cancellation until original waiters settle", () => retained(async (work, release) => {
  let finishQueue!: (value: SessionAdmission) => void; let finishCancel!: (value: SessionAdmission) => void;
  const queueResponse = new Promise<SessionAdmission>(resolve => { finishQueue = resolve; });
  const cancelResponse = new Promise<SessionAdmission>(resolve => { finishCancel = resolve; });
  work.push(queueResponse, cancelResponse);
  const selection = new AbortController();
  release(() => selection.abort());
  release(() => finishQueue({ status: "accepted", epoch: "epoch", receipt: row() }));
  release(() => finishCancel({ status: "accepted", epoch: "epoch", receipt: cancelRow() }));
  let queues = 0; let cancels = 0;
  const store = createQueueSubmissions(async () => { queues++; return queueResponse; }, async () => { cancels++; return cancelResponse; });
  const capability = createMutationCapability("epoch"); const original = request(); const intent = cancelIntent();
  const publish = () => assert.fail("Obsolete selection published");
  const a = store.submit(original, selection.signal, capability, publish); const b = store.cancel(intent, selection.signal, capability, publish);
  work.push(a, b, store.submit(original, selection.signal, capability, publish), store.cancel(intent, selection.signal, capability, publish));
  assert.equal(queues, 1); assert.equal(cancels, 1); selection.abort();
  assert.deepEqual(store.reconcile("session", page(), capability), { queueRecovered: false, cancellationsRecovered: 0 });
  assert.deepEqual(store.reconcile("session", page(cancelRow()), capability), { queueRecovered: false, cancellationsRecovered: 0 });
  assert.equal(store.pending("session")!.inFlight, true);
  assert.equal(store.cancellations("session")[0].inFlight, true);
  finishQueue({ status: "accepted", epoch: "epoch", receipt: row() }); finishCancel({ status: "accepted", epoch: "epoch", receipt: cancelRow() });
  await Promise.all([a, b]);
  assert.ok(store.pending("session")); assert.equal(store.cancellations("session").length, 1);
  assert.deepEqual(store.reconcile("session", page(), capability), { queueRecovered: true, cancellationsRecovered: 0 });
  assert.deepEqual(store.reconcile("session", page(cancelRow()), capability), { queueRecovered: false, cancellationsRecovered: 1 });
}));

test("queue reconciliation requests text clearing only for recovered queue intent, never cancellation-only recovery", async () => {
  const signal = new AbortController().signal;
  for (const scenario of ["cancellation-only", "queue-only", "both"] as const) {
    const capability = createMutationCapability("epoch");
    const store = createQueueSubmissions(async () => {
      if (scenario === "cancellation-only") return { status: "accepted", epoch: "epoch", receipt: row() };
      throw new Error("lost queue response");
    }, async () => { throw new Error("lost cancellation response"); });
    await store.submit(request(), signal, capability, () => {});
    if (scenario !== "queue-only") await store.cancel(cancelIntent(), signal, capability, () => {});
    // In the repro the old queue was admitted, leaving an editable, unrelated user draft.
    if (scenario === "cancellation-only") assert.equal(store.pending("session"), undefined);
    else assert.equal(store.pending("session")!.request.text, " exact text \n");
    const recovered = store.reconcile("session", { ...page(), rows: [row(), cancelRow()] }, capability);
    // This is the production result consumed directly by the panel's text-clear condition.
    assert.deepEqual(recovered, { queueRecovered: scenario !== "cancellation-only", cancellationsRecovered: scenario === "queue-only" ? 0 : 1 });
    assert.equal(store.pending("session"), undefined);
    assert.equal(store.cancellations("session").length, 0);
    assert.deepEqual(store.reconcile("session", { ...page(), rows: [row(), cancelRow()] }, capability),
      { queueRecovered: false, cancellationsRecovered: 0 });
  }
});

test("queue reconciliation requires exact epoch kind key session and cancellation target with valid phases", async () => {
  const store = createQueueSubmissions(async () => { throw new Error("lost"); }, async () => { throw new Error("lost"); });
  const capability = createMutationCapability("epoch"); const signal = new AbortController().signal;
  await store.submit(request(), signal, capability, () => {}); await store.cancel(cancelIntent(), signal, capability, () => {});
  const originalQueue = store.pending("session")!.request;
  const originalCancel = store.cancellations("session")[0].intent;
  for (const wrong of [{ ...row(), clientRequestId: "other" }, { ...row(), sessionId: "other" }, { ...row(), kind: "Send", queueInsertion: null },
    { ...row(), operationId: "invalid" }, { ...row(), state: "terminal", outcome: "Failed", code: "queue_failed" },
    { ...cancelRow(), targetOperationId: otherOperation }]) {
    assert.deepEqual(store.reconcile("session", page(wrong), capability), { queueRecovered: false, cancellationsRecovered: 0 });
    assert.equal(store.pending("session")!.request, originalQueue);
    assert.equal(store.cancellations("session")[0].intent, originalCancel);
  }
  assert.deepEqual(store.reconcile("session", { ...page(), next: 1 }, capability), { queueRecovered: false, cancellationsRecovered: 0 });
  assert.equal(store.pending("session")!.request, originalQueue);
  assert.equal(store.cancellations("session")[0].intent, originalCancel);
  assert.deepEqual(store.reconcile("session", page(), capability), { queueRecovered: true, cancellationsRecovered: 0 });
  assert.deepEqual(store.reconcile("session", page(cancelRow()), capability), { queueRecovered: false, cancellationsRecovered: 1 });
  await store.submit(request(), signal, capability, () => {});
  await store.cancel(cancelIntent(), signal, capability, () => {});
  const staleQueue = store.pending("session")!.request;
  const staleCancel = store.cancellations("session")[0].intent;
  assert.deepEqual(store.reconcile("session", { ...page(), epoch: "other-epoch", rows: [row(), cancelRow()] }, capability),
    { queueRecovered: false, cancellationsRecovered: 0 });
  assert.equal(store.pending("session")!.request, staleQueue);
  assert.equal(store.cancellations("session")[0].intent, staleCancel);
  assert.equal(capability.canMutate(), false); assert.ok(store.pending("session"));
});

test("queue late epoch mismatch disables shared mutation without stale publication or rebasing", async () => {
  const selection = new AbortController(); const capability = createMutationCapability("epoch"); let calls = 0;
  const store = createQueueSubmissions(async () => { calls++; selection.abort(); return { status: "stale_epoch", epoch: "new", receipt: null }; },
    async () => { throw new Error("unused"); });
  await store.submit(request(), selection.signal, capability, () => assert.fail("Stale publication"));
  assert.equal(capability.canMutate(), false);
  await store.submit(store.pending("session")!.request, new AbortController().signal, capability, () => {});
  assert.equal(calls, 1); assert.deepEqual(store.reconcile("session", page(), capability), { queueRecovered: false, cancellationsRecovered: 0 });
  const cancelSelection = new AbortController(); const cancelCapability = createMutationCapability("epoch");
  const cancelStore = createQueueSubmissions(async () => { throw new Error("unused"); }, async () => {
    cancelSelection.abort(); return { status: "stale_epoch", epoch: "new", receipt: null };
  });
  await cancelStore.cancel(cancelIntent(), cancelSelection.signal, cancelCapability, () => assert.fail("Stale cancellation publication"));
  assert.equal(cancelCapability.canMutate(), false); assert.equal(cancelStore.cancellations("session").length, 1);
});

test("queue combined bound precancellation and malformed responses retain uncertainty except definite rejection", async () => {
  let calls = 0;
  let response: SessionAdmission = { status: "accepted", epoch: "epoch", receipt: { ...row(), kind: "Send", queueInsertion: null } };
  const store = createQueueSubmissions(async () => { calls++; return response; }, async () => { calls++; return response; });
  const capability = createMutationCapability("epoch"); const cancelled = new AbortController(); cancelled.abort();
  await store.submit(request(), cancelled.signal, capability, () => {}); assert.equal(calls, 0);
  const signal = new AbortController().signal;
  for (const status of ["accepted", "admission_failed", "wire_limit"]) {
    response = { ...response, status };
    await store.submit(store.pending("session")?.request ?? request(), signal, capability, () => {});
    assert.ok(store.pending("session"));
  }
  for (const receipt of [{ ...row(), queueInsertion: null }, { ...row(), operationId: "malformed" },
    { ...row(), sessionId: "other" }, { ...row(), clientRequestId: "other" },
    { ...row(), state: "terminal", outcome: "Failed", code: "queue_failed" }]) {
    response = { status: "accepted", epoch: "epoch", receipt };
    await store.submit(store.pending("session")!.request, signal, capability, () => {});
    assert.ok(store.pending("session"));
  }
  await store.cancel(cancelIntent(), signal, capability, () => {}); assert.equal(store.cancellations("session").length, 1);
  for (let index = 2; index < 256; index++)
    await store.submit(Object.freeze({ ...request(), sessionId: `session-${index}`, clientRequestId: `key-${index}` }), signal, capability, () => {});
  const before = calls;
  await store.submit(Object.freeze({ ...request(), sessionId: "full" }), signal, capability, value => assert.equal(value.status, "capacity"));
  assert.equal(calls, before);
  response = { status: "unknowntarget", epoch: "epoch", receipt: null };
  await store.cancel(store.cancellations("session")[0].intent, signal, capability, () => {});
  assert.equal(store.cancellations("session").length, 0);
  response = { status: "busy", epoch: "epoch", receipt: row() }; // Contradictory rejection is not proof of non-admission.
  await store.submit(store.pending("session")!.request, signal, capability, () => {}); assert.ok(store.pending("session"));
  response = { status: "accepted", epoch: "epoch", receipt: row() };
  await store.submit(store.pending("session")!.request, signal, capability, () => {}); assert.equal(store.pending("session"), undefined);
  response = { status: "accepted", epoch: "epoch", receipt: { ...cancelRow(), targetOperationId: otherOperation } };
  await store.cancel(cancelIntent(), signal, capability, () => {}); assert.equal(store.cancellations("session").length, 1);
  response = { status: "accepted", epoch: "epoch", receipt: cancelRow() };
  await store.cancel(store.cancellations("session")[0].intent, signal, capability, () => {}); assert.equal(store.cancellations("session").length, 0);
});
