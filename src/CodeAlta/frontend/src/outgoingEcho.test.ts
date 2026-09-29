import test from "node:test";
import assert from "node:assert/strict";
import { matchesOutgoingText } from "./outgoingEcho";

test("image echoes reconcile only within the submitted run", () => {
  const text = "Describe this\n\nLocal image: image.png";
  assert.equal(matchesOutgoingText(text, "Describe this", 1, "run", "run"), true);
  assert.equal(matchesOutgoingText(text, "Describe this", 1, "run", "other"), false);
  assert.equal(matchesOutgoingText(text, "Describe this", 1, null, "run"), false);
  assert.equal(matchesOutgoingText(text, "Describe this", 0, "run", "run"), false);
  assert.equal(matchesOutgoingText("attachment", "", 1, "run", "run"), true);
  assert.equal(matchesOutgoingText("Different prompt", "Describe this", 1, "run", "run"), false);
});
