import assert from "node:assert/strict";
import test from "node:test";
import { sliceRows } from "./rowWindow";

test("the rows drawn are those that touch the part in view and its margin, with the space of the others", () => {
  const heights = Array.from({ length: 100 }, () => 50); // 5,000 pixels
  // In view: 1,000 to 1,400, with a margin of 200: rows 16 to 31.
  assert.deepEqual(sliceRows(heights, 1000, 1400, 200), { start: 16, end: 31, before: 800, after: 5000 - 1600 });
  // At the top and at the bottom, the slice stops at the list.
  assert.deepEqual(sliceRows(heights, 0, 300, 100), { start: 0, end: 7, before: 0, after: 5000 - 400 });
  assert.deepEqual(sliceRows(heights, 4800, 5200, 100), { start: 94, end: 99, before: 4700, after: 0 });
});

test("rows of different heights are placed where they are, and a view past the end still draws the last row", () => {
  const heights = [40, 80, 40, 80, 40, 80];
  const slice = sliceRows(heights, 130, 170, 0);
  assert.deepEqual(slice, { start: 2, end: 3, before: 120, after: 120 });
  const past = sliceRows(heights, 900, 1000, 0);
  assert.equal(past.start, 5);
  assert.equal(past.end, 5);
  assert.equal(past.before + heights[5] + past.after, 360);
  assert.deepEqual(sliceRows([], 0, 100), { start: 0, end: -1, before: 0, after: 0 });
});
