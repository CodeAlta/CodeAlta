import type { WorkspaceRequest, WorkspaceSnapshot } from "#neoastra";

export type WorkspaceState =
  | { kind: "loading" }
  | { kind: "unconfigured" }
  | { kind: "ready"; snapshot: WorkspaceSnapshot }
  | { kind: "error"; message: string };

export async function loadWorkspace(
  invoke: (request: WorkspaceRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<WorkspaceSnapshot>,
  signal: AbortSignal,
  publish: (state: WorkspaceState) => void,
): Promise<void> {
  if (signal.aborted) return;
  publish({ kind: "loading" });
  try {
    const snapshot = await invoke({}, { signal, timeoutMilliseconds: 30_000 });
    if (signal.aborted) return;
    publish(snapshot.configured ? { kind: "ready", snapshot } : { kind: "unconfigured" });
  } catch {
    if (signal.aborted) return;
    publish({ kind: "error", message: "The catalog snapshot could not be loaded. Check access to the trusted copy and its cache, then close this window and relaunch. No alternate session scan was used." });
  }
}

export function sessionsForProject(snapshot: WorkspaceSnapshot, projectId: string | null) {
  if (projectId !== null) {
    const project = snapshot.projects.find(value => value.id === projectId);
    return project ? snapshot.sessions.filter(value => value.scopeKind === "project"
      ? value.projectId === projectId && value.workspacePath === project.path
      : value.scopeKind !== "global" && value.workspacePath === project.path) : [];
  }
  const knownPaths = new Set(snapshot.projects.map(value => value.path));
  const knownIds = new Set(snapshot.projects.map(value => value.id));
  return snapshot.sessions.filter(value => value.scopeKind === "global" || (value.scopeKind === "project"
    ? !knownIds.has(value.projectId ?? "") || !snapshot.projects.some(project => project.id === value.projectId
        && project.path === value.workspacePath) : !value.workspacePath || !knownPaths.has(value.workspacePath)));
}

/**
 * What the Explorer shows of a snapshot's sessions, without their activity times: two snapshots with the
 * same signature list the same sessions under the same parents and titles.
 */
export function sessionListSignature(snapshot: WorkspaceSnapshot): string {
  return JSON.stringify([snapshot.projects.map(project => [project.id, project.name, project.path, project.archived]),
    snapshot.sessions.map(session => [session.id, session.title, session.parentSessionId, session.scopeKind, session.projectId, session.workspacePath,
      session.worktreePath, session.worktreeMissing])
      .sort((a, b) => String(a[0]).localeCompare(String(b[0])))]);
}

export function workspaceNotice(snapshot: WorkspaceSnapshot): string | null {
  if (!snapshot.projectsTruncated && !snapshot.sessionsTruncated && !snapshot.displayTextTruncated) return null;
  return "Display limits applied (up to 200 projects / 500 sessions, with a wire-size limit); some labels may be shortened. This is not paging: the shared loader still reads the whole catalog. Sessions whose projects are omitted appear under Chats.";
}
