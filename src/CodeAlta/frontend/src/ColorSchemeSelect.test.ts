import assert from "node:assert/strict";
import test from "node:test";
import { optionAfterKey } from "./ColorSchemeSelect";

test("the arrow keys move through the list of schemes and stop at its ends", () => {
  assert.equal(optionAfterKey("ArrowDown", 0, 13), 1);
  assert.equal(optionAfterKey("ArrowDown", 12, 13), 12);
  assert.equal(optionAfterKey("ArrowUp", 5, 13), 4);
  assert.equal(optionAfterKey("ArrowUp", 0, 13), 0);
  assert.equal(optionAfterKey("Home", 7, 13), 0);
  assert.equal(optionAfterKey("End", 7, 13), 12);
  // No option has the focus yet: Down takes the first one.
  assert.equal(optionAfterKey("ArrowDown", -1, 13), 0);
  assert.equal(optionAfterKey("Enter", 3, 13), null);
  assert.equal(optionAfterKey("ArrowDown", -1, 0), null);
});
