import assert from "node:assert/strict";
import test from "node:test";
import { createToolOutputStore } from "./toolOutput";

type Item = { status: string; text: string; start: string; total: string; isReset: boolean; isComplete: boolean };
type Request = { expectedEpoch: string; sessionId: string; activityId: string };

// A channel the test feeds: `push` delivers an item, `end` closes it; `returned` says the page gave it back.
function channel() {
  const waiting: ((result: IteratorResult<Item>) => void)[] = [];
  const queued: IteratorResult<Item>[] = [];
  const state = { returned: false, signal: undefined as AbortSignal | undefined };
  const deliver = (result: IteratorResult<Item>) => { const next = waiting.shift(); if (next) next(result); else queued.push(result); };
  const stream: AsyncIterable<Item> = { [Symbol.asyncIterator]: () => ({
    next: () => new Promise<IteratorResult<Item>>(resolve => { const ready = queued.shift(); if (ready) resolve(ready); else waiting.push(resolve); }),
    return: async () => { state.returned = true; deliver({ done: true, value: undefined }); return { done: true, value: undefined }; },
  }) };
  return { stream, state, push: (item: Partial<Item>) => deliver({ done: false, value: { status: "ok", text: "", start: "0", total: "0", isReset: false, isComplete: false, ...item } }),
    end: () => deliver({ done: true, value: undefined }) };
}
const settle = () => new Promise(resolve => setTimeout(resolve, 5));

test("the output of a running call is followed while something shows it, and added to as it comes", async () => {
  const opened: Request[] = [];
  const source = channel();
  const store = createToolOutputStore(async (request, options) => { opened.push(request); source.state.signal = options.signal; return source.stream; });
  const outputs = store.session("epoch", "session");
  assert.equal(outputs.get("call"), undefined, "A call nothing shows is not followed.");
  let changes = 0;
  const leave = outputs.subscribe("call", () => { changes++; });
  const second = outputs.subscribe("call", () => { });
  assert.deepEqual(opened, [{ expectedEpoch: "epoch", sessionId: "session", activityId: "call" }], "Two subscribers share one channel.");
  assert.deepEqual(outputs.get("call"), { text: "", offset: 0, total: 0, lines: 0, last: "", ended: false });
  source.push({ text: "step 1\nstep 2\n", start: "0", total: "14", isReset: true });
  await settle();
  assert.deepEqual(outputs.get("call"), { text: "step 1\nstep 2\n", offset: 0, total: 14, lines: 2, last: "step 2", ended: false });
  source.push({ text: "step 3\n   \n", start: "14", total: "26" });
  await settle();
  const state = outputs.get("call")!;
  assert.deepEqual([state.text, state.lines, state.last, state.total], ["step 1\nstep 2\nstep 3\n   \n", 4, "step 3", 26]);
  assert.equal(changes, 2);
  // Text in between that the host did not keep, or a reset, replaces what is held.
  source.push({ text: "later\n", start: "500", total: "506" });
  await settle();
  assert.deepEqual([outputs.get("call")!.text, outputs.get("call")!.offset, outputs.get("call")!.lines], ["later\n", 500, 1]);
  source.push({ text: "", start: "506", total: "506", isComplete: true });
  await settle();
  assert.equal(outputs.get("call")!.ended, true);
  assert.equal(source.state.returned, true, "The channel of an ended call is given back.");
  // The state stays while something shows it, then leaves with the last subscriber.
  leave();
  await settle();
  assert.equal(outputs.get("call")!.text, "later\n");
  second();
  await settle();
  assert.equal(outputs.get("call"), undefined);
  assert.equal(store.count(), 0);
});

test("a channel is closed with its last subscriber, unless one comes back at once", async () => {
  const sources: ReturnType<typeof channel>[] = [];
  const store = createToolOutputStore(async (_request, options) => { const source = channel(); source.state.signal = options.signal; sources.push(source); return source.stream; });
  const outputs = store.session("e", "s");
  // A component that mounts twice (StrictMode) subscribes, leaves and subscribes again: one channel serves it.
  outputs.subscribe("call", () => { })();
  const leave = outputs.subscribe("call", () => { });
  await settle();
  assert.equal(sources.length, 1);
  assert.equal(sources[0].state.signal!.aborted, false);
  leave();
  await settle();
  assert.equal(sources[0].state.signal!.aborted, true, "Nothing shows the call any more: its channel is closed.");
  assert.equal(store.count(), 0);
  // Another session and another call are other channels.
  const first = outputs.subscribe("call", () => { }), other = store.session("e", "other").subscribe("call", () => { });
  assert.equal(sources.length, 3);
  first(); other();
});

test("only the newest text is kept, and a refusal or a broken channel ends the live output", async () => {
  const source = channel();
  const store = createToolOutputStore(async () => source.stream, { maximumCharacters: 10 });
  const outputs = store.session("e", "s");
  const leave = outputs.subscribe("call", () => { });
  source.push({ text: "ab\ncd\nef\n", start: "0", total: "9", isReset: true });
  source.push({ text: "gh\nij\n", start: "9", total: "15" });
  await settle();
  assert.deepEqual(outputs.get("call"), { text: "\ncd\nef\ngh\nij\n".slice(-10), offset: 5, total: 15, lines: 4, last: "ij", ended: false });
  source.push({ status: "capacity", isComplete: true });
  await settle();
  assert.equal(outputs.get("call")!.ended, true);
  assert.equal(outputs.get("call")!.text.length, 10, "What was shown stays.");
  leave();
  const failing = createToolOutputStore(async () => { throw new Error("no channel"); }).session("e", "s");
  const stop = failing.subscribe("call", () => { });
  await settle();
  assert.deepEqual(failing.get("call"), { text: "", offset: 0, total: 0, lines: 0, last: "", ended: true });
  stop();
  const closing = channel();
  const closed = createToolOutputStore(async () => closing.stream).session("e", "s");
  const end = closed.subscribe("call", () => { });
  closing.end();
  await settle();
  assert.equal(closed.get("call")!.ended, true, "A channel that closes without saying so ends the output too.");
  end();
});
