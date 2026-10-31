import { test } from "node:test";
import assert from "node:assert/strict";
import { available, reserve, release } from "../src/inventory.js";

test("reserving takes from the stock", () => {
  assert.equal(available(reserve({ pen: 5 }, "pen", 2), "pen"), 3);
});

test("reserving more than the stock throws", () => {
  assert.throws(() => reserve({ pen: 1 }, "pen", 2));
});

test("releasing gives back", () => {
  assert.equal(available(release({}, "pen", 2), "pen"), 2);
});
