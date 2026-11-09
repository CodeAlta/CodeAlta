import assert from "node:assert/strict";
import test from "node:test";
import type { WorkspaceSession } from "#neoastra";
import { checkedRange, sessionDeletionPlan, withChecked } from "./sessionBrowserSelection";

const session = (id: string, parentSessionId: string | null = null, changes: Partial<WorkspaceSession> = {}): WorkspaceSession => ({
  id, title: id, fullTitle: id, fullTitleTruncated: false, workspacePath: "/p1", providerKey: "codex", updatedAt: "2026-11-09T10:00:00Z",
  createdAt: "2026-11-09T09:00:00Z", parentSessionId, scopeKind: "project", projectId: "p1", lineageIssue: null, messageCount: 2,
  automationId: null, worktreePath: null, worktreeRoot: null, worktreeName: null, worktreeMissing: false, forkedFromSessionId: null,
  forkedFromTitle: null, forkKind: null, forkTurnCount: 0, rewindCount: 0, archived: false, archivedAt: null, spaceId: null, ...changes,
});

test("a click checks one session, a Shift-click the rows from the one clicked before, in the order shown", () => {
  const shown = ["a", "b", "c", "d", "e"];
  assert.deepEqual(checkedRange(shown, null, "c"), ["c"]);
  assert.deepEqual(checkedRange(shown, "b", "d"), ["b", "c", "d"]);
  assert.deepEqual(checkedRange(shown, "d", "b"), ["b", "c", "d"], "upwards too");
  assert.deepEqual(checkedRange(shown, "gone", "c"), ["c"], "a row no longer shown starts nothing");
  assert.deepEqual(checkedRange(shown, "a", "gone"), []);

  const checked = withChecked(new Set(["a"]), ["b", "c"], true);
  assert.deepEqual([...checked].sort(), ["a", "b", "c"]);
  assert.deepEqual([...withChecked(checked, ["a", "c", "x"], false)], ["b"]);
});

test("checked sessions are deleted the deepest first, and one with a session below it that is not checked is kept", () => {
  // parent ─ child ─ grandchild; parent ─ other (in another project, archived); alone; lone ─ kid
  const catalog = [session("parent"), session("child", "parent"), session("grandchild", "child"),
    session("other", "parent", { projectId: "p2", archived: true }), session("alone"), session("lone"), session("kid", "lone")];
  const pick = (...ids: string[]) => catalog.filter(row => ids.includes(row.id));

  // The whole tree of lone: kid goes first.
  assert.deepEqual(sessionDeletionPlan(pick("lone", "kid", "alone"), catalog).order.map(row => row.id), ["kid", "alone", "lone"]);

  // parent has other below it, not checked: parent stays, its checked descendants go, deepest first.
  const plan = sessionDeletionPlan(pick("parent", "child", "grandchild"), catalog);
  assert.deepEqual(plan.order.map(row => row.id), ["grandchild", "child"]);
  assert.deepEqual(plan.kept.map(row => row.id), ["parent"]);
  assert.deepEqual(plan.missing.map(row => row.id), ["other"], "archived and in another project, it still counts");

  // child kept because grandchild is not checked, and parent with it.
  const partial = sessionDeletionPlan(pick("parent", "child", "other"), catalog);
  assert.deepEqual(partial.order.map(row => row.id), ["other"]);
  assert.deepEqual(partial.kept.map(row => row.id), ["parent", "child"]);
  assert.deepEqual(partial.missing.map(row => row.id), ["grandchild"]);

  assert.deepEqual(sessionDeletionPlan([], catalog), { order: [], kept: [], missing: [] });
});
