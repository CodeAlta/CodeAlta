import assert from "node:assert/strict";
import test from "node:test";
import type { WorkspaceSnapshot } from "#neoastra";
import { createMutationCapability } from "./sessionOperations";
import { confirmDeletedSession, createSessionDeletion, deletedSessionRecovery, deleteSelectionCurrent, hostSessionPresence, sessionDeletionMessage } from "./sessionDeletion";

const epoch = "11111111-1111-4111-8111-111111111111";
const other = "22222222-2222-4222-8222-222222222222";
const project = { scope: "project" as const, projectId: "p1", projectPath: "C:\\disposable-owned\\project" };
const global = { scope: "global" as const, projectPath: "C:\\disposable-owned\\global" };
const reply = (status: string, overrides = {}) => ({ status, hostEpoch: epoch, scope: project.scope,
  projectId: project.projectId, projectPath: project.projectPath, sessionId: "s1", ...overrides });
const snapshot: WorkspaceSnapshot = { configured: true, projects: [{ id: "p1", name: "Project", path: project.projectPath, archived: false }],
  sessions: [{ messageCount: null, automationId: null, worktreePath: null, worktreeRoot: null, worktreeName: null, worktreeMissing: false, forkedFromSessionId: null, forkedFromTitle: null, forkKind: null, forkTurnCount: 0, rewindCount: 0, archived: false, archivedAt: null, spaceId: null, createdAt: null, id: "s2", title: "Kept", fullTitle: "Kept", fullTitleTruncated: false, parentSessionId: null, scopeKind: "project", projectId: "p1", lineageIssue: null, workspacePath: project.projectPath, providerKey: "fixture", updatedAt: "2026-01-01T00:00:00Z" }],
  projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false };

test("a delete freezes scope, session identity and listed title; recovered selection stays in exact scope", async () => {
  let request: unknown;
  const remove = createSessionDeletion(async (value, options) => { request = value; assert.equal(options.timeoutMilliseconds, 10_000); return reply("ok"); });
  const result = await remove(epoch, project, "s1", "Exact title", createMutationCapability(epoch));
  assert.deepEqual(request, { expectedHostEpoch: epoch, ...project, sessionId: "s1", confirmedTitle: "Exact title" });
  assert.deepEqual(result, { kind: "deleted", target: project, id: "s1" });
  if (result.kind !== "deleted") throw new Error("Expected deletion");
  assert.deepEqual(deletedSessionRecovery(snapshot, result), { projectId: "p1", sessionId: "s2" });
  assert.equal(deleteSelectionCurrent(result, "p1", "s1"), true);
  assert.equal(deleteSelectionCurrent(result, "p1", "s2"), false);
  assert.equal(deleteSelectionCurrent(result, null, "s1"), false);
  assert.equal(deletedSessionRecovery({ ...snapshot, sessions: [...snapshot.sessions, { ...snapshot.sessions[0], id: "s1" }] }, result), undefined);
  assert.equal(deletedSessionRecovery({ ...snapshot, sessionsTruncated: true }, result), undefined);
  assert.equal(deletedSessionRecovery({ ...snapshot, projects: [{ ...snapshot.projects[0], path: global.projectPath }] }, result), undefined);
  assert.equal(deletedSessionRecovery({ ...snapshot, projects: [{ ...snapshot.projects[0], archived: true }] }, result), undefined);
  assert.deepEqual(deletedSessionRecovery({ ...snapshot, sessions: [] }, result), { projectId: "p1", sessionId: null });
});

test("a truncated catalog confirms a deletion by asking the host whether that session is gone", async () => {
  const result = { target: project, id: "s1" };
  const truncated = { ...snapshot, sessionsTruncated: true };
  const asked: string[] = [];
  const presence = (answer: "absent" | "present" | "unknown") => async (id: string) => { asked.push(id); return answer; };
  assert.deepEqual(await confirmDeletedSession(truncated, result, presence("absent")), { projectId: "p1", sessionId: "s2" });
  assert.deepEqual(asked, ["s1"]);
  assert.equal(await confirmDeletedSession(truncated, result, presence("present")), undefined);
  assert.equal(await confirmDeletedSession(truncated, result, presence("unknown")), undefined);
  asked.length = 0;
  // A complete catalog decides alone, and one that still lists the session or lost its project needs no question.
  assert.deepEqual(await confirmDeletedSession(snapshot, result, presence("present")), { projectId: "p1", sessionId: "s2" });
  assert.equal(await confirmDeletedSession({ ...truncated, sessions: [...snapshot.sessions, { ...snapshot.sessions[0], id: "s1" }] }, result, presence("absent")), undefined);
  assert.equal(await confirmDeletedSession({ ...truncated, projects: [{ ...snapshot.projects[0], archived: true }] }, result, presence("absent")), undefined);
  assert.deepEqual(asked, []);
});

test("the host says a session is absent only when it reads every session and finds none with that id", async () => {
  const replyFor = (status: string, overrides = {}) => async () => ({ status, hostEpoch: epoch, sessionId: "s1", root: null, rootAtWork: false,
    children: [], descendantIds: [], directCount: 0, totalCount: 0, truncated: false, ...overrides });
  assert.equal(await hostSessionPresence(replyFor("session_missing"), epoch)("s1"), "absent");
  assert.equal(await hostSessionPresence(replyFor("ok", { root: snapshot.sessions[0] }), epoch)("s1"), "present");
  assert.equal(await hostSessionPresence(replyFor("read_failed"), epoch)("s1"), "unknown");
  assert.equal(await hostSessionPresence(replyFor("session_missing", { hostEpoch: other }), epoch)("s1"), "unknown");
  assert.equal(await hostSessionPresence(replyFor("session_missing", { sessionId: "s2" }), epoch)("s1"), "unknown");
  assert.equal(await hostSessionPresence(async () => { throw new Error("lost"); }, epoch)("s1"), "unknown");
  assert.equal(await hostSessionPresence(replyFor("session_missing"), undefined)("s1"), "unknown");
});

test("an unusable title, a stale host and malformed responses never delete or retry automatically", async () => {
  let calls = 0;
  const capability = createMutationCapability(epoch);
  const remove = createSessionDeletion(async () => { calls++; return reply("ok"); });
  for (const title of ["", " ", " spaced", "control\n", "\ud800", "x".repeat(257)])
    assert.deepEqual(await remove(epoch, project, "s1", title, capability), { kind: "error", code: "invalid_confirmation" });
  assert.deepEqual(await remove(undefined, project, "s1", "Exact", undefined), { kind: "error", code: "unconfigured" });
  assert.equal(calls, 0);
  assert.deepEqual(await createSessionDeletion(async () => reply("ok", { sessionId: "s2" }))(epoch, project, "s1", "Exact", capability),
    { kind: "error", code: "delete_unconfirmed" });
  assert.deepEqual(await createSessionDeletion(async () => { throw new Error("response lost"); })(epoch, project, "s1", "Exact", capability),
    { kind: "error", code: "delete_unconfirmed" });
  assert.deepEqual(await createSessionDeletion(async () => reply("stale_epoch", { hostEpoch: other }))(epoch, project, "s1", "Exact", capability),
    { kind: "error", code: "stale_epoch" });
  assert.equal(capability.canMutate(), false);
  assert.match(sessionDeletionMessage("delete_unconfirmed"), /no retry/i);
});

test("one in-flight deletion only, including after switching project selection", async () => {
  let settle!: (value: ReturnType<typeof reply>) => void;
  let calls = 0;
  const remove = createSessionDeletion(async () => { calls++; return new Promise(resolve => { settle = resolve; }); });
  const capability = createMutationCapability(epoch);
  const pending = remove(epoch, project, "s1", "Exact", capability);
  assert.deepEqual(await remove(epoch, global, "g1", "Global", capability), { kind: "error", code: "busy" });
  settle(reply("ok"));
  const result = await pending;
  assert.equal(result.kind, "deleted");
  if (result.kind !== "deleted") throw new Error("Expected deletion");
  assert.equal(deleteSelectionCurrent(result, null, "g1"), false);
  assert.equal(calls, 1);
});
