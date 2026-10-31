import { test } from "node:test";
import assert from "node:assert/strict";

const root = process.env.EVAL_PROJECT_URL;
const { allocate } = await import(new URL("src/allocate.js", root));

test("the parts add up to the amount, by the largest remainder", () => {
  assert.deepEqual(allocate(100, [1, 1, 1]), [34, 33, 33]);
  assert.deepEqual(allocate(1000, [3, 7]), [300, 700]);
  assert.deepEqual(allocate(5, [1, 2]), [2, 3]);
  assert.deepEqual(allocate(7, [1, 1, 1, 1]), [2, 2, 2, 1]);
});

test("a tie of remainders goes to the earliest part", () => {
  assert.deepEqual(allocate(1, [1, 1]), [1, 0]);
  assert.deepEqual(allocate(3, [1, 1, 2, 2]), [1, 0, 1, 1]);
});

test("a negative amount is allocated as the opposite of its absolute value", () => {
  assert.deepEqual(allocate(-100, [1, 1, 1]), [-34, -33, -33]);
});

test("a zero ratio gets nothing", () => {
  assert.deepEqual(allocate(10, [0, 1, 0, 1]), [0, 5, 0, 5]);
  assert.deepEqual(allocate(0, [1, 2]), [0, 0]);
});

test("fractional ratios are exact", () => {
  assert.deepEqual(allocate(100, [0.1, 0.2, 0.7]), [10, 20, 70]);
  assert.deepEqual(allocate(10, [0.3, 0.3, 0.4]), [3, 3, 4]);
});

test("invalid input throws a RangeError", () => {
  for (const [amount, ratios] of [[10, []], [10, [0, 0]], [10, [1, -1]], [10.5, [1]], [10, [1, Number.NaN]], [Number.MAX_SAFE_INTEGER + 2, [1]]]) {
    assert.throws(() => allocate(amount, ratios), RangeError, JSON.stringify([amount, ratios]));
  }
});

test("large amounts stay exact", () => {
  const parts = allocate(Number.MAX_SAFE_INTEGER, [1, 1, 1]);
  assert.equal(parts.reduce((a, b) => a + b, 0), Number.MAX_SAFE_INTEGER);
  assert.deepEqual(parts, [3002399751580331, 3002399751580330, 3002399751580330]);
});
