import type { WorkspaceCreateSessionRequest, WorkspaceCreateSessionResponse, WorkspaceSnapshot } from "#neoastra";
import type { createMutationCapability } from "./sessionOperations";
import { sessionsForProject } from "./workspace";

type Capability = ReturnType<typeof createMutationCapability>;
export type SessionTarget = { scope: "global" } | { scope: "project"; projectId: string; projectPath: string };
type Result = { kind: "created"; target: SessionTarget; id: string; path: string } | { kind: "error"; code: string };
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
    capability: Capability | undefined): Promise<Result> {
    if (!epoch || !capability?.canMutate()) return { kind: "error", code: "unconfigured" };
    if (!["global", "project"].includes(target.scope)
      || target.scope === "project" && (!valid(target.projectId, 256)
      || !valid(target.projectPath, 4096) || !absolute(target.projectPath)) || title !== null && !valid(title, 256))
      return { kind: "error", code: "invalid_scope" };
    if (active) return { kind: "error", code: "busy" };
    active = true;
    const frozen: SessionTarget = target.scope === "global" ? { scope: "global" }
      : { scope: "project", projectId: target.projectId, projectPath: target.projectPath };
    const request: WorkspaceCreateSessionRequest = { expectedHostEpoch: epoch, scope: frozen.scope,
      projectId: frozen.scope === "project" ? frozen.projectId : null,
      projectPath: frozen.scope === "project" ? frozen.projectPath : null, title };
    try {
      const response = await invoke(request, { timeoutMilliseconds: 10_000 });
      if (!response || !valid(response.status, 64) || !/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/u.test(response.hostEpoch ?? ""))
        return { kind: "error", code: "create_unconfirmed" };
      capability.observe({ status: response.status, epoch: response.hostEpoch });
      if (!capability.canMutate() || response.hostEpoch !== epoch || response.status === "stale_epoch")
        return { kind: "error", code: "stale_epoch" };
      if (response.status === "invalid_scope") return { kind: "error", code: "invalid_scope" };
      if (response.scope !== request.scope || response.projectId !== request.projectId || response.projectPath !== request.projectPath)
        return { kind: "error", code: "create_unconfirmed" };
      if (response.status !== "ok") return { kind: "error", code: response.status };
      if (!valid(response.sessionId, 256) || !valid(response.workspacePath, 4096) || !absolute(response.workspacePath)
        || frozen.scope === "project" && response.workspacePath !== frozen.projectPath)
        return { kind: "error", code: "create_unconfirmed" };
      return { kind: "created", target: frozen, id: response.sessionId, path: response.workspacePath };
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
  if (!sessionsForProject(snapshot, projectId).some(session => session.id === result.id && session.workspacePath === result.path)) return undefined;
  return { projectId, sessionId: result.id };
}

export function sessionCreationMessage(code: string): string {
  switch (code) {
    case "unconfigured": return "Creating sessions requires an owned host; catalog-only browsing is read-only.";
    case "invalid_scope": return "The selected scope or title is invalid. Choose a catalog project or Global and try again.";
    case "project_missing": return "The project is no longer available or is archived. Refresh the catalog before creating a session.";
    case "provider_unavailable": return "No enabled provider is available for a new session.";
    case "stale_epoch": return "The host changed. Reload before creating a session.";
    case "busy": return "A session creation is already in progress.";
    case "closed": return "The host is closing; no new creation was admitted.";
    default: return "Creation may have completed. Refresh and inspect sessions before requesting another; there is no automatic retry.";
  }
}
