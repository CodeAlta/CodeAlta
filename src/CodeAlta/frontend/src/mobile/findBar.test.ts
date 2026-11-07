import assert from "node:assert/strict";
import test from "node:test";
import { findCount } from "./findBar";

test("the count of a search says which place is shown of how many, with a + while the page holds only a part of what can be searched", () => {
  assert.equal(findCount(2, 12, false, true), "3 of 12");
  assert.equal(findCount(2, 12, true, true), "3 of 12+");
  assert.equal(findCount(-1, 0, false, true), "0 of 0");
  assert.equal(findCount(-1, 0, true, true), "0 of 0+");
  assert.equal(findCount(-1, 0, true, false), "", "Nothing before a text is typed.");
});
