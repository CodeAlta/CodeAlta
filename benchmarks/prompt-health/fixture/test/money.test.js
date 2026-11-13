import { test } from "node:test";
import assert from "node:assert/strict";
import { toCents, formatCents } from "../src/money.js";

test("toCents converts typed and exported amounts", () => {
  assert.equal(toCents("12.50"), 1250);
  assert.equal(toCents(12.5), 1250);
  assert.equal(toCents("0.07"), 7);
});

test("formatCents writes two decimals", () => {
  assert.equal(formatCents(1250), "12.50");
});
