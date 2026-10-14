import assert from "node:assert/strict";
import test from "node:test";
import { showsTimelineCard } from "./ClaudeCliFirstUse";

test("the error of a provider shows the card unless the composer of its session already shows it", () => {
  assert.equal(showsTimelineCard({ composerProvider: "Claude-Missing" }, "claude-missing"), false);
  assert.equal(showsTimelineCard({ composerProvider: "claude-code" }, "claude-missing"), true);
  assert.equal(showsTimelineCard({ composerProvider: null }, "claude-missing"), true);
});
