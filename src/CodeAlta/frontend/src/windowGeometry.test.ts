import assert from "node:assert/strict";
import test from "node:test";
import { canStepWindow, centeredWindowGeometry, clampWindowGeometry, parseWindowGeometry, steppedWindowGeometry } from "./windowGeometry";

const minimum = { width: 480, height: 320 };

test("a window opens centered at its preferred size and never exceeds its bounds", () => {
  assert.deepEqual(centeredWindowGeometry({ width: 2000, height: 1000 }, { width: 1600, height: 800 }, minimum),
    { x: 200, y: 100, width: 1600, height: 800 });
  // A small screen: the window opens at a share of it, not at its whole size, and the window behind stays in sight.
  assert.deepEqual(centeredWindowGeometry({ width: 900, height: 600 }, { width: 1600, height: 800 }, minimum),
    { x: 45, y: 30, width: 810, height: 540 });
  assert.deepEqual(centeredWindowGeometry({ width: 500, height: 300 }, { width: 1600, height: 800 }, minimum),
    { x: 20, y: 0, width: 480, height: 300 }, "the minimum size wins over the share where it fits");
});

test("a window keeps out of the native title bar, whose controls would cover its own (#310)", () => {
  // 1920×1080 at 150%: a window that asks for nearly all the room opens below the title bar of 38 px.
  const opened = centeredWindowGeometry({ width: 1280, height: 720 }, { width: 1240, height: 642 }, minimum, 38);
  assert.deepEqual(opened, { x: 64, y: 72, width: 1152, height: 614 });
  // A stored window, from a larger screen or moved up, is brought below the title bar and fits the room there.
  const bounds = { width: 1280, height: 720 };
  assert.deepEqual(clampWindowGeometry({ x: 0, y: 0, width: 1600, height: 1000 }, bounds, minimum, 38), { x: 0, y: 38, width: 1280, height: 682 });
  assert.deepEqual(clampWindowGeometry({ x: 100, y: 10, width: 600, height: 400 }, bounds, minimum, 38), { x: 100, y: 38, width: 600, height: 400 });
  assert.deepEqual(clampWindowGeometry({ x: 100, y: 500, width: 600, height: 400 }, bounds, minimum, 38), { x: 100, y: 320, width: 600, height: 400 });
  // Too short a window for its minimum height below the title bar: the window goes over it, to stay whole.
  assert.deepEqual(clampWindowGeometry({ x: 0, y: 0, width: 600, height: 400 }, { width: 1280, height: 340 }, minimum, 38), { x: 0, y: 20, width: 600, height: 320 });
});

test("a stored geometry is pulled back inside smaller bounds and keeps the minimum size where it fits", () => {
  const bounds = { width: 1200, height: 800 };
  assert.deepEqual(clampWindowGeometry({ x: 900, y: 700, width: 600, height: 400 }, bounds, minimum), { x: 600, y: 400, width: 600, height: 400 });
  assert.deepEqual(clampWindowGeometry({ x: -50, y: -20, width: 100, height: 100 }, bounds, minimum), { x: 0, y: 0, width: 480, height: 320 });
  assert.deepEqual(clampWindowGeometry({ x: 10, y: 10, width: 5000, height: 5000 }, bounds, minimum), { x: 0, y: 0, width: 1200, height: 800 });
  // Bounds smaller than the minimum win: the window must stay reachable.
  assert.deepEqual(clampWindowGeometry({ x: 40, y: 40, width: 600, height: 400 }, { width: 300, height: 200 }, minimum), { x: 0, y: 0, width: 300, height: 200 });
  assert.deepEqual(clampWindowGeometry({ x: 10.4, y: 10.6, width: 600.5, height: 400.2 }, bounds, minimum), { x: 10, y: 11, width: 601, height: 400 });
});

test("malformed stored geometry is refused", () => {
  assert.deepEqual(parseWindowGeometry('{"x":1,"y":2,"width":640,"height":480}'), { x: 1, y: 2, width: 640, height: 480 });
  for (const text of [null, "", "{bad", "null", "[]", '{"x":1,"y":2,"width":640}', '{"x":"1","y":2,"width":640,"height":480}',
    '{"x":1,"y":2,"width":0,"height":480}', '{"x":1,"y":2,"width":640,"height":-1}', '{"x":1e999,"y":2,"width":640,"height":480}'])
    assert.equal(parseWindowGeometry(text), null, String(text));
});

test("Larger and Smaller change a window a step at a time around its center, within its bounds and its minimum", () => {
  const bounds = { width: 2000, height: 1000 };
  const window = { x: 600, y: 300, width: 800, height: 400 };
  // A step is a tenth of the bounds each way: the center stays where it was.
  assert.deepEqual(steppedWindowGeometry(window, bounds, minimum, 0, 1), { x: 500, y: 250, width: 1000, height: 500 });
  assert.deepEqual(steppedWindowGeometry(window, bounds, minimum, 0, -1), { x: 700, y: 350, width: 600, height: 300 + 20 }, "never under its minimum height");
  // Near an edge, the larger window takes the room there is, inside the bounds.
  assert.deepEqual(steppedWindowGeometry({ x: 0, y: 0, width: 1900, height: 950 }, bounds, minimum, 0, 1), { x: 0, y: 0, width: 2000, height: 1000 });
  // Below a native title bar of 40 px, the step is a tenth of the room under it.
  assert.deepEqual(steppedWindowGeometry({ x: 600, y: 340, width: 800, height: 400 }, bounds, minimum, 40, 1), { x: 500, y: 292, width: 1000, height: 496 });
  // At the size of the bounds there is nothing larger; at the minimum, nothing smaller.
  assert.equal(canStepWindow({ x: 0, y: 0, width: 2000, height: 1000 }, bounds, minimum, 0, 1), false);
  assert.equal(canStepWindow({ x: 0, y: 0, width: 2000, height: 1000 }, bounds, minimum, 0, -1), true);
  assert.equal(canStepWindow({ x: 10, y: 10, width: 480, height: 320 }, bounds, minimum, 0, -1), false);
});
