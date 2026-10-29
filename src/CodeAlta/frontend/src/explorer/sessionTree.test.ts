import assert from "node:assert/strict";
import test from "node:test";
import type { WorkspaceSession } from "#neoastra";
import { sessionHierarchy, type SessionHierarchyRow } from "../sessionHierarchy";
import { listedSessions, sessionEntries, sessionList, withClaudeCodeSessions, type SessionListLimits } from "./sessionTree";

const row = (id: string, parentSessionId: string | null = null, day = 1): WorkspaceSession => ({
  messageCount: null, automationId: null, worktreePath: null, worktreeRoot: null, worktreeName: null, worktreeMissing: false, forkedFromSessionId: null, forkedFromTitle: null, forkKind: null, forkTurnCount: 0, rewindCount: 0, archived: false, archivedAt: null, spaceId: null, createdAt: null, id, title: id, fullTitle: id, fullTitleTruncated: false, parentSessionId, scopeKind: "project", projectId: "p1",
  workspacePath: "/p1", lineageIssue: null, providerKey: "fixture", updatedAt: `2026-01-${String(day).padStart(2, "0")}T00:00:00Z`,
});
const rowsOf = (sessions: WorkspaceSession[]) => sessionHierarchy(sessions, sessions, "p1");
const limits = (value: Partial<SessionListLimits> & { collapsedIds?: readonly string[]; extras?: Record<string, number> } = {}): SessionListLimits => ({
  count: 50, subCount: 4, active: null, extra: id => value.extras?.[id] ?? 0, collapsed: id => !!value.collapsedIds?.includes(id), ...value,
});
// A line for each entry: the session with its depth, "-" before a collapsed one, or what its parent does not list.
const lines = (rows: readonly SessionHierarchyRow[], value: SessionListLimits) => sessionList(rows, value).entries.map(entry =>
  entry.kind === "session" ? `${entry.collapsed ? "-" : ""}${entry.row.session.id}@${entry.row.depth}`
    : entry.kind === "link" ? `link ${entry.session.id}@${entry.depth}<${entry.parentId}` : `more ${entry.parentId}@${entry.depth} ${entry.hidden}${entry.extended ? " extended" : ""}`);

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

test("the Claude Code sessions started elsewhere close the list of a project, when a Claude Code provider is configured", () => {
  const entries = sessionList(rows, limits({ count: 1, subCount: 0 })).entries;
  const listed = withClaudeCodeSessions(entries, "p1", true);
  assert.deepEqual(listed.at(-1), { kind: "claude-code", projectId: "p1" });
  assert.equal(listed.length, entries.length + 1);
  // It is not a session: the list counts and lists only its sessions.
  assert.deepEqual(sessionEntries(listed), entries);
  assert.deepEqual(listedSessions(listed), listedSessions(entries));
  // The global sessions have no folder of a project, and without Claude Code nothing started it elsewhere.
  assert.deepEqual(withClaudeCodeSessions(entries, null, true), entries);
  assert.deepEqual(withClaudeCodeSessions(entries, "p1", false), entries);
  // An empty project has the group all the same: its sessions may all have been started elsewhere.
  assert.deepEqual(withClaudeCodeSessions([], "p1", true), [{ kind: "claude-code", projectId: "p1" }]);
});

test("the sub-agents of a session listed in other projects follow its own as links, and count with them", () => {
  const away = (id: string, parent: string, day: number): WorkspaceSession => ({ ...row(id, parent, day), projectId: "p2", workspacePath: "/p2" });
  const all = [row("a", null, 20), row("a1", "a", 19), row("b", null, 10), away("x1", "a", 5), away("x2", "a", 8), away("y", "b", 3), away("z", "a1", 2)];
  const listed = rowsOf(all.filter(value => value.projectId === "p1"));
  // The hierarchy is given every session of the snapshot: those of p2 are links, newest first.
  const linked = sessionHierarchy(all.filter(value => value.projectId === "p1"), all, "p1");
  assert.deepEqual(linked.map(item => [item.session.id, item.subAgents, item.childrenElsewhere?.map(value => value.id) ?? []]),
    [["a", 1, ["x2", "x1"]], ["a1", 0, ["z"]], ["b", 0, ["y"]]]);
  assert.deepEqual(listed.map(item => item.childrenElsewhere), [undefined, undefined, undefined]);
  assert.deepEqual(lines(linked, limits()), ["a@0", "a1@1", "link z@2<a1", "link x2@1<a", "link x1@1<a", "b@0", "link y@1<b"]);
  // A session whose only sub-agents are elsewhere has a twist all the same, and collapsing it hides them.
  const b = sessionList(linked, limits()).entries.find(entry => entry.kind === "session" && entry.row.session.id === "b");
  assert.equal(b?.kind === "session" && b.parent, true);
  assert.deepEqual(lines(linked, limits({ collapsedIds: ["b", "a1"] })), ["a@0", "-a1@1", "link x2@1<a", "link x1@1<a", "-b@0"]);
  // The links count with the sub-agents of their parent.
  assert.deepEqual(lines(linked, limits({ subCount: 2 })), ["a@0", "a1@1", "link z@2<a1", "link x2@1<a", "more a@1 1", "b@0", "link y@1<b"]);
  // Filtered, a session links to the children found only, and has no twist for those it does not link.
  const found = linked.map(item => item.session.id === "a" ? { ...item, linked: [all[4]] } : item.session.id === "b" ? { ...item, linked: [] } : item);
  assert.deepEqual(lines(found, limits()), ["a@0", "a1@1", "link z@2<a1", "link x2@1<a", "b@0"]);
  assert.equal(sessionList(found, limits()).entries.some(entry => entry.kind === "session" && entry.row.session.id === "b" && entry.parent), false);
  // An archived child is not linked, nor is one whose parent is not listed here.
  const archived = [...all.slice(0, 3), { ...away("gone", "a", 9), archived: true }, away("orphan", "missing", 9)];
  assert.deepEqual(sessionHierarchy(archived.filter(value => value.projectId === "p1"), archived, "p1").map(item => item.childrenElsewhere), [undefined, undefined, undefined]);
});
