import { test } from "node:test";
import assert from "node:assert/strict";
import { applyDiscount, taxFor, withTax } from "../src/pricing.js";

test("a discount rounds half up to the cent", () => {
  assert.equal(applyDiscount(999, 15), 849);
});

test("a discount out of range throws", () => {
  assert.throws(() => applyDiscount(100, 120), RangeError);
});

test("the tax of a region", () => {
  assert.equal(taxFor("FR"), 20);
  assert.equal(withTax(1000, "DE"), 1190);
});
