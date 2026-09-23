import assert from "node:assert/strict";
import test from "node:test";
import type { WorkspaceSnapshot } from "#neoastra";
import { createMutationCapability } from "./sessionOperations";
import { createSessionRename, renamedSessionVisible, renameSelectionCurrent, sessionRenameMessage } from "./sessionRename";

const epoch = "11111111-1111-4111-8111-111111111111";
const other = "22222222-2222-4222-8222-222222222222";
const project = { scope: "project" as const, projectId: "p1", projectPath: "C:\\test-owned\\project" };
const global = { scope: "global" as const, projectPath: "C:\\test-owned\\global" };
const reply = (status: string, overrides = {}) => ({ status, hostEpoch: epoch, scope: project.scope,
  projectId: project.projectId, projectPath: project.projectPath, sessionId: "s1", title: status === "ok" ? "New" : null, ...overrides });
const snapshot: WorkspaceSnapshot = { configured: true, projects: [{ id: "p1", name: "Project", path: project.projectPath, archived: false }],
  sessions: [
    { id: "s1", title: "New", workspacePath: project.projectPath, providerKey: "fixture", updatedAt: "2026-01-01T00:00:00Z" },
    { id: "s2", title: "Untouched", workspacePath: project.projectPath, providerKey: "fixture", updatedAt: "2026-01-01T00:00:00Z" },
  ], projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false };

test("exact owned rename response and refreshed catalog display cannot select a different session or project", async () => {
  const calls: unknown[] = [];
  const rename = createSessionRename(async (request, options) => {
    calls.push(request);
    assert.equal(options.timeoutMilliseconds, 10_000);
    return request.scope === "global" ? reply("ok", { scope: "global", projectId: null, projectPath: global.projectPath,
      sessionId: "g1", title: "Global new" }) : reply("ok");
  });
  const capability = createMutationCapability(epoch);
  const result = await rename(epoch, project, "s1", "New", capability);
  assert.equal(result.kind, "renamed");
  if (result.kind !== "renamed") throw new Error("Expected renamed result");
  assert.deepEqual(calls[0], { expectedHostEpoch: epoch, ...project, sessionId: "s1", title: "New" });
  assert.equal(renamedSessionVisible(snapshot, result), true);
  assert.equal(renameSelectionCurrent(result, "p1", "s1"), true);
  assert.equal(renameSelectionCurrent(result, "p1", "s2"), false);
  assert.equal(renameSelectionCurrent(result, null, "s1"), false);
  assert.equal(renamedSessionVisible({ ...snapshot, sessions: [{ ...snapshot.sessions[0], id: "s2" }] }, result), false);
  assert.equal(renamedSessionVisible({ ...snapshot, sessions: [{ ...snapshot.sessions[0], title: "Old" }] }, result), false);
  assert.equal(renamedSessionVisible({ ...snapshot, projects: [{ ...snapshot.projects[0], archived: true }] }, result), false);
  const globalResult = await rename(epoch, global, "g1", "Global new", capability);
  assert.equal(globalResult.kind, "renamed");
  assert.deepEqual(calls[1], { expectedHostEpoch: epoch, ...global, projectId: null, sessionId: "g1", title: "Global new" });
});

test("invalid titles, stale host, wrong-session response and uncertainty never retry", async () => {
  let calls = 0;
  const capability = createMutationCapability(epoch);
  const rename = createSessionRename(async () => { calls++; return reply("ok"); });
  for (const title of ["", " ", " padded", "control\n", "\ud800", "x".repeat(257)])
    assert.deepEqual(await rename(epoch, project, "s1", title, capability), { kind: "error", code: "invalid_scope" });
  assert.deepEqual(await rename(undefined, project, "s1", "New", undefined), { kind: "error", code: "unconfigured" });
  assert.equal(calls, 0);
  const wrong = createSessionRename(async () => reply("ok", { sessionId: "s2" }));
  assert.deepEqual(await wrong(epoch, project, "s1", "New", capability), { kind: "error", code: "rename_unconfirmed" });
  const lost = createSessionRename(async () => { throw new Error("response lost"); });
  assert.deepEqual(await lost(epoch, project, "s1", "New", capability), { kind: "error", code: "rename_unconfirmed" });
  const stale = createSessionRename(async () => reply("stale_epoch", { hostEpoch: other }));
  assert.deepEqual(await stale(epoch, project, "s1", "New", capability), { kind: "error", code: "stale_epoch" });
  assert.equal(capability.canMutate(), false);
  assert.match(sessionRenameMessage("rename_unconfirmed"), /do not retry automatically/i);
});

test("one in-flight rename only, including after a project switch", async () => {
  let settle!: (value: ReturnType<typeof reply>) => void;
  const capability = createMutationCapability(epoch);
  let calls = 0;
  const rename = createSessionRename(async () => { calls++; return new Promise(resolve => { settle = resolve; }); });
  const pending = rename(epoch, project, "s1", "New", capability);
  assert.deepEqual(await rename(epoch, global, "g1", "Global new", capability), { kind: "error", code: "busy" });
  settle(reply("ok"));
  const result = await pending;
  assert.equal(result.kind, "renamed");
  if (result.kind !== "renamed") throw new Error("Expected renamed result");
  assert.equal(renameSelectionCurrent(result, null, "g1"), false);
  assert.equal(calls, 1);
});
