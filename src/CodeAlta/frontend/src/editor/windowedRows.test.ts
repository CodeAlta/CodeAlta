import assert from "node:assert/strict";
import test from "node:test";
import { fitImageZoom, nextImageZoom } from "./ImageView";
import { rowScrollTop, rowWindow } from "./windowedRows";

test("only the rows in view, and a few around them, are drawn; spacers stand for the rest", () => {
  // 1000 rows of 24 pixels in a view of 240: ten rows, twelve more on each side.
  assert.deepEqual(rowWindow(1000, 24, 0, 240), { first: 0, last: 22, before: 0, after: 978 * 24 });
  assert.deepEqual(rowWindow(1000, 24, 2400, 240), { first: 88, last: 122, before: 88 * 24, after: 878 * 24 });
  assert.deepEqual(rowWindow(1000, 24, 1000 * 24 - 240, 240), { first: 978, last: 1000, before: 978 * 24, after: 0 });
  // A short list is drawn whole; a view not measured yet still draws its first rows.
  assert.deepEqual(rowWindow(5, 24, 0, 240), { first: 0, last: 5, before: 0, after: 0 });
  assert.deepEqual(rowWindow(1000, 24, 0, 0), { first: 0, last: 13, before: 0, after: 987 * 24 });
  assert.deepEqual(rowWindow(1000, 24, 480, 240, 0), { first: 20, last: 30, before: 480, after: 970 * 24 });
  // A scroll position left over from a longer list draws the end of the shorter one.
  assert.deepEqual(rowWindow(10, 24, 9000, 240), { first: 0, last: 10, before: 0, after: 0 });
  assert.deepEqual([rowWindow(0, 24, 0, 240), rowWindow(10, 0, 0, 240)], [{ first: 0, last: 0, before: 0, after: 0 }, { first: 0, last: 0, before: 0, after: 0 }]);
});

test("a row is scrolled to only when it is not in view, and no further than needed", () => {
  assert.equal(rowScrollTop(5, 24, 0, 240), null);
  assert.equal(rowScrollTop(9, 24, 0, 240), null);
  assert.equal(rowScrollTop(10, 24, 0, 240), 24);
  assert.equal(rowScrollTop(50, 24, 0, 240), 50 * 24 + 24 - 240);
  assert.equal(rowScrollTop(3, 24, 480, 240), 72);
  assert.equal(rowScrollTop(-1, 24, 480, 240), null);
});

test("a picture is fitted to its pane without enlarging its pixels; a drawing fills it", () => {
  assert.equal(fitImageZoom(2000, 1000, 500, 500), 0.25);
  assert.equal(fitImageZoom(100, 400, 500, 200), 0.5);
  assert.equal(fitImageZoom(50, 50, 500, 500), 1);
  assert.equal(fitImageZoom(50, 50, 500, 400, true), 8);
  assert.deepEqual([fitImageZoom(0, 50, 500, 500), fitImageZoom(50, 50, 0, 500), fitImageZoom(50, 50, 500, -1, true)], [1, 1, 1]);
});

test("zooming goes through fixed steps and stops at the first and the last", () => {
  assert.deepEqual([nextImageZoom(1, 1), nextImageZoom(1, -1), nextImageZoom(0.3, 1), nextImageZoom(0.3, -1)], [1.5, 0.75, 0.5, 0.25]);
  assert.deepEqual([nextImageZoom(16, 1), nextImageZoom(0.1, -1), nextImageZoom(0.01, -1), nextImageZoom(99, 1)], [16, 0.1, 0.1, 16]);
  assert.deepEqual([nextImageZoom(0.25, 1), nextImageZoom(0.25, -1)], [0.5, 0.1]);
});
