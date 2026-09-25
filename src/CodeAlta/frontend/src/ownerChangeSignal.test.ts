import assert from "node:assert/strict";
import test from "node:test";
import { createOwnerChangeSignal } from "./ownerChangeSignal";
import { createMutationCapability, createOwnedSubmissions, captureSubmission } from "./sessionOperations";
import { createSteeringSubmissions } from "./sessionSteering";
import type { SessionReceiptPage } from "#neoastra";

test("owner change isolates a throwing listener, including notifications to later subscribers", () => {
  const change = createOwnerChangeSignal();
  const seen: number[] = [];
  change.subscribe(() => { throw new Error("presentation failed"); });
  change.subscribe(() => seen.push(change.getSnapshot()));
  change.changed();
  change.subscribe(() => seen.push(change.getSnapshot()));
  change.changed();
  assert.deepEqual(seen, [1, 2, 2]);
});

test("throwing subscriber cannot strand Send or Steer before transport starts", async () => {
  const capability = createMutationCapability("epoch");
  const signal = new AbortController().signal;
  let sends = 0;
  const sendStore = createOwnedSubmissions(async () => { sends++; throw new Error("lost"); }, async () => { throw new Error("unused"); });
  let steers = 0;
  const steerStore = createSteeringSubmissions(async () => { steers++; throw new Error("lost"); });
  const send = captureSubmission("epoch", "one", "exact", "send")!;
  const steer = { expectedEpoch: "epoch", sessionId: "one", clientRequestId: "steer",
    expectedRuntimeInstanceId: "runtime", expectedAttachmentGeneration: "1", expectedRunId: "run", text: "exact" };
  const receipt = (kind: "Send" | "Steer", key: string): SessionReceiptPage => ({ status: "ok", epoch: "epoch", next: null,
    rows: [{ kind, clientRequestId: key, sessionId: "one", operationId: "33333333-3333-3333-3333-333333333333",
      targetOperationId: null, state: "pending", outcome: null, code: null, runId: null, queueInsertion: null }] });
  for (const owner of [
    { store: sendStore, invoke: () => sendStore.submit(send, signal, capability, () => {}), pending: () => sendStore.pending("one"), calls: () => sends,
      recover: () => sendStore.reconcile("one", receipt("Send", "send"), capability) },
    { store: steerStore, invoke: () => steerStore.submit(steer, signal, capability, () => {}), pending: () => steerStore.pending("one"), calls: () => steers,
      recover: () => steerStore.reconcile("one", receipt("Steer", "steer"), capability) },
  ]) {
    const seen: number[] = [];
    owner.store.subscribe(() => { throw new Error("presentation failed"); });
    owner.store.subscribe(() => seen.push(owner.store.getSnapshot()));
    await owner.invoke();
    assert.equal(owner.calls(), 1);
    assert.equal(owner.pending()?.inFlight, false);
    assert.ok(owner.pending(), "uncertain original remains recoverable");
    assert.equal(seen.length, 2, "healthy listener sees admission and settlement");
    let late = 0;
    owner.store.subscribe(() => { late++; });
    owner.recover(); // A separate explicit local receipt observation; never a second send.
    assert.equal(late, 1);
    assert.equal(owner.calls(), 1);
    assert.equal(owner.pending(), undefined);
  }
});
