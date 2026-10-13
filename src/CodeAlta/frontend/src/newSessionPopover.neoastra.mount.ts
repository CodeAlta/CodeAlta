// The real shell, with synthetic creation/catalog services only. No native host or user data is touched.
import type { SessionChoicesRequest, WorkspaceCreateSessionRequest, WorkspaceCreateSessionResponse, WorkspaceSnapshot, WorkspaceSession } from "#neoastra";
import { workspace as baseWorkspace, sessionOperations as baseOperations, modelCatalog as baseModels } from "./settingsShell.neoastra.mount";
import { worktrees as baseWorktrees } from "./demo-api";
export * from "./projectFocus.neoastra.mount";

const created: WorkspaceSession[] = [];
const modelWaiters: (() => void)[] = [];
// What the test sets before a creation: it is held, refused, answered for another provider, or its choices cannot be read.
const fixture = { creates: [] as WorkspaceCreateSessionRequest[], hold: false, refused: false, uncertain: false, choicesFail: false,
  missingModel: localStorage.getItem("newSessionFixtureMissingModel") === "true",
  failModels: localStorage.getItem("newSessionFixtureFailModels") === "true",
  holdModels: localStorage.getItem("newSessionFixtureHoldModels") === "true", modelReads: 0,
  releaseModels: () => { for (const resolve of modelWaiters.splice(0)) resolve(); },
  release: () => {}, choiceReads: [] as string[] };
Object.assign(window, { newSessionFixture: fixture });
// A model with several efforts, so that the reasoning has a slider, and one without any.
const models = () => [{ id: "new", name: "New model", efforts: ["Low", "Medium", "High"], defaultEffort: "Medium" },
  { id: "old", name: "Old model", efforts: [] as string[], defaultEffort: null }].filter(model => !fixture.missingModel || model.id !== "new");
export const workspace = {
  ...baseWorkspace,
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
    return { status: fixture.refused ? "worktree_failed" : "ok", hostEpoch: request.expectedHostEpoch, scope: request.scope,
      projectId: request.projectId, projectPath: request.projectPath, providerId: fixture.uncertain ? "unconfirmed" : request.providerId, sessionId: fixture.refused ? null : id,
      workspacePath, worktreePath, reason: fixture.refused ? "invalid" : null, message: null };
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
  return { status: "ok", epoch: request.expectedEpoch, sessionId: request.sessionId,
    current: { providerKey: fixture.creates.find((_, i) => request.sessionId === `created-${i + 1}`)?.providerId ?? "fixture", agentPromptId: "default", modelId: "old", reasoningEffort: null },
    prompts: [{ id: "default", name: "Default" }, { id: "plan", name: "Plan" }],
    models: models().map(model => ({ id: model.id, name: model.name, efforts: model.efforts, startEffort: model.defaultEffort, imageInput: false })) };
} };
export const projectGit = { status: async () => ({ status: "ok", projectId: "project", branch: "main", detached: false, changes: { insertions: 0, deletions: 0, files: 0 } }) };
export const worktrees = { ...baseWorktrees,
  branches: async () => ({ status: "ok", projectId: "project", busy: false, branches: [
    { name: "main", current: true, remote: false, worktree: null, worktreeName: null },
    { name: "feature", current: false, remote: false, worktree: null, worktreeName: null }] }),
  list: async () => ({ status: "ok", projectId: "project", worktrees: [], newFolder: "/fixture/worktrees" }),
};
