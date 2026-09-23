import assert from "node:assert/strict";
import test from "node:test";
import type { WorkspaceSnapshot } from "#neoastra";
import { createMutationCapability } from "./sessionOperations";
import { createSessionDeletion, deletedSessionRecovery, deleteSelectionCurrent, sessionDeletionMessage } from "./sessionDeletion";

const epoch = "11111111-1111-4111-8111-111111111111";
const other = "22222222-2222-4222-8222-222222222222";
const project = { scope: "project" as const, projectId: "p1", projectPath: "C:\\disposable-owned\\project" };
const global = { scope: "global" as const, projectPath: "C:\\disposable-owned\\global" };
const reply = (status: string, overrides = {}) => ({ status, hostEpoch: epoch, scope: project.scope,
  projectId: project.projectId, projectPath: project.projectPath, sessionId: "s1", ...overrides });
const snapshot: WorkspaceSnapshot = { configured: true, projects: [{ id: "p1", name: "Project", path: project.projectPath, archived: false }],
  sessions: [{ id: "s2", title: "Kept", fullTitle: "Kept", fullTitleTruncated: false, parentSessionId: null, scopeKind: "project", projectId: "p1", lineageIssue: null, workspacePath: project.projectPath, providerKey: "fixture", updatedAt: "2026-01-01T00:00:00Z" }],
  projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false };

test("confirmed delete freezes scope, session identity and title; recovered selection stays in exact scope", async () => {
  let request: unknown;
  const remove = createSessionDeletion(async (value, options) => { request = value; assert.equal(options.timeoutMilliseconds, 10_000); return reply("ok"); });
  const result = await remove(epoch, project, "s1", "Exact title", "Exact title", createMutationCapability(epoch));
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

test("wrong confirmation, stale host and malformed responses never delete or retry automatically", async () => {
  let calls = 0;
  const capability = createMutationCapability(epoch);
  const remove = createSessionDeletion(async () => { calls++; return reply("ok"); });
  for (const title of ["", " ", " spaced", "control\n", "\ud800", "x".repeat(257)])
    assert.deepEqual(await remove(epoch, project, "s1", title, title, capability), { kind: "error", code: "invalid_confirmation" });
  assert.deepEqual(await remove(epoch, project, "s1", "Exact", "exact", capability), { kind: "error", code: "invalid_confirmation" });
  assert.deepEqual(await remove(undefined, project, "s1", "Exact", "Exact", undefined), { kind: "error", code: "unconfigured" });
  assert.equal(calls, 0);
  assert.deepEqual(await createSessionDeletion(async () => reply("ok", { sessionId: "s2" }))(epoch, project, "s1", "Exact", "Exact", capability),
    { kind: "error", code: "delete_unconfirmed" });
  assert.deepEqual(await createSessionDeletion(async () => { throw new Error("response lost"); })(epoch, project, "s1", "Exact", "Exact", capability),
    { kind: "error", code: "delete_unconfirmed" });
  assert.deepEqual(await createSessionDeletion(async () => reply("stale_epoch", { hostEpoch: other }))(epoch, project, "s1", "Exact", "Exact", capability),
    { kind: "error", code: "stale_epoch" });
  assert.equal(capability.canMutate(), false);
  assert.match(sessionDeletionMessage("delete_unconfirmed"), /no retry/i);
});

test("one in-flight deletion only, including after switching project selection", async () => {
  let settle!: (value: ReturnType<typeof reply>) => void;
  let calls = 0;
  const remove = createSessionDeletion(async () => { calls++; return new Promise(resolve => { settle = resolve; }); });
  const capability = createMutationCapability(epoch);
  const pending = remove(epoch, project, "s1", "Exact", "Exact", capability);
  assert.deepEqual(await remove(epoch, global, "g1", "Global", "Global", capability), { kind: "error", code: "busy" });
  settle(reply("ok"));
  const result = await pending;
  assert.equal(result.kind, "deleted");
  if (result.kind !== "deleted") throw new Error("Expected deletion");
  assert.equal(deleteSelectionCurrent(result, null, "g1"), false);
  assert.equal(calls, 1);
});
