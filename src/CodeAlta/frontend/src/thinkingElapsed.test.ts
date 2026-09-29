import assert from "node:assert/strict";
import test from "node:test";
import { formatThinkingElapsed } from "./thinkingElapsed";

test("thinking duration uses whole elapsed seconds and minutes", () => {
  assert.equal(formatThinkingElapsed(0), "0s");
  assert.equal(formatThinkingElapsed(59.9), "59s");
  assert.equal(formatThinkingElapsed(61), "1m 1s");
});
