import { test } from "node:test";
import assert from "node:assert/strict";
import { slugify } from "../src/text.js";

test("slugify makes the slug of a product name", () => {
  assert.equal(slugify("Crème brûlée 250g"), "creme-brulee-250g");
  assert.equal(slugify("  Pain & beurre  "), "pain-beurre");
});
