import type { WorkspaceRenameSessionRequest, WorkspaceRenameSessionResponse, WorkspaceSnapshot } from "#neoastra";
import type { createMutationCapability } from "./sessionOperations";
import { sessionsForProject } from "./workspace";

export type RenameTarget = { scope: "global"; projectPath: string } | { scope: "project"; projectId: string; projectPath: string };

type Capability = ReturnType<typeof createMutationCapability>;
type Invoke = (request: WorkspaceRenameSessionRequest, options: { timeoutMilliseconds: number }) => Promise<WorkspaceRenameSessionResponse>;
type Result = { kind: "renamed"; target: RenameTarget; id: string; title: string }
  | { kind: "error"; code: string };

function valid(value: string, maximum: number): boolean {
  if (!value || value.length > maximum || value !== value.trim() || /[\u0000-\u001f\u007f-\u009f]/u.test(value)) return false;
  for (let i = 0; i < value.length; i++) {
    const code = value.charCodeAt(i);
    if (code < 0xd800 || code > 0xdfff) continue;
    if (code > 0xdbff || ++i === value.length || value.charCodeAt(i) < 0xdc00 || value.charCodeAt(i) > 0xdfff) return false;
  }
  return true;
}

export function createSessionRename(invoke: Invoke) {
  let active = false;
  return async function rename(epoch: string | undefined, target: RenameTarget, id: string, title: string,
    capability: Capability | undefined): Promise<Result> {
    if (!epoch || !capability?.canMutate()) return { kind: "error", code: "unconfigured" };
    if (!valid(id, 256) || !valid(title, 256) || !valid(target.projectPath, 4096)
      || target.scope !== "global" && target.scope !== "project" || target.scope === "project" &&
      (!valid(target.projectId, 256) || !valid(target.projectPath, 4096))) return { kind: "error", code: "invalid_scope" };
    if (active) return { kind: "error", code: "busy" };
    active = true;
    const frozen: RenameTarget = target.scope === "global" ? { scope: "global", projectPath: target.projectPath }
      : { scope: "project", projectId: target.projectId, projectPath: target.projectPath };
    const request: WorkspaceRenameSessionRequest = { expectedHostEpoch: epoch, scope: frozen.scope,
      projectId: frozen.scope === "project" ? frozen.projectId : null,
      projectPath: frozen.projectPath, sessionId: id, title };
    try {
      const response = await invoke(request, { timeoutMilliseconds: 10_000 });
      if (!response || !/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/u.test(response.hostEpoch ?? ""))
        return { kind: "error", code: "rename_unconfirmed" };
      capability.observe({ status: response.status, epoch: response.hostEpoch });
      if (!capability.canMutate() || response.hostEpoch !== epoch || response.status === "stale_epoch")
        return { kind: "error", code: "stale_epoch" };
      if (response.status === "invalid_scope") return { kind: "error", code: "invalid_scope" };
      if (response.scope !== request.scope || response.projectId !== request.projectId
        || response.projectPath !== request.projectPath || response.sessionId !== id)
        return { kind: "error", code: "rename_unconfirmed" };
      if (response.status !== "ok") return { kind: "error", code: response.status };
      if (response.title !== title) return { kind: "error", code: "rename_unconfirmed" };
      return { kind: "renamed", target: frozen, id, title };
    } catch { return { kind: "error", code: "rename_unconfirmed" }; }
    finally { active = false; }
  };
}

export function renamedSessionVisible(snapshot: WorkspaceSnapshot, result: Extract<Result, { kind: "renamed" }>): boolean {
  if (!snapshot.configured) return false;
  const target = result.target;
  const projectId = target.scope === "project" ? target.projectId : null;
  if (result.target.scope === "project" && !snapshot.projects.some(project =>
    project.id === projectId && project.path === target.projectPath && !project.archived)) return false;
  return sessionsForProject(snapshot, projectId).some(session => session.id === result.id && session.title === result.title
    && session.workspacePath === result.target.projectPath);
}

export function renameSelectionCurrent(result: Extract<Result, { kind: "renamed" }>, projectId: string | null,
  sessionId: string | null): boolean {
  return projectId === (result.target.scope === "project" ? result.target.projectId : null) && sessionId === result.id;
}

export function sessionRenameMessage(code: string): string {
  switch (code) {
    case "unconfigured": return "Renaming requires an owned host.";
    case "invalid_scope": return "Enter a nonempty title (up to 256 characters, no control characters).";
    case "scope_missing": case "session_missing": return "The session or its scope changed. Refresh before renaming.";
    case "stale_epoch": return "The host changed. Reload before renaming.";
    case "closed": return "The host is closing; the rename was not admitted.";
    case "busy": return "Another rename is in progress.";
    default: return "Rename may have completed. Refresh and inspect the title; do not retry automatically.";
  }
}
