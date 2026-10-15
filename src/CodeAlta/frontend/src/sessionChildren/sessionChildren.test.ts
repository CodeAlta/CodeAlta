import assert from "node:assert/strict";
import test from "node:test";
import type { WorkspaceDeleteSessionTreeRequest, WorkspaceDeleteSessionTreeResponse, WorkspaceSession, WorkspaceSessionChildrenResponse } from "#neoastra";
import { translate } from "../localization";
import { deleteSessionTree, readSessionChildren, sessionsAtWork, sessionTreeMessage, treeDeletable, type SessionChildren } from "./sessionChildren";

const epoch = "12345678-1234-1234-1234-123456789abc";
const fixtureSession = (id: string, changes: Partial<WorkspaceSession> = {}): WorkspaceSession => ({
  id, title: id, fullTitle: id, fullTitleTruncated: false, workspacePath: "/chats", providerKey: "codex", updatedAt: "2026-10-14T10:00:00Z",
  createdAt: "2026-10-14T09:00:00Z", parentSessionId: null, scopeKind: "global", projectId: null, lineageIssue: null, messageCount: 2,
  automationId: null, worktreePath: null, worktreeRoot: null, worktreeName: null, worktreeMissing: false, forkedFromSessionId: null,
  forkedFromTitle: null, forkKind: null, forkTurnCount: 0, rewindCount: 0, archived: false, archivedAt: null, spaceId: null, ...changes,
});

// A chat with a session in a project, which has a chat of its own, and a second chat.
const reply = (changes: Partial<WorkspaceSessionChildrenResponse> = {}): WorkspaceSessionChildrenResponse => ({
  status: "ok", hostEpoch: epoch, sessionId: "parent", root: fixtureSession("parent", { spaceId: "work" }), rootAtWork: false,
  children: [
    { session: fixtureSession("child", { scopeKind: "project", projectId: "p1", workspacePath: "/p1" }), parentSessionId: "parent", depth: 1, atWork: false },
    { session: fixtureSession("grandchild"), parentSessionId: "child", depth: 2, atWork: false },
    { session: fixtureSession("second"), parentSessionId: "parent", depth: 1, atWork: false },
  ],
  descendantIds: ["child", "grandchild", "second"], directCount: 2, totalCount: 3, truncated: false, ...changes,
});

test("the child sessions are read from the host, which answers about the session asked", async () => {
  let answer = reply();
  const requests: unknown[] = [];
  const invoke = async (request: unknown) => { requests.push(request); return answer; };
  const read = await readSessionChildren(invoke, epoch, "parent");
  assert.equal(read.kind, "read");
  if (read.kind !== "read") return;
  assert.deepEqual([read.value.direct, read.value.total, read.value.children.map(child => `${child.session.id}@${child.depth}<${child.parentSessionId}`)],
    [2, 3, ["child@1<parent", "grandchild@2<child", "second@1<parent"]]);
  assert.deepEqual(requests, [{ expectedHostEpoch: epoch, sessionId: "parent" }]);

  answer = reply({ sessionId: "other" });
  assert.deepEqual(await readSessionChildren(invoke, epoch, "parent"), { kind: "error", code: "read_failed" });
  answer = reply({ status: "session_missing", root: null });
  assert.deepEqual(await readSessionChildren(invoke, epoch, "parent"), { kind: "error", code: "session_missing" });
  assert.deepEqual(await readSessionChildren(async () => { throw new Error("gone"); }, epoch, "parent"), { kind: "error", code: "read_failed" });
  assert.deepEqual(await readSessionChildren(invoke, null, "parent"), { kind: "error", code: "unconfigured" });
});

test("a tree is deleted in one go only when the host listed it all and nothing in it is at work", async () => {
  const read = await readSessionChildren(async () => reply(), epoch, "parent");
  assert.equal(read.kind, "read");
  if (read.kind !== "read") return;
  const tree: SessionChildren = read.value;
  assert.equal(treeDeletable(tree), true);
  const working: SessionChildren = { ...tree, children: tree.children.map(child => child.session.id === "grandchild" ? { ...child, atWork: true } : child) };
  assert.deepEqual(sessionsAtWork(working).map(session => session.id), ["grandchild"]);
  assert.equal(treeDeletable(working), false);
  assert.deepEqual(sessionsAtWork({ ...tree, rootAtWork: true }).map(session => session.id), ["parent"]);
  assert.equal(treeDeletable({ ...tree, rootAtWork: true }), false);
  // More descendants than the host gives the identities of: never a partial confirmation.
  assert.equal(treeDeletable({ ...tree, descendantIds: [] }), false);
  assert.equal(treeDeletable({ ...tree, children: [], descendantIds: [], direct: 0, total: 0 }), false);
});

test("the deletion confirms the title of the session and the descendants the user saw", async () => {
  const read = await readSessionChildren(async () => reply(), epoch, "parent");
  if (read.kind !== "read") throw new Error("unread");
  const requests: WorkspaceDeleteSessionTreeRequest[] = [];
  let answer: WorkspaceDeleteSessionTreeResponse = { status: "ok", hostEpoch: epoch, sessionId: "parent", deleted: ["grandchild", "child", "second", "parent"], blockingSessionId: null };
  const invoke = async (request: WorkspaceDeleteSessionTreeRequest) => { requests.push(request); return answer; };

  assert.deepEqual(await deleteSessionTree(invoke, epoch, { scope: "global", projectPath: "/chats" }, read.value),
    { kind: "deleted", deleted: ["grandchild", "child", "second", "parent"] });
  assert.deepEqual(requests[0], { expectedHostEpoch: epoch, scope: "global", projectId: null, projectPath: "/chats", sessionId: "parent",
    confirmedTitle: "parent", descendants: ["child", "grandchild", "second"] });
  await deleteSessionTree(invoke, epoch, { scope: "project", projectId: "p1", projectPath: "/p1" }, read.value);
  assert.deepEqual([requests[1].scope, requests[1].projectId, requests[1].projectPath], ["project", "p1", "/p1"]);

  answer = { status: "session_in_use", hostEpoch: epoch, sessionId: "parent", deleted: [], blockingSessionId: "grandchild" };
  assert.deepEqual(await deleteSessionTree(invoke, epoch, { scope: "global", projectPath: "/chats" }, read.value),
    { kind: "error", code: "session_in_use", deleted: [], blocking: "grandchild" });
  answer = { status: "delete_partial", hostEpoch: epoch, sessionId: "parent", deleted: ["grandchild"], blockingSessionId: "child" };
  assert.deepEqual(await deleteSessionTree(invoke, epoch, { scope: "global", projectPath: "/chats" }, read.value),
    { kind: "error", code: "delete_partial", deleted: ["grandchild"], blocking: "child" });
  // An answer about another session confirms nothing; a failed call may have deleted something.
  answer = { status: "ok", hostEpoch: epoch, sessionId: "other", deleted: [], blockingSessionId: null };
  assert.equal((await deleteSessionTree(invoke, epoch, { scope: "global", projectPath: "/chats" }, read.value)).kind, "error");
  assert.deepEqual(await deleteSessionTree(async () => { throw new Error("lost"); }, epoch, { scope: "global", projectPath: "/chats" }, read.value),
    { kind: "error", code: "delete_unconfirmed", deleted: [], blocking: null });
});

test("every refusal is told in the words of the window, in every language", () => {
  for (const code of ["descendants_changed", "session_in_use", "session_missing", "delete_partial", "busy", "stale_epoch", "closed", "read_failed", "delete_unconfirmed"]) {
    const key = sessionTreeMessage(code);
    assert.notEqual(translate("fr", key), key, code);
  }
  assert.match(sessionTreeMessage("descendants_changed"), /changed/);
});
