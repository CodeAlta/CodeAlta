import test from "node:test";
import assert from "node:assert/strict";
import { bottomScrollTop, createTimelineScrollMemory, distanceFromBottom, nearTimelineTop, olderHistoryDistance, preservePrependScrollTop, readerScrolledUp, shouldFollowTimeline } from "./timelineScroll";

test("timeline follows initial and near-bottom content but respects reading position", () => {
  assert.equal(bottomScrollTop({ scrollHeight: 1200, clientHeight: 400 }), 800);
  assert.equal(distanceFromBottom({ scrollTop: 750, scrollHeight: 1200, clientHeight: 400 }), 50);
  assert.equal(shouldFollowTimeline({ scrollTop: 750, scrollHeight: 1200, clientHeight: 400 }), true);
  assert.equal(shouldFollowTimeline({ scrollTop: 300, scrollHeight: 1200, clientHeight: 400 }), false);
});

test("prepending older content preserves the visible anchor", () => {
  assert.equal(preservePrependScrollTop({ scrollTop: 240, scrollHeight: 1000, clientHeight: 500 }, 1350), 590);
});

test("the top of what is loaded is near within a screen of it, and within a fixed distance in a short view", () => {
  // A screen above the view: the rows before are read before the reader reaches the first loaded one.
  assert.equal(nearTimelineTop({ scrollTop: 0, clientHeight: 900 }), true);
  assert.equal(nearTimelineTop({ scrollTop: 900, clientHeight: 900 }), true);
  assert.equal(nearTimelineTop({ scrollTop: 901, clientHeight: 900 }), false);
  // A short view keeps the same margin as a view of that height.
  assert.equal(nearTimelineTop({ scrollTop: olderHistoryDistance, clientHeight: 200 }), true);
  assert.equal(nearTimelineTop({ scrollTop: olderHistoryDistance + 1, clientHeight: 200 }), false);
  // A latest turn shorter than the view has nothing to scroll: it is at its top.
  assert.equal(nearTimelineTop({ scrollTop: 0, clientHeight: 0 }), true);
});

test("a scroll up is the reader's unless the layout moved the view", () => {
  const before = { scrollTop: 500, scrollHeight: 2000, clientHeight: 400 };
  assert.equal(readerScrolledUp(before, { ...before, scrollTop: 380 }, false), true);
  assert.equal(readerScrolledUp(before, { ...before, scrollTop: 620 }, false), false);
  assert.equal(readerScrolledUp(before, before, false), false);
  // Rows above the view took another size, and the browser kept the reader on the same rows.
  assert.equal(readerScrolledUp(before, { ...before, scrollTop: 460, scrollHeight: 1960 }, false), false);
  // The view grew at the end of the timeline (the prompt got shorter): the content came down with it.
  assert.equal(readerScrolledUp({ scrollTop: 1600, scrollHeight: 2000, clientHeight: 400 }, { scrollTop: 1500, scrollHeight: 2000, clientHeight: 500 }, false), false);
  // The reader is going up: the scrolls of that move count while rows come into the layout.
  assert.equal(readerScrolledUp(before, { ...before, scrollTop: 380, scrollHeight: 2100 }, true), true);
  assert.equal(readerScrolledUp(before, { ...before, scrollTop: 620, scrollHeight: 2100 }, true), false);
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

test("keyboard-positioned bottom stays unfollowed through repeated native scrolls until the user moves", () => {
  const selection = createTimelineScrollMemory().open("reader");
  selection.settle({ scrollTop: 0, scrollHeight: 1000, clientHeight: 200 });
  selection.finishRestore();
  selection.pauseAt(800);
  for (let index = 0; index < 3; index++)
    assert.equal(selection.scroll({ scrollTop: 800, scrollHeight: 1000, clientHeight: 200 }), false);
  assert.equal(selection.scroll({ scrollTop: 650, scrollHeight: 1000, clientHeight: 200 }), false);
  assert.equal(selection.scroll({ scrollTop: 800, scrollHeight: 1000, clientHeight: 200 }), true);
  selection.pauseAt(800);
  selection.jump({ scrollTop: 800, scrollHeight: 1000, clientHeight: 200 });
  assert.equal(selection.following(), true);
});

test("a session that was rewound starts at its end again: the position kept before is one of another conversation", () => {
  const memory = createTimelineScrollMemory();
  const before = memory.open("a", 0);
  before.settle({ scrollTop: 0, scrollHeight: 1000, clientHeight: 300 });
  before.finishRestore();
  assert.equal(before.scroll({ scrollTop: 240, scrollHeight: 1000, clientHeight: 300 }), false);
  // The same session, not rewound, is found where it was left.
  assert.equal(memory.open("a", 0).following(), false);
  const after = memory.open("a", 1);
  assert.equal(after.following(), true);
  assert.equal(after.settle({ scrollTop: 0, scrollHeight: 600, clientHeight: 300 }), 300);
  after.finishRestore();
  assert.equal(after.scroll({ scrollTop: 100, scrollHeight: 600, clientHeight: 300 }), false);
  // What is kept from now on is the position of the rewound session.
  const again = memory.open("a", 1);
  assert.equal(again.following(), false);
  assert.equal(again.settle({ scrollTop: 0, scrollHeight: 600, clientHeight: 300 }), 100);
  assert.equal(memory.open("a", 2).following(), true);
});
