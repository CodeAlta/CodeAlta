import assert from "node:assert/strict";
import test from "node:test";
import { centeredWindowGeometry, clampWindowGeometry, parseWindowGeometry } from "./windowGeometry";

const minimum = { width: 480, height: 320 };

test("a window opens centered at its preferred size and never exceeds its bounds", () => {
  assert.deepEqual(centeredWindowGeometry({ width: 2000, height: 1000 }, { width: 1600, height: 800 }, minimum),
    { x: 200, y: 100, width: 1600, height: 800 });
  assert.deepEqual(centeredWindowGeometry({ width: 900, height: 600 }, { width: 1600, height: 800 }, minimum),
    { x: 0, y: 0, width: 900, height: 600 });
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
