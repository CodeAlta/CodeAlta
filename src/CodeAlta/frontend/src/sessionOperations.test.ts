import assert from "node:assert/strict";
import test from "node:test";
import { captureSubmission, createMutationCapability, sendSubmission, refreshSubmissions } from "./sessionOperations";
import type { SessionAdmission, SessionReceiptPage } from "#neoastra";

const signal = { aborted: false } as AbortSignal;
const page: SessionReceiptPage = { status: "ok", epoch: "epoch", rows: [], next: null };

function finiteJoin<T>(task: Promise<T>): Promise<T> {
  let timer: ReturnType<typeof setTimeout>;
  const deadline = new Promise<never>((_, reject) => { timer = setTimeout(() => reject(new Error("Fixture join timed out")), 5_000); });
  return Promise.race([task, deadline]).finally(() => clearTimeout(timer));
}

test("uncertain retry preserves epoch key session and exact text", async () => {
  const request = captureSubmission("epoch", "session", "  exact\ntext  ", "key");
  const capability = createMutationCapability("epoch");
  const seen: unknown[] = [];
  const invoke = async (value: typeof request): Promise<SessionAdmission> => {
    seen.push(value);
    throw new Error("uncertain transport");
  };
  await sendSubmission(invoke, request, signal, () => {}, capability);
  await sendSubmission(invoke, request, signal, () => {}, capability);
  assert.equal(seen[0], seen[1]);
  assert.equal(request.text, "  exact\ntext  ");
  assert.equal(request.expectedEpoch, "epoch");
  assert.equal(request.clientRequestId, "key");
  assert.equal(request.sessionId, "session");
  assert.equal(Object.isFrozen(request), true);
});

test("stale selection and unmount suppress callbacks", async () => {
  let aborted = false;
  const current = { get aborted() { return aborted; } } as AbortSignal;
  let resolve!: (value: SessionAdmission) => void;
  const result = new Promise<SessionAdmission>(done => { resolve = done; });
  const published: unknown[] = [];
  const capability = createMutationCapability("epoch");
  const work = sendSubmission(async () => result, captureSubmission("epoch", "old", "text", "key"), current, value => published.push(value), capability);
  let reject!: (error: Error) => void;
  const failure = new Promise<SessionAdmission>((_, fail) => { reject = fail; });
  const failureObservation = failure.then(() => null, error => error);
  const lateFailure = sendSubmission(async () => failure, captureSubmission("epoch", "old", "text", "other"), current, value => published.push(value), capability);
  const expectedFailure = new Error("late transport failure");
  const failures: unknown[] = [];
  try {
    aborted = true;
    resolve({ status: "stale_epoch", epoch: "new", receipt: null });
    reject(expectedFailure);
    const waits = [finiteJoin(work), finiteJoin(lateFailure), finiteJoin(result), finiteJoin(failureObservation)];
    await Promise.all(waits);
    assert.deepEqual(published, []);
    assert.equal(capability.canMutate(), true);
    assert.equal(await failureObservation, expectedFailure);
  } catch (error) {
    failures.push(error);
  } finally {
    aborted = true;
    resolve({ status: "stale_epoch", epoch: "new", receipt: null });
    reject(expectedFailure);
    const cleanup = [finiteJoin(work), finiteJoin(lateFailure), finiteJoin(result), finiteJoin(failureObservation)];
    const settled = await Promise.allSettled(cleanup);
    failures.push(...settled.filter((value): value is PromiseRejectedResult => value.status === "rejected").map(value => value.reason));
  }
  if (failures.length) throw new AggregateError(failures, "Fixture failed or cleanup unconfirmed");
});

test("epoch mismatch never resends", async () => {
  let calls = 0;
  const published: unknown[] = [];
  const request = captureSubmission("old", "session", "  exact\ntext  ", "key");
  const capability = createMutationCapability("old");
  const invoke = async (): Promise<SessionAdmission> => { calls++; return { status: "stale_epoch", epoch: "new", receipt: null }; };
  await sendSubmission(invoke, request, signal, value => published.push(value), capability);
  assert.equal(capability.canMutate(), false);
  assert.equal(capability.canSubmit(request), false);
  // An explicit retry must not invoke transport even though boot still says "old".
  await sendSubmission(invoke, request, signal, value => published.push(value), capability);
  capability.observe({ status: "ok", epoch: "old" });
  await sendSubmission(invoke, request, signal, value => published.push(value), capability);
  const replacementPresentation = { sessionId: "session", capability }; // Selection away/back reuses App's epoch owner.
  assert.equal(replacementPresentation.capability.canSubmit(request), false);
  await sendSubmission(invoke, request, signal, value => published.push(value), replacementPresentation.capability);
  assert.equal(calls, 1);
  assert.equal(published.length, 1);
  assert.deepEqual(request, { expectedEpoch: "old", sessionId: "session", text: "  exact\ntext  ", clientRequestId: "key" });
});

test("receipt recovery exposes no prompt text", async () => {
  const published: unknown[] = [];
  const recovered: SessionReceiptPage = { ...page, rows: [{ clientRequestId: "key", sessionId: "session", operationId: "operation", targetOperationId: null, kind: "Send", state: "terminal", outcome: "Completed", code: null, runId: "run" }] };
  await refreshSubmissions(async () => recovered, "epoch", 0, signal, value => published.push(value));
  assert.deepEqual(published, [recovered]);
  assert.equal(JSON.stringify(published).includes("text"), false);
});

test("refresh is explicit and never submits", async () => {
  let calls = 0;
  const capability = createMutationCapability("epoch");
  assert.equal(calls, 0);
  await refreshSubmissions(async () => { calls++; return { ...page, status: "stale_epoch", epoch: "new" }; }, "epoch", 0, signal, value => capability.observe(value));
  assert.equal(calls, 1);
  assert.equal(capability.canMutate(), false);
  await sendSubmission(async () => { calls++; return { status: "accepted", epoch: "epoch", receipt: null }; },
    captureSubmission("epoch", "session", "text", "key"), signal, () => {}, capability);
  assert.equal(calls, 1);
});
