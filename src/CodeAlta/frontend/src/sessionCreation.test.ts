import assert from "node:assert/strict";
import test from "node:test";
import type { WorkspaceSnapshot } from "#neoastra";
import { createMutationCapability } from "./sessionOperations";
import { createSessionCreation, createdSessionSelection, sessionCreationMessage, validSpaceId, worktreeCreationMessage } from "./sessionCreation";

const epoch = "11111111-1111-4111-8111-111111111111";
const other = "22222222-2222-4222-8222-222222222222";
const project = { scope: "project" as const, projectId: "project-1", projectPath: "C:\\test-owned\\project" };
const global = { scope: "global" as const };
const reply = (status: string, scope: string | null = "project", projectId: string | null = project.projectId,
  projectPath: string | null = project.projectPath, hostEpoch: string | null = epoch,
  sessionId: string | null = "draft-1", workspacePath: string | null = project.projectPath) =>
  ({ status, scope, projectId, projectPath, hostEpoch, sessionId, workspacePath, providerId: null, worktreePath: null as string | null,
    reason: null as string | null, message: null as string | null, warning: null as string | null });
const empty: WorkspaceSnapshot = { configured: true, projects: [], sessions: [], projectsTruncated: false,
  sessionsTruncated: false, displayTextTruncated: false };

test("explicit provider is frozen and mismatched success cannot certify creation", async () => {
  let captured: unknown;
  const create = createSessionCreation(async request => {
    captured = request;
    return { ...reply("ok"), providerId: "default" };
  });
  const result = await create(epoch, project, "Keep title", createMutationCapability(epoch), "alternate");
  assert.equal((captured as { providerId: string }).providerId, "alternate");
  assert.equal(Object.isFrozen(captured), true);
  assert.deepEqual(result, { kind: "error", code: "create_unconfirmed" });
});

test("a session is asked in the permission mode chosen before it exists, and a refused mode is said", async () => {
  const calls: { permissionMode: string | null }[] = [];
  const create = createSessionCreation(async request => { calls.push(request); return request.permissionMode === "plan" ? reply("invalid_permission_mode") : reply("ok"); });
  const capability = createMutationCapability(epoch);
  assert.equal((await create(epoch, project, null, capability, null, null, null, "mode-1", "bypassPermissions")).kind, "created");
  assert.deepEqual(await create(epoch, project, null, capability, null, null, null, "mode-2", "plan"), { kind: "error", code: "invalid_permission_mode" });
  assert.deepEqual(await create(epoch, project, null, capability, null, null, null, "mode-3", " plan"), { kind: "error", code: "invalid_scope" });
  assert.deepEqual(calls.map(call => call.permissionMode), ["bypassPermissions", "plan"], "a malformed mode is not sent");
  assert.match(sessionCreationMessage("invalid_permission_mode"), /permission mode/);
});

test("exact selected project and global requests produce switchable persisted catalog sessions", async () => {
  const calls: unknown[] = [];
  let ids = 0;
  const create = createSessionCreation(async (request, options) => {
    assert.equal(options.timeoutMilliseconds, 600_000);
    calls.push(request);
    return request.scope === "global"
      ? reply("ok", "global", null, null, epoch, "global-1", "C:\\test-owned\\global") : reply("ok");
  }, undefined, () => `request-${++ids}`);
  const capability = createMutationCapability(epoch);
  const projectResult = await create(epoch, project, "My draft", capability);
  assert.equal(projectResult.kind, "created");
  const globalResult = await create(epoch, global, null, capability, null, null, "business");
  assert.equal(globalResult.kind, "created");
  assert.deepEqual(calls, [
    { expectedHostEpoch: epoch, scope: "project", projectId: project.projectId, projectPath: project.projectPath, title: "My draft", providerId: null, worktree: false, baseBranch: null, spaceId: null, requestId: "request-1", permissionMode: null, preparedWorktreeId: null },
    { expectedHostEpoch: epoch, scope: "global", projectId: null, projectPath: null, title: null, providerId: null, worktree: false, baseBranch: null, spaceId: "business", requestId: "request-2", permissionMode: null, preparedWorktreeId: null },
  ]);
  if (projectResult.kind !== "created" || globalResult.kind !== "created") throw new Error("Expected created results");
  const snapshot: WorkspaceSnapshot = { ...empty,
    projects: [{ id: project.projectId, path: project.projectPath, name: "Project", archived: false }],
    sessions: [
      { messageCount: null, automationId: null, worktreePath: null, worktreeRoot: null, worktreeName: null, worktreeMissing: false, forkedFromSessionId: null, forkedFromTitle: null, forkKind: null, forkTurnCount: 0, rewindCount: 0, archived: false, archivedAt: null, spaceId: null, createdAt: null, id: "draft-1", title: "My draft", fullTitle: "My draft", fullTitleTruncated: false, parentSessionId: null, scopeKind: "project", projectId: project.projectId, lineageIssue: null, workspacePath: project.projectPath, providerKey: "fixture", updatedAt: "2026-01-01T00:00:00Z" },
      { messageCount: null, automationId: null, worktreePath: null, worktreeRoot: null, worktreeName: null, worktreeMissing: false, forkedFromSessionId: null, forkedFromTitle: null, forkKind: null, forkTurnCount: 0, rewindCount: 0, archived: false, archivedAt: null, spaceId: null, createdAt: null, id: "global-1", title: "Global Session", fullTitle: "Global Session", fullTitleTruncated: false, parentSessionId: null, scopeKind: "global", projectId: null, lineageIssue: null, workspacePath: "C:\\test-owned\\global", providerKey: "fixture", updatedAt: "2026-01-01T00:00:00Z" },
    ],
  };
  assert.deepEqual(createdSessionSelection(snapshot, projectResult), { projectId: project.projectId, sessionId: "draft-1" });
  assert.deepEqual(createdSessionSelection(snapshot, globalResult), { projectId: null, sessionId: "global-1" });
  assert.equal(createdSessionSelection({ ...snapshot, sessions: [] }, projectResult), undefined);
  assert.equal(createdSessionSelection({ ...snapshot, projects: [{ ...snapshot.projects[0], archived: true }] }, projectResult), undefined);
  assert.equal(createdSessionSelection({ ...snapshot, sessions: [{ ...snapshot.sessions[0], workspacePath: "C:\\other" }] }, projectResult), undefined);
  assert.equal(createdSessionSelection(snapshot, { ...projectResult, providerId: "alternate" }), undefined);
  assert.deepEqual(createdSessionSelection(snapshot, { ...projectResult, providerId: "fixture" }), { projectId: project.projectId, sessionId: "draft-1" });
  assert.equal(createdSessionSelection({ ...snapshot, sessions: [...snapshot.sessions, snapshot.sessions[0]] }, projectResult), undefined);
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
  for (const provider of ["", " wrong", "wrong ", "bad\nvalue", "\ud800", "x".repeat(257)])
    assert.deepEqual(await create(epoch, global, null, capability, provider), { kind: "error", code: "provider_unavailable" });
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

test("a lost answer is asked again by the identifier of its request, and never sent twice", async () => {
  const capability = createMutationCapability(epoch);
  const sent: unknown[] = [], asked: unknown[] = [];
  let outcome = reply("ok");
  const create = createSessionCreation(async request => { sent.push(request); throw new Error("timed out"); },
    async (request, options) => { asked.push(request); assert.equal(options.timeoutMilliseconds, 600_000); return outcome; }, () => "lost-1");
  // The session was created: the answer that comes back is the one of the creation.
  assert.deepEqual(await create(epoch, project, null, capability),
    { kind: "created", target: project, id: "draft-1", path: project.projectPath, providerId: null, worktreePath: null, warning: null });
  assert.equal(sent.length, 1);
  assert.deepEqual(asked, [{ expectedHostEpoch: epoch, requestId: "lost-1" }]);
  // The host admitted no such request: nothing was created, and that is definite.
  outcome = reply("unknown", null, null, null, epoch, null, null);
  assert.deepEqual(await create(epoch, project, null, capability), { kind: "error", code: "not_created" });
  assert.match(sessionCreationMessage("not_created"), /no session was created/);
  // A refusal is the refusal; an outcome that cannot be read either stays unconfirmed.
  outcome = reply("project_missing");
  assert.deepEqual(await create(epoch, project, null, capability), { kind: "error", code: "project_missing" });
  const unreachable = createSessionCreation(async () => { throw new Error("timed out"); }, async () => { throw new Error("host gone"); });
  assert.deepEqual(await unreachable(epoch, project, null, capability), { kind: "error", code: "create_unconfirmed" });
  assert.equal(sent.length, 3, "a creation is sent once");
});

test("a session in a new worktree names its base, waits for git, and a refusal of git is definite", async () => {
  const worktree = "C:\\test-owned\\worktrees\\project\\quiet-heron";
  const calls: { request: unknown; timeout: number }[] = [];
  let answer = { ...reply("ok"), worktreePath: worktree as string | null };
  const create = createSessionCreation(async (request, options) => { calls.push({ request, timeout: options.timeoutMilliseconds }); return answer; },
    undefined, () => "worktree-request");
  const capability = createMutationCapability(epoch);

  const created = await create(epoch, project, null, capability, null, { worktree: true, base: "main" });
  assert.deepEqual(created, { kind: "created", target: project, id: "draft-1", path: project.projectPath, providerId: null, worktreePath: worktree, warning: null });
  assert.deepEqual(calls, [{ request: { expectedHostEpoch: epoch, scope: "project", projectId: project.projectId, projectPath: project.projectPath, title: null,
    providerId: null, worktree: true, baseBranch: "main", spaceId: null, requestId: "worktree-request", permissionMode: null, preparedWorktreeId: null }, timeout: 600_000 }]);
  // Submodules git could not check out: the session is created in its worktree, with what git said.
  answer = { ...reply("ok"), worktreePath: worktree, warning: " fatal: clone of 'lib' failed " };
  assert.deepEqual(await create(epoch, project, null, capability, null, { worktree: true, base: null }),
    { kind: "created", target: project, id: "draft-1", path: project.projectPath, providerId: null, worktreePath: worktree, warning: "fatal: clone of 'lib' failed" });
  calls.pop();

  // The place of a project that chose none is its folder: nothing is asked of git.
  answer = reply("ok");
  assert.equal((await create(epoch, project, null, capability, null, { worktree: false, base: "ignored" })).kind, "created");
  assert.deepEqual((calls[1].request as { worktree: boolean; baseBranch: string | null }).baseBranch, null);
  assert.equal(calls[1].timeout, 600_000, "the page waits as long as the host gives any creation");

  // What was asked and what was created must agree: a worktree that was not asked for, or one that is missing.
  answer = { ...reply("ok"), worktreePath: worktree };
  assert.deepEqual(await create(epoch, project, null, capability), { kind: "error", code: "create_unconfirmed" });
  answer = reply("ok");
  assert.deepEqual(await create(epoch, project, null, capability, null, { worktree: true, base: null }), { kind: "error", code: "create_unconfirmed" });

  answer = { ...reply("worktree_failed", "project", project.projectId, project.projectPath, epoch, null, null), reason: "no_commit", message: "fatal: not a valid object name" };
  assert.deepEqual(await create(epoch, project, null, capability, null, { worktree: true, base: null }),
    { kind: "error", code: "worktree_failed", reason: "no_commit", message: "fatal: not a valid object name" });
  assert.match(worktreeCreationMessage("no_commit", null), /no commit yet/);
  assert.match(worktreeCreationMessage("failed", " fatal: boom "), /Git could not create the worktree\. fatal: boom$/);

  // The worktree made while the prompt was typed is named; only a worktree session names one, and by its GUID.
  const prepared = "33333333-3333-4333-8333-333333333333";
  answer = { ...reply("ok"), worktreePath: worktree };
  assert.equal((await create(epoch, project, null, capability, null, { worktree: true, base: null }, null, "prepared-request", null, prepared)).kind, "created");
  assert.equal((calls[calls.length - 1].request as { preparedWorktreeId: string | null }).preparedWorktreeId, prepared);
  assert.deepEqual(await create(epoch, project, null, capability, null, null, null, "plain", null, prepared), { kind: "error", code: "invalid_scope" });
  assert.deepEqual(await create(epoch, project, null, capability, null, { worktree: true, base: null }, null, "bad", null, "not-a-guid"), { kind: "error", code: "invalid_scope" });

  // A worktree is a checkout of a project, and its base is a name.
  const count = calls.length;
  assert.deepEqual(await create(epoch, global, null, capability, null, { worktree: true, base: null }), { kind: "error", code: "invalid_scope" });
  assert.deepEqual(await create(epoch, project, null, capability, null, { worktree: true, base: " main" }), { kind: "error", code: "invalid_scope" });
  assert.equal(calls.length, count);
});

test("a new session names the space the window shows, which the host may no longer have", async () => {
  const requests: unknown[] = [];
  let answer = reply("ok");
  const create = createSessionCreation(async request => { requests.push(request); return answer; });
  const capability = createMutationCapability(epoch);

  assert.equal((await create(epoch, project, null, capability, null, null, "work")).kind, "created");
  assert.equal((requests[0] as { spaceId: string | null }).spaceId, "work");

  // What is no identifier of a space is not sent.
  assert.deepEqual(await create(epoch, project, null, capability, null, null, "Not A Space"), { kind: "error", code: "invalid_scope" });
  assert.equal(requests.length, 1);
  assert.equal(validSpaceId("default"), true);
  assert.equal(validSpaceId("a"), false);

  answer = reply("space_missing");
  assert.deepEqual(await create(epoch, project, null, capability, null, null, "gone"), { kind: "error", code: "space_missing" });
  assert.match(sessionCreationMessage("space_missing"), /no longer exists/);
  assert.match(sessionCreationMessage("provider_outside_space"), /not available in the space/);
});
