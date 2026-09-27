import assert from "node:assert/strict";
import test from "node:test";
import { createMutationCapability } from "./sessionOperations";
import { createQueueSubmissions } from "./sessionQueue";
import { retainedQueueEvidence } from "./retainedQueueEvidence";
import type { SessionAdmission } from "#neoastra";

const epoch = "host";
const request = { expectedEpoch: epoch, sessionId: "session", clientRequestId: "original-key",
  expectedRuntimeInstanceId: "12345678-1234-1234-1234-123456789abc", expectedAttachmentGeneration: "1", text: " literal\n queue text " };

test("review projects only the original pending/unknown queue intent and never reads or retries", async () => {
  let calls = 0;
  let fail!: (error: Error) => void;
  const owner = createQueueSubmissions(() => { calls++; return new Promise((_, reject) => { fail = reject; }); }, async () => { throw new Error("no cancellation"); });
  assert.equal(retainedQueueEvidence(owner, epoch, "session"), null);
  const work = owner.submit(request, new AbortController().signal, createMutationCapability(epoch), () => {});
  const pending = retainedQueueEvidence(owner, epoch, "session")!;
  assert.equal(pending.queue?.request.text, request.text);
  assert.equal(pending.queue?.inFlight, true);
  assert.equal(retainedQueueEvidence(owner, "other-host", "session"), null);
  assert.equal(retainedQueueEvidence(owner, epoch, "other-session"), null);
  fail(new Error("lost response")); await work;
  const uncertain = retainedQueueEvidence(owner, epoch, "session")!;
  assert.equal(uncertain.queue?.request, pending.queue?.request);
  assert.equal(uncertain.queue?.inFlight, false);
  assert.notEqual(uncertain.revision, pending.revision);
  assert.equal(calls, 1);
});

test("cancellation-only originals preserve exact target/key and uncertainty without queue text", async () => {
  let calls = 0;
  let fail!: (error: Error) => void;
  const owner = createQueueSubmissions(async () => { throw new Error("no queue"); }, () => {
    calls++; return new Promise((_, reject) => { fail = reject; });
  });
  const intent = { sessionId: "session", request: { expectedEpoch: epoch, clientRequestId: "cancel-original",
    targetOperationId: "abcdefab-1234-5678-9abc-abcdefabcdef" } };
  const capability = createMutationCapability(epoch);
  const work = owner.cancel(intent, new AbortController().signal, capability, () => {});
  const pending = retainedQueueEvidence(owner, epoch, "session")!;
  assert.equal(pending.queue, undefined);
  assert.deepEqual(pending.cancellations[0].intent, intent);
  assert.equal(pending.cancellations[0].inFlight, true);
  assert.equal(retainedQueueEvidence(owner, "wrong", "session"), null);
  assert.equal(retainedQueueEvidence(owner, epoch, "wrong"), null);
  fail(new Error("lost cancellation")); await work;
  const unknown = retainedQueueEvidence(owner, epoch, "session")!;
  assert.equal(unknown.cancellations[0].intent, pending.cancellations[0].intent);
  assert.equal(unknown.cancellations[0].inFlight, false);
  assert.notEqual(unknown.revision, pending.revision);
  assert.equal(calls, 1);
});

test("definite settlement removes originals without reconstructing settled text or cancellation inventory", async () => {
  let finishQueue!: (value: SessionAdmission) => void;
  let finishCancel!: (value: SessionAdmission) => void;
  const owner = createQueueSubmissions(() => new Promise(resolve => { finishQueue = resolve; }),
    () => new Promise(resolve => { finishCancel = resolve; }));
  const capability = createMutationCapability(epoch);
  const signal = new AbortController().signal;
  const queue = owner.submit(request, signal, capability, () => {});
  const cancel = owner.cancel({ sessionId: "session", request: { expectedEpoch: epoch, clientRequestId: "cancel-key",
    targetOperationId: "abcdefab-1234-5678-9abc-abcdefabcdef" } }, signal, capability, () => {});
  const both = retainedQueueEvidence(owner, epoch, "session")!;
  assert.equal(both.queue?.request.text, request.text);
  assert.equal(both.cancellations.length, 1);
  finishQueue({ status: "busy", epoch, receipt: null }); await queue;
  const cancellationOnly = retainedQueueEvidence(owner, epoch, "session")!;
  assert.equal(cancellationOnly.queue, undefined);
  assert.equal(cancellationOnly.cancellations.length, 1);
  assert.notEqual(cancellationOnly.revision, both.revision);
  finishCancel({ status: "unknowntarget", epoch, receipt: null }); await cancel;
  assert.equal(retainedQueueEvidence(owner, epoch, "session"), null);
});
