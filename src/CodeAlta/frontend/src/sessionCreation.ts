import type { WorkspaceCreateSessionRequest, WorkspaceCreateSessionResponse, WorkspaceSnapshot } from "#neoastra";
import type { createMutationCapability } from "./sessionOperations";
import { sessionsForProject } from "./workspace";

type Capability = ReturnType<typeof createMutationCapability>;
export type SessionTarget = { scope: "global" } | { scope: "project"; projectId: string; projectPath: string };
/** Where a new project session works: null and `worktree: false` are the folder of the project. */
export type SessionPlace = Readonly<{ worktree: boolean; base: string | null }> | null;
type Result = { kind: "created"; target: SessionTarget; id: string; path: string; providerId: string | null; worktreePath: string | null }
  | { kind: "error"; code: string; reason?: string | null; message?: string | null };
// Git checks a whole repository out for a worktree: that may take minutes, where a session alone takes a moment.
const worktreeTimeoutMilliseconds = 600_000;
type Invoke = (request: WorkspaceCreateSessionRequest, options: { timeoutMilliseconds: number }) => Promise<WorkspaceCreateSessionResponse>;

function valid(value: unknown, maximum: number): value is string {
  if (typeof value !== "string" || !value || value.length > maximum || value !== value.trim()
    || /[\u0000-\u001f\u007f-\u009f]/u.test(value)) return false;
  for (let i = 0; i < value.length; i++) {
    const ch = value.charCodeAt(i);
    if (ch < 0xd800 || ch > 0xdfff) continue;
    if (ch > 0xdbff || ++i === value.length || value.charCodeAt(i) < 0xdc00 || value.charCodeAt(i) > 0xdfff) return false;
  }
  return true;
}

function absolute(path: string): boolean {
  return path.startsWith("/") || /^[a-zA-Z]:[\\/]/u.test(path) || /^\\\\[^\\]+\\[^\\]+/u.test(path);
}

export function createSessionCreation(invoke: Invoke) {
  let active = false;
  return async function create(epoch: string | undefined, target: SessionTarget, title: string | null,
    capability: Capability | undefined, providerId: string | null = null, place: SessionPlace = null): Promise<Result> {
    if (!epoch || !capability?.canMutate()) return { kind: "error", code: "unconfigured" };
    const worktree = place?.worktree === true, base = worktree ? place.base : null;
    if (!["global", "project"].includes(target.scope)
      || target.scope === "project" && (!valid(target.projectId, 256)
      || !valid(target.projectPath, 4096) || !absolute(target.projectPath)) || title !== null && !valid(title, 256)
      || worktree && target.scope !== "project" || base !== null && !valid(base, 200))
      return { kind: "error", code: "invalid_scope" };
    if (active) return { kind: "error", code: "busy" };
    if (providerId !== null && !valid(providerId, 256)) return { kind: "error", code: "provider_unavailable" };
    active = true;
    const frozen: SessionTarget = target.scope === "global" ? { scope: "global" }
      : { scope: "project", projectId: target.projectId, projectPath: target.projectPath };
    const request: WorkspaceCreateSessionRequest = Object.freeze({ expectedHostEpoch: epoch, scope: frozen.scope,
      projectId: frozen.scope === "project" ? frozen.projectId : null,
      projectPath: frozen.scope === "project" ? frozen.projectPath : null, title, providerId, worktree, baseBranch: base });
    try {
      const response = await invoke(request, { timeoutMilliseconds: worktree ? worktreeTimeoutMilliseconds : 10_000 });
      if (!response || !valid(response.status, 64) || !/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/u.test(response.hostEpoch ?? ""))
        return { kind: "error", code: "create_unconfirmed" };
      capability.observe({ status: response.status, epoch: response.hostEpoch });
      if (!capability.canMutate() || response.hostEpoch !== epoch || response.status === "stale_epoch")
        return { kind: "error", code: "stale_epoch" };
      if (response.status === "invalid_scope") return { kind: "error", code: "invalid_scope" };
      if (response.scope !== request.scope || response.projectId !== request.projectId || response.projectPath !== request.projectPath
        || response.providerId !== request.providerId)
        return { kind: "error", code: "create_unconfirmed" };
      // No worktree, no session: git said why, and nothing is left to inspect.
      if (response.status === "worktree_failed") return { kind: "error", code: "worktree_failed", reason: response.reason ?? null, message: response.message ?? null };
      if (response.status !== "ok") return { kind: "error", code: response.status };
      const worktreePath = response.worktreePath ?? null;
      if (!valid(response.sessionId, 256) || !valid(response.workspacePath, 4096) || !absolute(response.workspacePath)
        || frozen.scope === "project" && response.workspacePath !== frozen.projectPath
        || worktree !== (worktreePath !== null) || worktreePath !== null && (!valid(worktreePath, 4096) || !absolute(worktreePath)))
        return { kind: "error", code: "create_unconfirmed" };
      return { kind: "created", target: frozen, id: response.sessionId, path: response.workspacePath, providerId, worktreePath };
    } catch { return { kind: "error", code: "create_unconfirmed" }; }
    finally { active = false; }
  };
}

export function createdSessionSelection(snapshot: WorkspaceSnapshot, result: Extract<Result, { kind: "created" }>) {
  if (!snapshot.configured) return undefined;
  const projectId = result.target.scope === "project" ? result.target.projectId : null;
  if (result.target.scope === "project") {
    const { projectId: id, projectPath: path } = result.target;
    if (!snapshot.projects.some(project => project.id === id && project.path === path && !project.archived)) return undefined;
  }
  const matches = sessionsForProject(snapshot, projectId).filter(session => session.id === result.id);
  if (matches.length !== 1 || matches[0].workspacePath !== result.path
    || result.providerId !== null && matches[0].providerKey !== result.providerId) return undefined;
  return { projectId, sessionId: result.id };
}

/** Why git created no worktree for a session, in the words of the window. */
export function worktreeCreationMessage(reason: string | null | undefined, message: string | null | undefined): string {
  switch (reason) {
    case "not_repository": return "The folder of the project is not in a git repository: a worktree needs one. Send again to work in the project folder.";
    case "no_commit": return "The repository has no commit yet: a worktree starts from one. Choose the project folder and send again.";
    case "invalid": return "The branch the worktree starts from is no longer there. Choose another one and send again.";
    case "git_unavailable": return "Git is not installed: no worktree was created. Send again to work in the project folder.";
    case "timeout": return "Git did not answer in time: no worktree was created.";
    default: return `Git could not create the worktree.${message?.trim() ? ` ${message.trim()}` : ""}`;
  }
}

export function sessionCreationMessage(code: string): string {
  switch (code) {
    case "unconfigured": return "Creating sessions requires an owned host; catalog-only browsing is read-only.";
    case "invalid_scope": return "The selected scope or title is invalid. Choose a catalog project or Global and try again.";
    case "project_missing": return "The project is no longer available or is archived. Refresh the catalog before creating a session.";
    case "provider_unavailable": return "The requested provider is unavailable, or no enabled provider exists. No alternative was selected.";
    case "stale_epoch": return "The host changed. Reload before creating a session.";
    case "busy": return "A session creation is already in progress.";
    case "closed": return "The host is closing; no new creation was admitted.";
    default: return "Creation may have completed. Refresh and inspect sessions before requesting another; there is no automatic retry.";
  }
}
