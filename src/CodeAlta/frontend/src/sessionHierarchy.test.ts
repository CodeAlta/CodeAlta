import assert from "node:assert/strict";
import test from "node:test";
import type { WorkspaceSession } from "#neoastra";
import { sessionHierarchy } from "./sessionHierarchy";

const date = (day: number) => `2026-01-${String(day).padStart(2, "0")}T00:00:00Z`;
const row = (id: string, parentSessionId: string | null = null, day = 1, title = id): WorkspaceSession => ({
  messageCount: null, automationId: null, worktreePath: null, worktreeRoot: null, worktreeName: null, worktreeMissing: false, createdAt: null, id, title, fullTitle: title, fullTitleTruncated: false, parentSessionId, scopeKind: "project", projectId: "p1",
  workspacePath: "/p1", lineageIssue: null, providerKey: "fixture", updatedAt: date(day),
});

test("out-of-order nested rows retain all descendants and sort roots by latest descendant", () => {
  const sessions = [row("grandchild", "child", 7), row("older-root", null, 5), row("child", "parent", 2), row("parent", null, 1)];
  const rows = sessionHierarchy(sessions, sessions, "p1");
  assert.deepEqual(rows.map(item => [item.session.id, item.depth]), [
    ["parent", 0], ["child", 1], ["grandchild", 2], ["older-root", 0],
  ]);
  assert.match(rows[1].tooltip, /Child of 'parent'/);
});

test("missing, wrong project/path, unverified, self and cyclic parent remain visible as root diagnostics", () => {
  const sessions = [row("missing", "absent"), row("cross", "other"), row("unverified", "no-header"),
    row("self", "self"), row("cycle-a", "cycle-b"), row("cycle-b", "cycle-a"),
    row("wrong-path", "same-path"), row("invalid", null), row("other", null), row("no-header", null), row("same-path", null)];
  sessions[7] = { ...sessions[7], lineageIssue: "invalid_parent" };
  sessions[8] = { ...sessions[8], projectId: "p2" };
  sessions[9] = { ...sessions[9], scopeKind: null };
  sessions[10] = { ...sessions[10], workspacePath: "/other" };
  const rows = sessionHierarchy(sessions, sessions, "p1");
  assert.equal(rows.length, sessions.length);
  assert.deepEqual(new Set(rows.map(item => item.session.id)), new Set(sessions.map(item => item.id)));
  for (const id of ["missing", "cross", "unverified", "self", "cycle-a", "cycle-b", "wrong-path", "invalid"]) {
    const actual = rows.find(item => item.session.id === id)!;
    assert.equal(actual.depth, 0);
    assert.match(actual.tooltip, /root/i);
  }
  assert.match(rows.find(item => item.session.id === "cross")!.diagnostic!, /another scope/);
  assert.match(rows.find(item => item.session.id === "self")!.diagnostic!, /cycle/);
  // A header from a different project is not authority even if the on-screen rail path coincides.
  const samePathWrongRef = sessionHierarchy([row("child", "parent"), { ...row("parent"), projectId: "p2" }],
    [row("child", "parent"), { ...row("parent"), projectId: "p2" }], "p1");
  assert.equal(samePathWrongRef.find(item => item.session.id === "child")!.depth, 0);
});

test("a child says whose it is, and long titles keep bounded tooltip provenance", () => {
  const title = "Long title ".repeat(40);
  const sessions = [row("parent"), row("child", "parent", 2, title), row("unrelated")];
  sessions[1] = { ...sessions[1], title: title.slice(0, 256) };
  const rows = sessionHierarchy(sessions, sessions, "p1").filter(item => item.session.id === "child");
  assert.deepEqual(rows.map(item => [item.session.id, item.depth]), [["child", 1]]);
  assert.match(rows[0].tooltip, /Child of 'parent'/);
  assert.ok(rows[0].tooltip.includes(title));
  const truncated = sessionHierarchy([{ ...row("limit"), fullTitleTruncated: true }], [], null);
  assert.match(truncated[0].tooltip, /title truncated/);
  assert.deepEqual(sessionHierarchy(sessions, sessions.slice(0, 1), "p1").map(item => item.session.id).sort(),
    ["child", "parent", "unrelated"]);
});

test("bounded 500-row cycles and missing parents terminate without duplication", () => {
  const sessions = Array.from({ length: 500 }, (_, index) => row(`s${index}`, index === 0 ? "s499" : `s${index - 1}`));
  const rows = sessionHierarchy(sessions, sessions, "p1");
  assert.equal(rows.length, 500);
  assert.ok(rows.every(item => item.depth === 0 && item.diagnostic?.includes("cycle")));
  const incomplete = sessionHierarchy(sessions.slice(1), sessions.slice(1), "p1");
  assert.equal(incomplete.length, 499);
});

test("a row says how many sessions it started, and a session of another scope is not counted", () => {
  const sessions = [row("parent"), row("first", "parent", 2), row("second", "parent", 3), row("nested", "first", 4), row("alone")];
  const counts = Object.fromEntries(sessionHierarchy(sessions, sessions, "p1").map(item => [item.session.id, item.subAgents]));
  assert.deepEqual(counts, { parent: 2, second: 0, first: 1, nested: 0, alone: 0 });
  // A child whose parent is not shown is a root: its parent is not there to count it.
  assert.deepEqual(sessionHierarchy([sessions[1]], sessions, "p1").map(item => [item.session.id, item.depth, item.subAgents]), [["first", 0, 0]]);
});
