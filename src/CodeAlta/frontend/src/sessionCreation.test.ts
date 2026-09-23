import assert from "node:assert/strict";
import test from "node:test";
import type { WorkspaceSnapshot } from "#neoastra";
import { createMutationCapability } from "./sessionOperations";
import { createSessionCreation, createdSessionSelection, sessionCreationMessage } from "./sessionCreation";

const epoch = "11111111-1111-4111-8111-111111111111";
const other = "22222222-2222-4222-8222-222222222222";
const project = { scope: "project" as const, projectId: "project-1", projectPath: "C:\\test-owned\\project" };
const global = { scope: "global" as const };
const reply = (status: string, scope: string | null = "project", projectId: string | null = project.projectId,
  projectPath: string | null = project.projectPath, hostEpoch: string | null = epoch,
  sessionId: string | null = "draft-1", workspacePath: string | null = project.projectPath) =>
  ({ status, scope, projectId, projectPath, hostEpoch, sessionId, workspacePath });
const empty: WorkspaceSnapshot = { configured: true, projects: [], sessions: [], projectsTruncated: false,
  sessionsTruncated: false, displayTextTruncated: false };

test("exact selected project and global requests produce switchable persisted catalog sessions", async () => {
  const calls: unknown[] = [];
  const create = createSessionCreation(async (request, options) => {
    assert.equal(options.timeoutMilliseconds, 10000);
    calls.push(request);
    return request.scope === "global"
      ? reply("ok", "global", null, null, epoch, "global-1", "C:\\test-owned\\global") : reply("ok");
  });
  const capability = createMutationCapability(epoch);
  const projectResult = await create(epoch, project, "My draft", capability);
  assert.equal(projectResult.kind, "created");
  const globalResult = await create(epoch, global, null, capability);
  assert.equal(globalResult.kind, "created");
  assert.deepEqual(calls, [
    { expectedHostEpoch: epoch, scope: "project", projectId: project.projectId, projectPath: project.projectPath, title: "My draft" },
    { expectedHostEpoch: epoch, scope: "global", projectId: null, projectPath: null, title: null },
  ]);
  if (projectResult.kind !== "created" || globalResult.kind !== "created") throw new Error("Expected created results");
  const snapshot: WorkspaceSnapshot = { ...empty,
    projects: [{ id: project.projectId, path: project.projectPath, name: "Project", archived: false }],
    sessions: [
      { id: "draft-1", title: "My draft", workspacePath: project.projectPath, providerKey: "fixture", updatedAt: "2026-01-01T00:00:00Z" },
      { id: "global-1", title: "Global Session", workspacePath: "C:\\test-owned\\global", providerKey: "fixture", updatedAt: "2026-01-01T00:00:00Z" },
    ],
  };
  assert.deepEqual(createdSessionSelection(snapshot, projectResult), { projectId: project.projectId, sessionId: "draft-1" });
  assert.deepEqual(createdSessionSelection(snapshot, globalResult), { projectId: null, sessionId: "global-1" });
  assert.equal(createdSessionSelection({ ...snapshot, sessions: [] }, projectResult), undefined);
  assert.equal(createdSessionSelection({ ...snapshot, projects: [{ ...snapshot.projects[0], archived: true }] }, projectResult), undefined);
  assert.equal(createdSessionSelection({ ...snapshot, sessions: [{ ...snapshot.sessions[0], workspacePath: "C:\\other" }] }, projectResult), undefined);
});

test("invalid scope, title, catalog-only authority and stale epoch refuse mutation", async () => {
  let calls = 0;
  const create = createSessionCreation(async () => { calls++; return reply("ok"); });
  const capability = createMutationCapability(epoch);
  for (const target of [{ ...project, projectId: " wrong" }, { ...project, projectPath: "relative" }])
    assert.deepEqual(await create(epoch, target, null, capability), { kind: "error", code: "invalid_scope" });
  assert.deepEqual(await create(epoch, { scope: "other" } as never, null, capability), { kind: "error", code: "invalid_scope" });
  assert.deepEqual(await create(epoch, global, "\ud800", capability), { kind: "error", code: "invalid_scope" });
  assert.deepEqual(await create(epoch, global, "x".repeat(257), capability), { kind: "error", code: "invalid_scope" });
  assert.deepEqual(await create(undefined, global, null, undefined), { kind: "error", code: "unconfigured" });
  assert.equal(calls, 0);
  const stale = createSessionCreation(async () => reply("stale_epoch", "global", null, null, other));
  assert.deepEqual(await stale(epoch, global, null, capability), { kind: "error", code: "stale_epoch" });
  assert.equal(capability.canMutate(), false);
});

test("duplicate admission and uncertain or mismatched outcome never silently retry or switch", async () => {
  const capability = createMutationCapability(epoch);
  let settle!: (result: ReturnType<typeof reply>) => void;
  let calls = 0;
  const create = createSessionCreation(async () => { calls++; return new Promise(resolve => { settle = resolve; }); });
  const first = create(epoch, project, null, capability);
  assert.deepEqual(await create(epoch, project, null, capability), { kind: "error", code: "busy" });
  settle(reply("ok", "project", "different", project.projectPath));
  assert.deepEqual(await first, { kind: "error", code: "create_unconfirmed" });
  assert.equal(calls, 1);
  const lost = createSessionCreation(async () => { throw new Error("response lost after persistence"); });
  assert.deepEqual(await lost(epoch, project, null, capability), { kind: "error", code: "create_unconfirmed" });
  const mismatchedRefusal = createSessionCreation(async () => reply("project_missing", "project", "different"));
  assert.deepEqual(await mismatchedRefusal(epoch, project, null, capability), { kind: "error", code: "create_unconfirmed" });
  assert.match(sessionCreationMessage("create_unconfirmed"), /no automatic retry/i);
});
