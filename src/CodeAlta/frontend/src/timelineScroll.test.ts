import test from "node:test";
import assert from "node:assert/strict";
import { bottomScrollTop, createTimelineScrollMemory, distanceFromBottom, preservePrependScrollTop, shouldFollowTimeline } from "./timelineScroll";

test("timeline follows initial and near-bottom content but respects reading position", () => {
  assert.equal(bottomScrollTop({ scrollHeight: 1200, clientHeight: 400 }), 800);
  assert.equal(distanceFromBottom({ scrollTop: 750, scrollHeight: 1200, clientHeight: 400 }), 50);
  assert.equal(shouldFollowTimeline({ scrollTop: 750, scrollHeight: 1200, clientHeight: 400 }), true);
  assert.equal(shouldFollowTimeline({ scrollTop: 300, scrollHeight: 1200, clientHeight: 400 }), false);
});

test("prepending older content preserves the visible anchor", () => {
  assert.equal(preservePrependScrollTop({ scrollTop: 240, scrollHeight: 1000, clientHeight: 500 }, 1350), 590);
});

test("switching sessions restores an unfollowed position only after history settles; other sessions follow independently", () => {
  const memory = createTimelineScrollMemory();
  const a = memory.open("a");
  assert.equal(a.following(), true);
  assert.equal(a.scroll({ scrollTop: 0, scrollHeight: 1000, clientHeight: 300 }), true);
  assert.equal(a.settle({ scrollTop: 0, scrollHeight: 1000, clientHeight: 300 }), 700);
  a.finishRestore();
  assert.equal(a.scroll({ scrollTop: 240, scrollHeight: 1000, clientHeight: 300 }), false);
  const b = memory.open("b");
  assert.equal(b.following(), true);
  b.settle({ scrollTop: 0, scrollHeight: 1200, clientHeight: 300 });
  b.finishRestore();
  b.scroll({ scrollTop: 900, scrollHeight: 1200, clientHeight: 300 });
  const again = memory.open("a");
  assert.equal(again.following(), false);
  assert.equal(again.scroll({ scrollTop: 0, scrollHeight: 300, clientHeight: 300 }), false); // Loading scroll cannot erase the position.
  assert.equal(again.settle({ scrollTop: 0, scrollHeight: 1000, clientHeight: 300 }), 240);
  assert.equal(again.scroll({ scrollTop: 240, scrollHeight: 1000, clientHeight: 300 }), false); // Programmatic restore.
  again.finishRestore();
  assert.equal(again.scroll({ scrollTop: 240, scrollHeight: 500, clientHeight: 300 }), false); // Delayed native scroll event.
  assert.equal(again.scroll({ scrollTop: 695, scrollHeight: 1000, clientHeight: 300 }), true);
  assert.equal(memory.open("b").following(), true);
  assert.equal(memory.open("a").following(), true);
});

test("history error or short content does not overwrite the saved reading position; jump explicitly follows", () => {
  const memory = createTimelineScrollMemory();
  const initial = memory.open("a");
  initial.settle({ scrollTop: 0, scrollHeight: 1000, clientHeight: 200 });
  initial.finishRestore();
  initial.scroll({ scrollTop: 300, scrollHeight: 1000, clientHeight: 200 });
  const selected = memory.open("a");
  assert.equal(selected.settle({ scrollTop: 0, scrollHeight: 100, clientHeight: 200 }), 0);
  selected.finishRestore();
  assert.equal(selected.scroll({ scrollTop: 0, scrollHeight: 100, clientHeight: 200 }), false);
  assert.equal(memory.open("a").settle({ scrollTop: 0, scrollHeight: 1000, clientHeight: 200 }), 300);
  selected.jump({ scrollTop: 0, scrollHeight: 100, clientHeight: 200 });
  assert.equal(memory.open("a").following(), true);
});

test("scroll memory has a bounded per-session window", () => {
  const memory = createTimelineScrollMemory();
  const first = memory.open("first");
  first.settle({ scrollTop: 0, scrollHeight: 1000, clientHeight: 200 });
  first.finishRestore();
  first.scroll({ scrollTop: 200, scrollHeight: 1000, clientHeight: 200 });
  for (let index = 0; index < 64; index++) memory.open(`other-${index}`).jump({ scrollTop: 0, scrollHeight: 100, clientHeight: 100 });
  assert.equal(memory.open("first").following(), true);
});
