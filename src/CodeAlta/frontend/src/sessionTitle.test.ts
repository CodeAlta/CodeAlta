import assert from "node:assert/strict";
import test from "node:test";
import { plainTitle } from "./sessionTitle";

test("titles lose their Markdown markers but keep their words", () => {
  assert.equal(plainTitle("Committed all fixes in **`4176c487` — `Fix WebApp composer`**."), "Committed all fixes in 4176c487 — Fix WebApp composer.");
  assert.equal(plainTitle("**I identified the exact cause**"), "I identified the exact cause");
  assert.equal(plainTitle("## Plan\n for the  week"), "Plan for the week");
  assert.equal(plainTitle("- item one"), "item one");
  assert.equal(plainTitle("a * b and __init__"), "a * b and __init__");
  assert.equal(plainTitle("**"), "**", "a title made of markers only is left alone");
});
