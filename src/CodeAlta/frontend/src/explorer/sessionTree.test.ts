import assert from "node:assert/strict";
import test from "node:test";
import type { WorkspaceSession } from "#neoastra";
import { sessionHierarchy, type SessionHierarchyRow } from "../sessionHierarchy";
import { listedSessions, sessionList, type SessionListLimits } from "./sessionTree";

const row = (id: string, parentSessionId: string | null = null, day = 1): WorkspaceSession => ({
  messageCount: null, automationId: null, worktreePath: null, worktreeRoot: null, worktreeName: null, worktreeMissing: false, createdAt: null, id, title: id, fullTitle: id, fullTitleTruncated: false, parentSessionId, scopeKind: "project", projectId: "p1",
  workspacePath: "/p1", lineageIssue: null, providerKey: "fixture", updatedAt: `2026-01-${String(day).padStart(2, "0")}T00:00:00Z`,
});
const rowsOf = (sessions: WorkspaceSession[]) => sessionHierarchy(sessions, sessions, "p1");
const limits = (value: Partial<SessionListLimits> & { collapsedIds?: readonly string[]; extras?: Record<string, number> } = {}): SessionListLimits => ({
  count: 50, subCount: 4, active: null, extra: id => value.extras?.[id] ?? 0, collapsed: id => !!value.collapsedIds?.includes(id), ...value,
});
// A line for each entry: the session with its depth, "-" before a collapsed one, or what its parent does not list.
const lines = (rows: readonly SessionHierarchyRow[], value: SessionListLimits) => sessionList(rows, value).entries.map(entry =>
  entry.kind === "session" ? `${entry.collapsed ? "-" : ""}${entry.row.session.id}@${entry.row.depth}` : `more ${entry.parentId}@${entry.depth} ${entry.hidden}${entry.extended ? " extended" : ""}`);

// Two sessions: "a" has six sub-agents, the second of which has three of its own; "b" has none. Newest first.
const sessions = [row("a", null, 20), row("b", null, 10),
  ...[1, 2, 3, 4, 5, 6].map(index => row(`a${index}`, "a", 20 - index)),
  ...[1, 2, 3].map(index => row(`a2-${index}`, "a2", 10 - index))];
const rows = rowsOf(sessions);

test("each level has its own count: the sessions of the scope, and the sub-agents of each session", () => {
  assert.deepEqual(lines(rows, limits()), ["a@0", "a1@1", "a2@1", "a2-1@2", "a2-2@2", "a2-3@2", "a3@1", "a4@1", "more a@1 2", "b@0"]);
  assert.equal(sessionList(rows, limits()).hidden, 0);
  // The count of sub-agents is the one of every level under a session.
  assert.deepEqual(lines(rows, limits({ subCount: 2 })), ["a@0", "a1@1", "a2@1", "a2-1@2", "a2-2@2", "more a2@2 1", "more a@1 4", "b@0"]);
  // The count of sessions is of the sessions of the scope alone: their sub-agents are not counted in it.
  const one = sessionList(rows, limits({ count: 1 }));
  assert.deepEqual([listedSessions(one.entries).map(item => item.session.id).join(" "), one.hidden], ["a a1 a2 a2-1 a2-2 a2-3 a3 a4", 1]);
  assert.equal(sessionList([], limits()).entries.length, 0);
});

test("a session lists more of its sub-agents on demand, and says so", () => {
  assert.deepEqual(lines(rows, limits({ subCount: 2, extras: { a: 2 } })).filter(line => line.startsWith("more")), ["more a2@2 1", "more a@1 2 extended"]);
  // All of them listed: what remains is the way back.
  assert.deepEqual(lines(rows, limits({ extras: { a: 4 } })).at(-2), "more a@1 0 extended");
  assert.deepEqual(lines(rows, limits({ subCount: 6 })).filter(line => line.startsWith("more")), []);
});

test("a collapsed session lists none of its sub-agents, at any level", () => {
  assert.deepEqual(lines(rows, limits({ collapsedIds: ["a"] })), ["-a@0", "b@0"]);
  assert.deepEqual(lines(rows, limits({ collapsedIds: ["a2"] })), ["a@0", "a1@1", "-a2@1", "a3@1", "a4@1", "more a@1 2", "b@0"]);
  // A session that has no sub-agent is never collapsed, and neither says it has some.
  const list = sessionList(rows, limits({ collapsedIds: ["b", "a1"] })).entries;
  assert.deepEqual(list.flatMap(entry => entry.kind === "session" && (entry.parent || entry.collapsed) ? [entry.row.session.id] : []), ["a", "a2"]);
});

test("the selected session stays listed beyond the counts, with the sessions it is under", () => {
  assert.deepEqual(lines(rows, limits({ count: 1, active: "b" })), ["a@0", "a1@1", "a2@1", "a2-1@2", "a2-2@2", "a2-3@2", "a3@1", "a4@1", "more a@1 2", "b@0"]);
  assert.equal(sessionList(rows, limits({ count: 1, active: "b" })).hidden, 0);
  assert.deepEqual(lines(rows, limits({ subCount: 1, active: "a6" })), ["a@0", "a1@1", "a6@1", "more a@1 4", "b@0"]);
  assert.deepEqual(lines(rows, limits({ subCount: 1, active: "a2-3" })), ["a@0", "a1@1", "a2@1", "a2-1@2", "a2-3@2", "more a2@2 1", "more a@1 4", "b@0"]);
  // Collapsing is the user's own doing: it hides the selected session too.
  assert.deepEqual(lines(rows, limits({ collapsedIds: ["a"], active: "a6" })), ["-a@0", "b@0"]);
  assert.deepEqual(lines(rows, limits({ active: "absent" })), lines(rows, limits()));
});

test("a long list stays bounded, and a long chain of sub-agents is walked without recursion", () => {
  const many = rowsOf(Array.from({ length: 300 }, (_, index) => row(`s${index}`, null, 1)));
  const first = sessionList(many, limits({ count: 20 }));
  assert.deepEqual([first.entries.length, first.hidden], [20, 280]);
  const chain = rowsOf(Array.from({ length: 400 }, (_, index) => row(`c${index}`, index === 0 ? null : `c${index - 1}`)));
  assert.equal(chain.at(-1)!.depth, 399);
  assert.equal(sessionList(chain, limits({ subCount: 1 })).entries.length, 400);
  assert.deepEqual(lines(chain, limits({ collapsedIds: ["c1"] })), ["c0@0", "-c1@1"]);
  // Rows whose depth does not follow the one before are listed all the same.
  const odd = [{ depth: 2, id: "x" }, { depth: 0, id: "y" }, { depth: 3, id: "z" }].map(item => ({ session: row(item.id), depth: item.depth, diagnostic: null, tooltip: "" }));
  assert.equal(listedSessions(sessionList(odd, limits()).entries).length, 3);
});
