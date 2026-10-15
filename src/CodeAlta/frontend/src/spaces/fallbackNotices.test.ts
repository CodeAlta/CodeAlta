import assert from "node:assert/strict";
import test from "node:test";
import type { SpaceSessionActivity } from "#neoastra";
import { newFallbackNotices } from "./fallbackNotices";
import { readSessionActivity, sameSessions } from "./spaces";

const doing = (sessionId: string, fallback: SpaceSessionActivity["fallback"] = null): SpaceSessionActivity =>
  ({ sessionId, projectId: "p", title: `Title of ${sessionId}`, running: true, backgroundTasks: 0, failed: false, waiting: false, ancestors: [], spaceId: null, fallback, restoredForRunAt: null });

test("a fallback is said once, and none that happened before the window opened", () => {
  const first = { at: "2026-10-14T18:00:00.0000000+00:00", message: "Claude Code quota reached until 18:00, continuing on Codex GPT-6.1 high." };
  const opened = newFallbackNotices(null, [doing("a", first)]);
  assert.equal(opened.notices.length, 0);

  const again = newFallbackNotices(opened.said, [doing("a", first), doing("b")]);
  assert.equal(again.notices.length, 0);

  const later = { at: "2026-10-14T18:30:00.0000000+00:00", message: "Codex unavailable, continuing on Copilot Claude Opus 4.6 high." };
  const next = newFallbackNotices(again.said, [doing("a", later), doing("b", first)]);
  assert.deepEqual(next.notices.map(notice => [notice.sessionId, notice.message]), [["a", later.message], ["b", first.message]]);
  assert.equal(newFallbackNotices(next.said, [doing("a", later), doing("b", first)]).notices.length, 0);
});

test("the fallback of a session is read and compared with its activity", () => {
  const read = readSessionActivity(doing("a", { at: "2026-10-14T18:00:00Z", message: "x".repeat(900) }))!;
  assert.equal(read.fallback?.message.length, 512);
  assert.equal(readSessionActivity({ ...doing("a"), fallback: { at: "", message: "m" } })?.fallback, null);
  assert.equal(sameSessions([doing("a")], [doing("a", { at: "2026-10-14T18:00:00Z", message: "m" })]), false);
});
