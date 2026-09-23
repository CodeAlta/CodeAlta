import type { WorkspaceDeleteSessionRequest, WorkspaceDeleteSessionResponse, WorkspaceSnapshot } from "#neoastra";
import type { createMutationCapability } from "./sessionOperations";
import type { RenameTarget } from "./sessionRename";
import { sessionsForProject } from "./workspace";

type Capability = ReturnType<typeof createMutationCapability>;
type Invoke = (request: WorkspaceDeleteSessionRequest, options: { timeoutMilliseconds: number }) => Promise<WorkspaceDeleteSessionResponse>;
export type DeletedTarget = { target: RenameTarget; id: string };
type Result = { kind: "deleted" } & DeletedTarget | { kind: "error"; code: string };

function valid(value: string, maximum: number): boolean {
  if (!value || value.length > maximum || value !== value.trim() || /[\u0000-\u001f\u007f-\u009f]/u.test(value)) return false;
  for (let i = 0; i < value.length; i++) {
    const code = value.charCodeAt(i);
    if (code < 0xd800 || code > 0xdfff) continue;
    if (code > 0xdbff || ++i === value.length || value.charCodeAt(i) < 0xdc00 || value.charCodeAt(i) > 0xdfff) return false;
  }
  return true;
}

export function createSessionDeletion(invoke: Invoke) {
  let active = false;
  return async function remove(epoch: string | undefined, target: RenameTarget, id: string, title: string,
    confirmation: string, capability: Capability | undefined): Promise<Result> {
    if (!epoch || !capability?.canMutate()) return { kind: "error", code: "unconfigured" };
    if (!valid(id, 256) || !valid(title, 256) || confirmation !== title || !valid(target.projectPath, 4096)
      || target.scope !== "global" && target.scope !== "project"
      || target.scope === "project" && !valid(target.projectId, 256)) return { kind: "error", code: "invalid_confirmation" };
    if (active) return { kind: "error", code: "busy" };
    active = true;
    const frozen: RenameTarget = target.scope === "global" ? { scope: "global", projectPath: target.projectPath }
      : { scope: "project", projectId: target.projectId, projectPath: target.projectPath };
    const request: WorkspaceDeleteSessionRequest = { expectedHostEpoch: epoch, scope: frozen.scope,
      projectId: frozen.scope === "project" ? frozen.projectId : null,
      projectPath: frozen.projectPath, sessionId: id, confirmedTitle: title };
    try {
      const response = await invoke(request, { timeoutMilliseconds: 10_000 });
      if (!response || !/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/u.test(response.hostEpoch ?? ""))
        return { kind: "error", code: "delete_unconfirmed" };
      capability.observe({ status: response.status, epoch: response.hostEpoch });
      if (!capability.canMutate() || response.hostEpoch !== epoch || response.status === "stale_epoch")
        return { kind: "error", code: "stale_epoch" };
      if (response.status === "invalid_scope") return { kind: "error", code: "invalid_confirmation" };
      if (response.scope !== request.scope || response.projectId !== request.projectId
        || response.projectPath !== request.projectPath || response.sessionId !== id)
        return { kind: "error", code: "delete_unconfirmed" };
      return response.status === "ok" ? { kind: "deleted", target: frozen, id }
        : { kind: "error", code: response.status };
    } catch { return { kind: "error", code: "delete_unconfirmed" }; }
    finally { active = false; }
  };
}

// Absence is not proof when the catalog response is truncated or the project scope changed.
export function deletedSessionRecovery(snapshot: WorkspaceSnapshot, result: DeletedTarget) {
  if (!snapshot.configured || snapshot.sessionsTruncated) return undefined;
  const projectId = result.target.scope === "project" ? result.target.projectId : null;
  if (result.target.scope === "project" && !snapshot.projects.some(project => project.id === projectId
    && project.path === result.target.projectPath && !project.archived)) return undefined;
  if (snapshot.sessions.some(session => session.id === result.id)) return undefined;
  const remaining = sessionsForProject(snapshot, projectId);
  return { projectId, sessionId: remaining[0]?.id ?? null };
}

export function deleteSelectionCurrent(result: DeletedTarget, projectId: string | null, sessionId: string | null) {
  return projectId === (result.target.scope === "project" ? result.target.projectId : null) && sessionId === result.id;
}

export function sessionDeletionMessage(code: string): string {
  switch (code) {
    case "unconfigured": return "Deleting sessions requires an owned host.";
    case "invalid_confirmation": return "Type the exact current session title to confirm deletion.";
    case "scope_missing": case "session_missing": return "The session, title or project changed. Refresh before deciding whether to delete.";
    case "session_in_use": return "This session has active runtime work. Wait for it to finish and refresh before deleting.";
    case "has_children": return "This session has child sessions. Exact-session deletion will not remove them.";
    case "stale_epoch": return "The host changed. Reload before deleting.";
    case "busy": return "A session operation is in progress. Nothing was deleted.";
    case "closed": return "The host is closing; deletion was not admitted.";
    default: return "Deletion may have completed. Refresh and inspect the catalog; no retry will be sent.";
  }
}
