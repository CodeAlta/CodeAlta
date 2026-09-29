import test from "node:test";
import assert from "node:assert/strict";
import { createComposerQueue } from "./composerQueue";
import type { SessionReceiptView } from "#neoastra";

const request = { expectedEpoch: "epoch", sessionId: "session", clientRequestId: "key", text: "queued text", expectedRuntimeInstanceId: "runtime", expectedAttachmentGeneration: "1" };
test("queued drafts are editable only before dispatch and scoped to their host/session", () => {
  const owner = createComposerQueue();
  assert.equal(owner.add("Queue", request), true);
  assert.equal(owner.list("other", "session").length, 0);
  const original = owner.list("epoch", "session")[0];
  assert.equal(owner.edit(original, "changed", 3), true);
  assert.equal(owner.remove(original), false, "stale DOM cannot delete an edited draft");
  const edited = owner.list("epoch", "session")[0];
  assert.equal(owner.claim(edited), true);
  assert.equal(owner.claim(edited), false);
  const pending = owner.list("epoch", "session")[0];
  assert.equal(owner.remove(pending), false);
  assert.equal(owner.edit(pending, "retargeted"), false);
  owner.outcome(pending.id, { status: "uncertain", epoch: "epoch", receipt: null });
  assert.equal(owner.list("epoch", "session")[0].state, "uncertain");
  assert.equal(owner.list("epoch", "session")[0].request, pending.request);
});

test("receipt settlement repeats once with a fresh key and ignores old receipts", () => {
  const owner = createComposerQueue();
  owner.add("Queue", request);
  owner.edit(owner.list("epoch", "session")[0], request.text, 2);
  owner.claim(owner.list("epoch", "session")[0]);
  const row: SessionReceiptView = { clientRequestId: "key", sessionId: "session", operationId: "operation",
    targetOperationId: null, kind: "Queue", state: "terminal", outcome: "Completed", code: null, runId: "run", queueInsertion: null };
  owner.reconcile({ status: "ok", epoch: "other", rows: [row], next: null });
  assert.equal(owner.list("epoch", "session")[0].state, "sending");
  owner.reconcile({ status: "ok", epoch: "epoch", rows: [row], next: null });
  const repeated = owner.list("epoch", "session")[0];
  assert.equal(repeated.count, 1);
  assert.notEqual(repeated.request.clientRequestId, "key");
  owner.claim(repeated);
  owner.reconcile({ status: "ok", epoch: "epoch", rows: [row], next: null });
  assert.equal(owner.list("epoch", "session")[0].state, "sending");
  owner.reconcile({ status: "ok", epoch: "epoch", rows: [{ ...row, clientRequestId: repeated.request.clientRequestId }], next: null });
  assert.equal(owner.list("epoch", "session").length, 0);
});
