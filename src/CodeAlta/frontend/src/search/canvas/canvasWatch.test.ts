import assert from "node:assert/strict";
import test from "node:test";
import { reconnectMilliseconds, watchIndex } from "./canvasWatch";

/** Timers the test runs by hand. */
function manualTimers() {
  const pending: { run: () => void; milliseconds: number }[] = [];
  return {
    pending,
    timers: { set: (run: () => void, milliseconds: number) => { const entry = { run, milliseconds }; pending.push(entry); return entry; },
      clear: (timer: unknown) => { const at = pending.indexOf(timer as never); if (at >= 0) pending.splice(at, 1); } },
  };
}

/** A watch whose events the test sends, and which it can end. */
function channel() {
  let push: ((value: IteratorResult<unknown>) => void) | null = null;
  const queue: IteratorResult<unknown>[] = [];
  const send = (value: IteratorResult<unknown>) => { if (push) { const resolve = push; push = null; resolve(value); } else queue.push(value); };
  const iterable: AsyncIterable<unknown> = { [Symbol.asyncIterator]: () => ({ next: () => queue.length ? Promise.resolve(queue.shift()!) : new Promise(resolve => { push = resolve; }) }) };
  return { iterable, event: (revision: number) => send({ value: { revision }, done: false }), end: () => send({ value: undefined, done: true }) };
}

const settle = () => new Promise(resolve => setTimeout(resolve, 0));

test("each event of the host is a change; a watch that ends is opened again after a moment, and stopping ends it all", async () => {
  const { pending, timers } = manualTimers();
  const opened: AbortSignal[] = [];
  let current = channel();
  let changes = 0;
  const stop = watchIndex(async signal => { opened.push(signal); current = channel(); return current.iterable; }, () => { changes++; }, timers);
  await settle();
  assert.equal(opened.length, 1);
  current.event(0);
  current.event(1);
  await settle();
  assert.equal(changes, 2);

  // The host restarted: the watch ends, and is opened again once the delay is over.
  current.end();
  await settle();
  assert.equal(pending.length, 1);
  assert.equal(pending[0].milliseconds, reconnectMilliseconds);
  pending.shift()!.run();
  await settle();
  assert.equal(opened.length, 2);
  current.event(0);
  await settle();
  assert.equal(changes, 3, "a new connection reads again");

  stop();
  assert.equal(opened[1].aborted, true);
  current.event(1);
  await settle();
  assert.equal(changes, 3, "nothing is heard once stopped");
});

test("a watch that cannot be opened is tried again, until it is stopped", async () => {
  const { pending, timers } = manualTimers();
  let calls = 0;
  const stop = watchIndex(async () => { calls++; throw new Error("host gone"); }, () => assert.fail("no change"), timers);
  await settle();
  assert.equal(calls, 1);
  assert.equal(pending.length, 1);
  stop();
  assert.equal(pending.length, 0, "the retry is cancelled");
});
