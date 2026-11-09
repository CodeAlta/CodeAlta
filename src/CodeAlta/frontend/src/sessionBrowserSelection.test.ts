import assert from "node:assert/strict";
import test from "node:test";
import type { WorkspaceSession } from "#neoastra";
import { checkedRange, sessionDeletionPlan, sessionTreeRows, treeCheckState, withChecked, withCheckedTree } from "./sessionBrowserSelection";

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

test("the sessions of a scope are a tree, sorted within each level; a search shows each match with its whole tree", () => {
  // b ─ b1 ─ b11; b ─ b2; a; c, whose parent is in another scope
  const sessions = [session("a"), session("b"), session("b1", "b"), session("b11", "b1"), session("b2", "b"), session("c", "elsewhere")];
  const byName = (x: WorkspaceSession, y: WorkspaceSession) => x.id.localeCompare(y.id);
  const shown = (matched: ReadonlySet<string> | null, compare = byName) => sessionTreeRows(sessions, matched, compare).map(row => `${"-".repeat(row.depth)}${row.session.id}${row.match ? "" : "?"}`);
  assert.deepEqual(shown(null), ["a", "b", "-b1", "--b11", "-b2", "c"]);
  // Each level in the order given: the children too.
  assert.deepEqual(shown(null, (x, y) => -byName(x, y)), ["c", "b", "-b2", "-b1", "--b11", "a"]);
  // b1 matches: its whole tree is shown, b above it, b11 below it and b2 beside it, which checking b would check too.
  assert.deepEqual(shown(new Set(["b1"])), ["b?", "-b1", "--b11?", "-b2?"]);
  assert.deepEqual(shown(new Set(["b11", "a"])), ["a", "b?", "-b1?", "--b11", "-b2?"]);
  // b matches too: shown as a match, with all that is below it.
  assert.deepEqual(shown(new Set(["b", "b11"])), ["b", "-b1?", "--b11", "-b2?"]);
  assert.deepEqual(shown(new Set()), []);
  // A cycle is shown once, at the top.
  assert.deepEqual(sessionTreeRows([session("x", "y"), session("y", "x")], null, byName).map(row => row.session.id), ["x", "y"]);
});

test("checking a session checks the sessions below it; unchecking one unchecks those below and above it", () => {
  const sessions = [session("p"), session("c1", "p"), session("g", "c1"), session("c2", "p"), session("other")];
  let checked = withCheckedTree(new Set(), sessions, ["p"], true);
  assert.deepEqual([...checked].sort(), ["c1", "c2", "g", "p"]);
  assert.equal(treeCheckState(checked, sessions, "p"), "checked");
  // Unchecking g leaves c2: p and c1 are no longer checked whole.
  checked = withCheckedTree(checked, sessions, ["g"], false);
  assert.deepEqual([...checked].sort(), ["c2"]);
  assert.equal(treeCheckState(checked, sessions, "p"), "partial");
  assert.equal(treeCheckState(checked, sessions, "c1"), "none");
  assert.equal(treeCheckState(checked, sessions, "other"), "none");
  // Checking c1 again: all that is below p is checked, so p is too.
  checked = withCheckedTree(checked, sessions, ["c1"], true);
  assert.deepEqual([...checked].sort(), ["c1", "c2", "g", "p"]);
  assert.equal(treeCheckState(checked, sessions, "p"), "checked");
  // One child after the other: the parent is checked with the last one.
  let one = withCheckedTree(new Set(), sessions, ["c2"], true);
  assert.equal(treeCheckState(one, sessions, "p"), "partial");
  one = withCheckedTree(one, sessions, ["g"], true);
  assert.deepEqual([...one].sort(), ["c1", "c2", "g", "p"], "g checks c1, then p");
  // A session that cannot be checked, one at work, is never checked, and keeps the ones above it from being checked whole.
  const atWork = withCheckedTree(new Set(), sessions, ["p"], true, id => id !== "g");
  assert.deepEqual([...atWork].sort(), ["c2"]);
  assert.equal(treeCheckState(atWork, sessions, "p"), "partial");
});
