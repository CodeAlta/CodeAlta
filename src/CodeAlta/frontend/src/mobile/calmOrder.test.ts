import assert from "node:assert/strict";
import test from "node:test";
import { createCalmOrder, type CalmClock } from "./calmOrder";

// A clock a test moves by hand: the timers fire when their time comes.
function fakeClock() {
  let now = 0, seq = 0;
  const timers = new Map<number, { at: number; callback: () => void }>();
  const clock: CalmClock = {
    now: () => now,
    setTimer: (callback, ms) => { timers.set(++seq, { at: now + ms, callback }); return seq; },
    clearTimer: timer => { timers.delete(timer as number); },
  };
  return {
    clock,
    get pending() { return timers.size; },
    advance(ms: number) {
      const end = now + ms;
      for (;;) {
        const next = [...timers.entries()].filter(([, t]) => t.at <= end).sort((a, b) => a[1].at - b[1].at)[0];
        if (!next) break;
        timers.delete(next[0]);
        now = next[1].at;
        next[1].callback();
      }
      now = end;
    },
  };
}

function setUp() {
  const time = fakeClock();
  let settled = 0;
  const calm = createCalmOrder({ clock: time.clock, timing: { quietMs: 5000, maxWaitMs: 15_000 }, onSettle: () => settled++ });
  return { time, calm, settled: () => settled };
}

test("an order that changed waits until the list has been quiet", () => {
  const { time, calm, settled } = setUp();
  calm.shown(["a", "b"], ["a", "b"]);
  assert.equal(calm.waiting, false);
  assert.deepEqual([...calm.held!], [["a", 0], ["b", 1]], "the order shown is held");
  calm.shown(["a", "b"], ["b", "a"]);
  assert.equal(calm.waiting, true);
  time.advance(4999);
  assert.equal(settled(), 0);
  time.advance(1);
  assert.equal(settled(), 1, "5 s after the change");
  assert.equal(calm.held, null, "the next drawing takes the latest order");
});

test("a change of the order pushes the reorder back, a drawing of the same order does not, and it never waits more than the longest wait", () => {
  const { time, calm, settled } = setUp();
  calm.shown(["a", "b", "c"], ["b", "a", "c"]);
  time.advance(3000);
  calm.shown(["a", "b", "c"], ["b", "a", "c"]);
  time.advance(2000);
  assert.equal(settled(), 1, "a live update of the same order does not push it back");

  calm.shown(["b", "a", "c"], ["c", "b", "a"]);
  for (let step = 0; step < 3; step++) {
    time.advance(4000);
    calm.shown(["b", "a", "c"], step % 2 ? ["c", "b", "a"] : ["a", "c", "b"]);
  }
  assert.equal(settled(), 1, "sessions taking turns push it back");
  time.advance(3000);
  assert.equal(settled(), 2, "but 15 s at most after the first change");
});

test("nothing moves under a finger or while the list scrolls; once quiet, the order is taken", () => {
  const { time, calm, settled } = setUp();
  calm.shown(["a", "b"], ["b", "a"]);
  calm.down();
  time.advance(60_000);
  assert.equal(settled(), 0, "never while the finger is down");
  calm.up();
  time.advance(4000);
  calm.scrolled();
  time.advance(4999);
  assert.equal(settled(), 0, "the list scrolled: it waits again");
  time.advance(1);
  assert.equal(settled(), 1, "5 s after the last scroll");
});

test("the latest order is taken at once when the user comes back to the list or refreshes it", () => {
  const { time, calm, settled } = setUp();
  calm.shown(["a", "b"], ["b", "a"]);
  calm.down();
  calm.settle();
  assert.equal(calm.held, null);
  assert.equal(calm.waiting, false);
  assert.equal(time.pending, 0, "no timer left");
  time.advance(60_000);
  assert.equal(settled(), 0, "the caller draws it");
  calm.shown(["b", "a"], ["b", "a"]);
  assert.equal(calm.waiting, false, "the latest order shown: nothing waits");
});
