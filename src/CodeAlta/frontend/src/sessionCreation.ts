import type { SessionOptionsChoice, WorkspaceCreateSessionOutcomeRequest, WorkspaceCreateSessionRequest, WorkspaceCreateSessionResponse, WorkspaceSnapshot } from "#neoastra";
import type { createMutationCapability } from "./sessionOperations";
import { sessionsForProject } from "./workspace";

type Capability = ReturnType<typeof createMutationCapability>;
export type SessionTarget = { scope: "global" } | { scope: "project"; projectId: string; projectPath: string };
/** Where a new project session works: null and `worktree: false` are the folder of the project. */
export type SessionPlace = Readonly<{ worktree: boolean; base: string | null }> | null;
/** `warning` is what git said when it could not check the submodules of the new worktree out; the session works there all the same. */
type Result = { kind: "created"; target: SessionTarget; id: string; path: string; providerId: string | null; worktreePath: string | null; warning: string | null }
  | { kind: "error"; code: string; reason?: string | null; message?: string | null };
// The page waits as long as the host gives a creation: git checks a whole repository out for a worktree, and a
// provider may be slow to start. Nothing of the window waits for it.
const creationTimeoutMilliseconds = 600_000;
type Invoke = (request: WorkspaceCreateSessionRequest, options: { timeoutMilliseconds: number }) => Promise<WorkspaceCreateSessionResponse>;
/** Asks the host again for the outcome of a creation whose answer was lost, by the identifier of its request. */
type Recover = (request: WorkspaceCreateSessionOutcomeRequest, options: { timeoutMilliseconds: number }) => Promise<WorkspaceCreateSessionResponse>;

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

/** Whether a text is the identifier the page gives a request: a GUID in its usual form. */
export function validRequestId(value: unknown): value is string {
  return typeof value === "string" && /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/u.test(value);
}

/** Whether a text can be the identifier of a space, as the catalog writes them. */
export function validSpaceId(value: unknown): value is string {
  return typeof value === "string" && /^[a-z0-9][a-z0-9._-]{1,63}$/u.test(value);
}

/**
 * Creates sessions one at a time. Each request has an identifier: when its answer is lost (the wait ended, the call
 * failed), `recover` asks the host for the outcome of that very request, which never creates a second session.
 */
export function createSessionCreation(invoke: Invoke, recover?: Recover, newRequestId: () => string = () => crypto.randomUUID()) {
  let active = false;
  /**
   * `spaceId` is the space the window shows: the session belongs to it for its whole life. `requestId` names the
   * request, by which its steps are read while it runs. `permissionMode` is the mode the session starts in, one its
   * provider offers; null for the one its provider is configured with. `preparedWorktreeId` names the worktree made
   * for the session while its prompt was typed; the host makes another one when it cannot take that one. `options` are
   * the options the session is created with (sub-sessions, size of context); null for the defaults.
   */
  return async function create(epoch: string | undefined, target: SessionTarget, title: string | null,
    capability: Capability | undefined, providerId: string | null = null, place: SessionPlace = null, spaceId: string | null = null,
    requestId: string = newRequestId(), permissionMode: string | null = null, preparedWorktreeId: string | null = null,
    options: SessionOptionsChoice | null = null): Promise<Result> {
    if (!epoch || !capability?.canMutate()) return { kind: "error", code: "unconfigured" };
    const worktree = place?.worktree === true, base = worktree ? place.base : null;
    if (!["global", "project"].includes(target.scope)
      || target.scope === "project" && (!valid(target.projectId, 256)
      || !valid(target.projectPath, 4096) || !absolute(target.projectPath)) || title !== null && !valid(title, 256)
      || worktree && target.scope !== "project" || base !== null && !valid(base, 200) || spaceId !== null && !validSpaceId(spaceId)
      || permissionMode !== null && !valid(permissionMode, 64) || preparedWorktreeId !== null && (!worktree || !validRequestId(preparedWorktreeId)))
      return { kind: "error", code: "invalid_scope" };
    if (active) return { kind: "error", code: "busy" };
    if (providerId !== null && !valid(providerId, 256)) return { kind: "error", code: "provider_unavailable" };
    active = true;
    const frozen: SessionTarget = target.scope === "global" ? { scope: "global" }
      : { scope: "project", projectId: target.projectId, projectPath: target.projectPath };
    const request: WorkspaceCreateSessionRequest = Object.freeze({ expectedHostEpoch: epoch, scope: frozen.scope,
      projectId: frozen.scope === "project" ? frozen.projectId : null,
      projectPath: frozen.scope === "project" ? frozen.projectPath : null, title, providerId, worktree, baseBranch: base, spaceId, requestId, permissionMode,
      preparedWorktreeId, options: options ? { subSessions: options.subSessions, ...(options.contextSize ? { contextSize: options.contextSize } : {}) } : null });
    try {
      let response: WorkspaceCreateSessionResponse;
      try { response = await invoke(request, { timeoutMilliseconds: creationTimeoutMilliseconds }); }
      catch (failure) {
        if (!recover) throw failure;
        response = await recover(Object.freeze({ expectedHostEpoch: epoch, requestId }), { timeoutMilliseconds: creationTimeoutMilliseconds });
        // The host admitted no creation with this identifier: none was made.
        if (response?.status === "unknown" && response.hostEpoch === epoch) return { kind: "error", code: "not_created" };
      }
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
      const warning = worktreePath !== null && typeof response.warning === "string" && response.warning.trim() ? response.warning.trim() : null;
      return { kind: "created", target: frozen, id: response.sessionId, path: response.workspacePath, providerId, worktreePath, warning };
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
    case "space_missing": return "The space this window shows no longer exists. Show another space and create the session there.";
    case "provider_outside_space": return "This provider is not available in the space this window shows. Choose another provider and create the session again.";
    case "invalid_permission_mode": return "The provider does not offer the permission mode chosen: no session was created. Choose another mode and create the session again.";
    case "not_created": return "The host received no creation: no session was created. Create it again.";
    default: return "Creation may have completed. Refresh and inspect sessions before requesting another; there is no automatic retry.";
  }
}
