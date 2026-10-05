import test from "node:test";
import assert from "node:assert/strict";
import { clampQueueCount, createComposerQueue, queuePreview } from "./composerQueue";
import type { SessionReceiptView, SessionSendRequest, SessionSteerRequest } from "#neoastra";

const send = (key: string, text = "queued text"): SessionSendRequest => ({ expectedEpoch: "epoch", sessionId: "session", clientRequestId: key,
  text, selection: null, references: null, images: null });
const steer = (key: string, text = "steer text"): SessionSteerRequest => ({ expectedEpoch: "epoch", sessionId: "session", clientRequestId: key,
  expectedRuntimeInstanceId: "runtime", expectedAttachmentGeneration: "1", expectedRunId: "run", text });
const receipt = (key: string, kind: string, outcome: string | null = "Completed"): SessionReceiptView => ({ clientRequestId: key, sessionId: "session",
  operationId: "operation", targetOperationId: null, kind, state: outcome ? "terminal" : "pending", outcome, code: null, runId: "run", queueInsertion: null });

test("queued prompts belong to their host and session, and are editable until they leave", () => {
  const owner = createComposerQueue();
  const added = owner.add("Queue", "epoch", "session", "queued text");
  assert.ok(added);
  assert.equal(owner.list("other", "session").length, 0);
  assert.equal(owner.list("epoch", "other").length, 0);
  assert.equal(owner.add("Queue", "epoch", "session", "   "), null, "an empty prompt is not queued");
  assert.equal(owner.edit(added, "changed", 3), true);
  assert.equal(owner.remove(added), false, "a stale row cannot delete the edited prompt");
  const edited = owner.list("epoch", "session")[0];
  assert.deepEqual([edited.text, edited.count, edited.state], ["changed", 3, "waiting"]);
  const claimed = owner.claim(edited, send("key", "changed"));
  assert.equal(claimed?.state, "sending");
  assert.equal(owner.claim(edited, send("other")), null, "one attempt at a time");
  assert.equal(owner.edit(claimed!, "retargeted"), false);
  assert.equal(owner.remove(claimed!), false);
});

test("a sent prompt leaves the queue once its repeats are done", () => {
  const owner = createComposerQueue();
  const added = owner.add("Queue", "epoch", "session", "again")!;
  owner.edit(added, "again", 2);
  const first = owner.claim(owner.list("epoch", "session")[0], send("first"))!;
  owner.sent(first.id);
  const repeated = owner.list("epoch", "session")[0];
  assert.deepEqual([repeated.count, repeated.state, repeated.request], [1, "waiting", undefined]);
  owner.sent(owner.claim(repeated, send("second"))!.id);
  assert.equal(owner.list("epoch", "session").length, 0);
});

test("a busy session keeps the prompt queued, and a refusal keeps it for another try", () => {
  const owner = createComposerQueue();
  const added = owner.add("Queue", "epoch", "session", "later")!;
  const claimed = owner.claim(added, send("key"))!;
  owner.release(claimed.id);
  const waiting = owner.list("epoch", "session")[0];
  assert.deepEqual([waiting.state, waiting.request, waiting.count], ["waiting", undefined, 1]);
  owner.fail(owner.claim(waiting, send("next"))!.id, "invalid_request");
  const failed = owner.list("epoch", "session")[0];
  assert.deepEqual([failed.state, failed.code], ["failed", "invalid_request"]);
  assert.equal(owner.edit(failed, "fixed"), true);
  owner.release(failed.id);
  assert.equal(owner.list("epoch", "session")[0].state, "waiting");
});

test("an unconfirmed send keeps its request and is settled by its receipt only", () => {
  const owner = createComposerQueue();
  const claimed = owner.claim(owner.add("Queue", "epoch", "session", "unsure")!, send("key"))!;
  owner.uncertain(claimed.id, "timeout");
  const unsure = owner.list("epoch", "session")[0];
  assert.equal(unsure.state, "uncertain");
  assert.equal(unsure.request, claimed.request);
  assert.equal(owner.remove(unsure), false, "it may still have been sent");
  owner.reconcile({ status: "ok", epoch: "other", rows: [receipt("key", "Send", null)], next: null });
  owner.reconcile({ status: "ok", epoch: "epoch", rows: [receipt("else", "Send", null), receipt("key", "Steer")], next: null });
  assert.equal(owner.list("epoch", "session")[0].state, "uncertain");
  owner.reconcile({ status: "ok", epoch: "epoch", rows: [receipt("key", "Send", null)], next: null });
  assert.equal(owner.list("epoch", "session").length, 0);
});

test("steering is listed first and stays until the agent takes it up", () => {
  const owner = createComposerQueue();
  owner.add("Queue", "epoch", "session", "queued");
  const added = owner.add("Steer", "epoch", "session", "steer text")!;
  assert.deepEqual(owner.list("epoch", "session").map(item => item.kind), ["Steer", "Queue"]);
  assert.equal(owner.edit(added, "changed"), false, "steering is sent as written");
  const claimed = owner.claim(added, steer("key"))!;
  owner.sent(claimed.id);
  const delivering = owner.list("epoch", "session")[0];
  assert.equal(delivering.state, "delivering");
  owner.reconcile({ status: "ok", epoch: "epoch", rows: [receipt("key", "Steer")], next: null });
  assert.equal(owner.list("epoch", "session")[0].state, "delivering", "accepted by the host is not yet taken up by the agent");
  owner.delivered(delivering.id);
  assert.deepEqual(owner.list("epoch", "session").map(item => item.text), ["queued"]);
});

test("steering that cannot reach the turn becomes the next queued prompt", () => {
  const owner = createComposerQueue();
  owner.add("Queue", "epoch", "session", "queued");
  const claimed = owner.claim(owner.add("Steer", "epoch", "session", "steer text")!, steer("key"))!;
  owner.sent(claimed.id);
  owner.reconcile({ status: "ok", epoch: "epoch", rows: [receipt("key", "Steer", "Failed")], next: null });
  assert.deepEqual(owner.list("epoch", "session").map(item => [item.kind, item.text, item.state, item.request]),
    [["Queue", "steer text", "waiting", undefined], ["Queue", "queued", "waiting", undefined]]);
  const idle = owner.add("Steer", "epoch", "session", "nothing runs")!;
  owner.requeue(idle.id);
  assert.equal(owner.list("epoch", "session")[0].text, "nothing runs");
});

test("a sent steering prompt can be taken off the list, a prompt on its way cannot", () => {
  const owner = createComposerQueue();
  const claimed = owner.claim(owner.add("Steer", "epoch", "session", "steer text")!, steer("key"))!;
  assert.equal(owner.remove(claimed), false);
  owner.sent(claimed.id);
  assert.equal(owner.remove(owner.list("epoch", "session")[0]), true);
  assert.equal(owner.list("epoch", "session").length, 0);
});

test("steering now takes one send of a queued prompt", () => {
  const owner = createComposerQueue();
  const added = owner.add("Queue", "epoch", "session", "repeat me")!;
  owner.edit(added, "repeat me", 2);
  assert.equal(owner.steerNow(owner.list("epoch", "session")[0]), true);
  assert.deepEqual(owner.list("epoch", "session").map(item => [item.kind, item.text, item.count]), [["Steer", "repeat me", 1], ["Queue", "repeat me", 1]]);
  assert.equal(owner.steerNow(owner.list("epoch", "session")[1]), true);
  assert.deepEqual(owner.list("epoch", "session").map(item => item.kind), ["Steer", "Steer"]);
  const image = [{ title: "Image 1", mediaType: "image/png", base64: "AAAA" }];
  const withImage = owner.add("Queue", "epoch", "session", "look", image)!;
  assert.equal(owner.steerNow(withImage), false, "steering carries text only");
});

test("clearing removes the prompts that have not left, and a prompt being edited is not sent", () => {
  const owner = createComposerQueue();
  const first = owner.add("Queue", "epoch", "session", "first")!;
  const second = owner.add("Queue", "epoch", "session", "second")!;
  owner.add("Queue", "epoch", "other", "elsewhere");
  owner.add("Steer", "epoch", "session", "steer text");
  owner.hold(second, true);
  assert.equal(owner.claim(second, send("held")), null);
  owner.hold(second, false);
  owner.claim(first, send("key"));
  owner.clear("epoch", "session");
  assert.deepEqual(owner.list("epoch", "session").map(item => [item.kind, item.text]), [["Steer", "steer text"], ["Queue", "first"]]);
  assert.equal(owner.list("epoch", "other").length, 1);
});

test("an image prompt is queued with its images", () => {
  const owner = createComposerQueue();
  const image = [{ title: "Image 1", mediaType: "image/png", base64: "AAAA" }];
  const added = owner.add("Queue", "epoch", "session", "", image);
  assert.equal(added?.images, image);
  assert.equal(owner.add("Steer", "epoch", "session", "text", image)?.images, null);
});

test("the row shows one line, and the repeat count stays a whole number of at least one", () => {
  assert.equal(queuePreview("  first line\n\n  second\tline  "), "first line second line");
  assert.deepEqual([clampQueueCount(0), clampQueueCount(2.9), clampQueueCount(Number.NaN), clampQueueCount(100000)], [1, 2, 1, 999]);
});
