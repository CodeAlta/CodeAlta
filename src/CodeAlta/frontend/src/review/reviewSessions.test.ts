import assert from "node:assert/strict";
import test from "node:test";
import { reviewSessions, sameCheckout, sessionCheckout } from "./reviewSessions";

const row = (id: string, more: Partial<Parameters<typeof reviewSessions>[0][number]> = {}) => ({ id, title: id, projectId: "p", archived: false, updatedAt: "2026-10-01",
  parentSessionId: null, worktreePath: null, worktreeRoot: null, worktreeName: null, worktreeMissing: false, ...more });

test("a review goes to the sessions of its checkout, the current one first, then the most recent; not to sub-sessions or archived ones", () => {
  const sessions = [row("old", { updatedAt: "2026-09-01" }), row("recent", { updatedAt: "2026-10-19" }), row("worktree", { worktreePath: "/w/a" }),
    row("child", { parentSessionId: "recent" }), row("archived", { archived: true }), row("elsewhere", { projectId: "q" }), row("here", { updatedAt: "2026-08-01" })];
  assert.deepEqual(reviewSessions(sessions, "p", null).map(session => session.id), ["recent", "old", "here"]);
  assert.deepEqual(reviewSessions(sessions, "p", null, "here").map(session => session.id), ["here", "recent", "old"]);
  assert.deepEqual(reviewSessions(sessions, "p", "/w/a/").map(session => session.id), ["worktree"], "a folder with a trailing separator is the same");
  assert.equal(sessionCheckout(row("x", { worktreePath: "/w/b" })), "/w/b");
  assert.equal(sessionCheckout(row("x")), null);
  assert.equal(sameCheckout(null, null), true);
  assert.equal(sameCheckout(null, "/w"), false);
  assert.equal(sameCheckout("C:\\W\\A", "c:/w/a"), true);
});
