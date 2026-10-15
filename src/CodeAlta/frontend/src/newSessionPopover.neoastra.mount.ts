// The real shell, with synthetic creation/catalog services only. No native host or user data is touched.
import type { SessionChoicesRequest, WorkspaceCreateSessionOutcomeRequest, WorkspaceCreateSessionProgressResponse, WorkspaceCreateSessionRequest,
  WorkspaceCreateSessionResponse, WorkspaceCreationStep, WorkspaceDraftPermissionModesRequest, WorkspaceDraftPermissionModesResponse, WorkspaceSnapshot,
  WorkspaceSession } from "#neoastra";
import { workspace as baseWorkspace, sessionOperations as baseOperations, modelCatalog as baseModels } from "./settingsShell.neoastra.mount";
import { worktrees as baseWorktrees } from "./demo-api";
export * from "./projectFocus.neoastra.mount";

const created: WorkspaceSession[] = [];
const modelWaiters: (() => void)[] = [];
// The answers the host gave, by the identifier of their request, as the host keeps them.
const outcomes = new Map<string, WorkspaceCreateSessionResponse>();
// What the test sets before a creation: it is held, refused, answered for another provider, its answer is lost on
// the way, or its choices cannot be read.
const fixture = { creates: [] as WorkspaceCreateSessionRequest[], hold: false, refused: false, uncertain: false, lost: false, choicesFail: false,
  outcomeReads: [] as string[],
  // The steps the host says while it creates the session, as the test sets them.
  steps: [] as WorkspaceCreationStep[], progressReads: 0,
  missingModel: localStorage.getItem("newSessionFixtureMissingModel") === "true",
  failModels: localStorage.getItem("newSessionFixtureFailModels") === "true",
  holdModels: localStorage.getItem("newSessionFixtureHoldModels") === "true", modelReads: 0,
  releaseModels: () => { for (const resolve of modelWaiters.splice(0)) resolve(); },
  release: () => {}, choiceReads: [] as string[], permissionReads: [] as string[] };
Object.assign(window, { newSessionFixture: fixture });
// A model with several efforts, so that the reasoning has a slider, and one without any.
const models = () => [{ id: "new", name: "New model", efforts: ["Low", "Medium", "High"], defaultEffort: "Medium" },
  { id: "old", name: "Old model", efforts: [] as string[], defaultEffort: null }].filter(model => !fixture.missingModel || model.id !== "new");
// The alternate provider has the permission modes of Claude Code, configured with one of them; the other one has none.
const permissionModes = (providerId: string | null) => providerId === "alternate" ? ["default", "acceptEdits", "auto", "dontAsk", "bypassPermissions"].map(id => ({ id })) : [];
const defaultPermissionMode = (providerId: string | null) => providerId === "alternate" ? "acceptEdits" : null;
export const workspace = {
  ...baseWorkspace,
  draftPermissionModes: async (request: WorkspaceDraftPermissionModesRequest): Promise<WorkspaceDraftPermissionModesResponse> => {
    fixture.permissionReads.push(request.providerId);
    return { status: "ok", hostEpoch: request.expectedHostEpoch, providerId: request.providerId,
      permissionModes: permissionModes(request.providerId), defaultPermissionMode: defaultPermissionMode(request.providerId) };
  },
  snapshot: async () => { const value = await baseWorkspace.snapshot() as WorkspaceSnapshot; return { ...value, sessions: [...value.sessions, ...created] }; },
  createSession: async (request: WorkspaceCreateSessionRequest): Promise<WorkspaceCreateSessionResponse> => {
    fixture.creates.push(request);
    if (fixture.hold) await new Promise<void>(resolve => { fixture.release = resolve; });
    const id = `created-${fixture.creates.length}`;
    const snapshot = await baseWorkspace.snapshot() as WorkspaceSnapshot;
    const worktreePath = request.worktree ? "/fixture/worktree" : null;
    const workspacePath = request.projectPath ?? "/fixture/chats";
    if (!fixture.refused) created.push({ ...snapshot.sessions[0], id, title: request.title ?? "Untitled", fullTitle: request.title ?? "Untitled",
      scopeKind: request.scope, projectId: request.projectId, workspacePath,
      providerKey: request.providerId, worktreePath, worktreeRoot: worktreePath, worktreeName: worktreePath ? "worktree" : null, worktreeMissing: false });
    const response = { status: fixture.refused ? "worktree_failed" : "ok", hostEpoch: request.expectedHostEpoch, scope: request.scope,
      projectId: request.projectId, projectPath: request.projectPath, providerId: fixture.uncertain ? "unconfirmed" : request.providerId, sessionId: fixture.refused ? null : id,
      workspacePath, worktreePath, reason: fixture.refused ? "invalid" : null, message: null };
    if (request.requestId) outcomes.set(request.requestId, response);
    if (fixture.lost) throw new Error("synthetic lost answer: the wait ended");
    return response;
  },
  createSessionProgress: async (request: WorkspaceCreateSessionOutcomeRequest): Promise<WorkspaceCreateSessionProgressResponse> => {
    fixture.progressReads++;
    return { status: "ok", hostEpoch: request.expectedHostEpoch, steps: fixture.steps };
  },
  createSessionOutcome: async (request: WorkspaceCreateSessionOutcomeRequest): Promise<WorkspaceCreateSessionResponse> => {
    fixture.outcomeReads.push(request.requestId);
    return outcomes.get(request.requestId) ?? { status: "unknown", hostEpoch: request.expectedHostEpoch, scope: null, projectId: null, projectPath: null,
      providerId: null, sessionId: null, workspacePath: null, worktreePath: null, reason: null, message: null };
  },
};
export const modelCatalog = { ...baseModels, models: async (request: { expectedEpoch: string; providerId: string }) => {
  fixture.modelReads++;
  if (fixture.holdModels) await new Promise<void>(resolve => modelWaiters.push(resolve));
  if (fixture.failModels) throw new Error("synthetic model catalog failure");
  const value = await baseModels.models();
  return { ...value, epoch: request.expectedEpoch, providerId: request.providerId, models: models().map(model => ({ ...value.models[0], ...model })) };
} };
export const sessionOperations = { ...baseOperations, choices: async (request: SessionChoicesRequest) => {
  fixture.choiceReads.push(request.sessionId);
  if (fixture.choicesFail) throw new Error("synthetic choices failure");
  // A session is created in the permission mode its creation names, as the host saves it.
  const creation = fixture.creates.find((_, i) => request.sessionId === `created-${i + 1}`);
  const providerKey = creation?.providerId ?? "fixture";
  return { status: "ok", epoch: request.expectedEpoch, sessionId: request.sessionId,
    current: { providerKey, agentPromptId: "default", modelId: "old", reasoningEffort: null, permissionMode: creation?.permissionMode ?? null },
    prompts: [{ id: "default", name: "Default" }, { id: "plan", name: "Plan" }],
    models: models().map(model => ({ id: model.id, name: model.name, efforts: model.efforts, startEffort: model.defaultEffort, imageInput: false })),
    permissionModes: permissionModes(providerKey), defaultPermissionMode: defaultPermissionMode(providerKey) };
} };
export const projectGit = { status: async () => ({ status: "ok", projectId: "project", branch: "main", detached: false, changes: { insertions: 0, deletions: 0, files: 0 } }) };
export const worktrees = { ...baseWorktrees,
  branches: async () => ({ status: "ok", projectId: "project", busy: false, branches: [
    { name: "main", current: true, remote: false, worktree: null, worktreeName: null },
    { name: "feature", current: false, remote: false, worktree: null, worktreeName: null }] }),
  list: async () => ({ status: "ok", projectId: "project", worktrees: [], newFolder: "/Users/someone-with-a-long-name/.alta/worktrees/a-project-with-a-long-name" }),
};
