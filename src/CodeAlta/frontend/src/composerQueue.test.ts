import test from "node:test";
import assert from "node:assert/strict";
import { clampQueueCount, createComposerQueue, queuePreview } from "./composerQueue";
import type { SessionReceiptView, SessionSendRequest, SessionSteerRequest } from "#neoastra";

const send = (key: string, text = "queued text"): SessionSendRequest => ({ expectedEpoch: "epoch", sessionId: "session", clientRequestId: key,
  text, selection: null, references: null, images: null });
const steer = (key: string, text = "steer text"): SessionSteerRequest => ({ expectedEpoch: "epoch", sessionId: "session", clientRequestId: key,
  expectedRuntimeInstanceId: "runtime", expectedAttachmentGeneration: "1", expectedRunId: "run", text });
const receipt = (key: string, kind: string, outcome: string | null = "Completed"): SessionReceiptView => ({ clientRequestId: key, sessionId: "session",
  operationId: "operation", targetOperationId: null, kind, state: outcome ? "terminal" : "pending", outcome, code: null, runId: "run" });

test("queued context is a deep immutable capture, including an explicit Default", () => {
  const queue = createComposerQueue();
  const selection = { providerKey: "key", modelId: "model", agentPromptId: "default", reasoningEffort: null, context: { tokens: 1050000 as number | null } };
  const first = queue.add("Queue", "epoch", "session", "first", null, selection)!;
  selection.context.tokens = null;
  const second = queue.add("Queue", "epoch", "session", "second", null, selection)!;
  selection.context.tokens = 272000;
  assert.equal(first.selection?.context?.tokens, 1050000);
  assert.deepEqual(second.selection?.context, { tokens: null });
  assert.equal(Object.isFrozen(first.selection?.context), true);
});

test("programmatic enqueue preserves its selection and awaits downstream ownership, not local insertion", async () => {
  const owner = createComposerQueue();
  const selection = { providerId: "provider", providerKey: "key", modelId: "model", agentPromptId: "default", reasoningEffort: null, permissionMode: null };
  const outcome = owner.admit("epoch", "session", "Do the plan", new AbortController().signal, selection);
  let settled = false; void outcome.then(() => { settled = true; });
  await Promise.resolve(); assert.equal(settled, false);
  const item = owner.list("epoch", "session")[0];
  assert.equal(item.selection?.agentPromptId, "default");
  assert.notEqual(item.selection, selection);
  owner.claim(item, { ...send("original"), selection: item.selection ?? null });
  await Promise.resolve(); assert.equal(settled, false);
  owner.sent(item.id);
  assert.equal(await outcome, true);
});

test("canceling queued work before dispatch removes it, but cancellation cannot release an original in flight", async () => {
  const owner = createComposerQueue(), waiting = new AbortController();
  const canceled = owner.admit("epoch", "session", "Not started", waiting.signal);
  waiting.abort(); assert.equal(await canceled, false);
  assert.deepEqual(owner.list("epoch", "session"), []);
  for (const accepted of [true, false]) {
    const lifetime = new AbortController();
    const pending = owner.admit("epoch", "session", "Captured", lifetime.signal);
    const item = owner.list("epoch", "session")[0];
    owner.claim(item, send("original"));
    let settled = false; void pending.then(() => { settled = true; });
    lifetime.abort(); await Promise.resolve(); assert.equal(settled, false);
    if (accepted) owner.sent(item.id); else owner.release(item.id); // Busy refusal cannot requeue after Close.
    assert.equal(await pending, accepted);
    assert.deepEqual(owner.list("epoch", "session"), []);
  }
});

test("uncertain queued admission stays protected after close until its original receipt is known", async () => {
  const owner = createComposerQueue(), lifetime = new AbortController();
  const pending = owner.admit("epoch", "session", "Original", lifetime.signal);
  const item = owner.list("epoch", "session")[0];
  owner.claim(item, send("original")); owner.uncertain(item.id, "transport");
  let settled = false; void pending.then(() => { settled = true; });
  lifetime.abort(); await Promise.resolve(); assert.equal(settled, false);
  assert.equal(owner.remove(owner.list("epoch", "session")[0]), false);
  owner.sent(item.id); assert.equal(await pending, true);
});

test("close during an admitted repeated Send never schedules a fresh repeat", async () => {
  const owner = createComposerQueue(), lifetime = new AbortController();
  const pending = owner.admit("epoch", "session", "Original", lifetime.signal);
  owner.edit(owner.list("epoch", "session")[0], "Original", 2);
  const item = owner.list("epoch", "session")[0];
  owner.claim(item, send("original")); lifetime.abort(); owner.sent(item.id);
  assert.equal(await pending, true, "the original still has a conclusive owner receipt");
  assert.deepEqual(owner.list("epoch", "session"), [], "Close cancels any not-yet-started repeats");
});

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

test("the sessions that have rows are named once, until their last row leaves", () => {
  const owner = createComposerQueue();
  assert.deepEqual(owner.sessions("epoch"), []);
  const first = owner.add("Queue", "epoch", "one", "first")!;
  owner.add("Steer", "epoch", "one", "steer");
  const other = owner.add("Queue", "epoch", "two", "other")!;
  owner.add("Queue", "older", "three", "of another host");
  assert.deepEqual(owner.sessions("epoch"), ["one", "two"]);
  assert.deepEqual(owner.sessions("older"), ["three"]);
  // A row that is being sent still needs its session; a session with no row left is not named.
  owner.claim(other, send("key"));
  assert.deepEqual(owner.sessions("epoch"), ["one", "two"]);
  owner.sent(other.id);
  owner.remove(first);
  assert.deepEqual(owner.sessions("epoch"), ["one"]);
});

test("the row shows one line, and the repeat count stays a whole number of at least one", () => {
  assert.equal(queuePreview("  first line\n\n  second\tline  "), "first line second line");
  assert.deepEqual([clampQueueCount(0), clampQueueCount(2.9), clampQueueCount(Number.NaN), clampQueueCount(100000)], [1, 2, 1, 999]);
});
