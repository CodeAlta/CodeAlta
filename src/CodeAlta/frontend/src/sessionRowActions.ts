import type { WorkspaceProject, WorkspaceSession } from "#neoastra";

export type SessionMenuTarget = { id: string; projectId: string | null; hostEpoch: string | null };
export type SessionAction = "open" | "rename" | "delete";

export function sessionActionAccess(row: WorkspaceSession, target: SessionMenuTarget, selectedId: string | null,
  selectedProjectId: string | null, project: WorkspaceProject | undefined, hostEpoch: string | null,
  owned: boolean, canMutate: boolean, busy: boolean, uncertain: boolean) {
  const open = row.id === target.id && selectedId === target.id && target.projectId === selectedProjectId
    && target.hostEpoch === hostEpoch;
  // Unknown, cross-project or omitted-project headers never authorize a menu mutation.
  const exactScope = selectedProjectId === null
    ? row.scopeKind === "global" && row.projectId === null && !!row.workspacePath
    : row.scopeKind === "project" && row.projectId === selectedProjectId && !!project && !project.archived
      && project.id === selectedProjectId && row.workspacePath === project.path;
  const mutate = open && exactScope && owned && !!hostEpoch && canMutate && !busy && !uncertain;
  return { open, rename: mutate, delete: mutate };
}

export function isSessionContextKey(key: string, shiftKey: boolean, composing: boolean, editing: boolean) {
  return !composing && !editing && (key === "ContextMenu" || key === "F10" && shiftKey);
}

export function menuFocusIndex(key: string, current: number, count: number): number | null {
  if (count === 0) return null;
  if (key === "Home") return 0;
  if (key === "End") return count - 1;
  if (current < 0 && key === "ArrowUp") return count - 1;
  if (current < 0 && key === "ArrowDown") return 0;
  if (key === "ArrowDown") return (current + 1 + count) % count;
  if (key === "ArrowUp") return (current - 1 + count) % count;
  return null;
}

export function restoreSessionMenuFocus(origin: { isConnected: boolean; focus: () => void } | null) {
  if (origin?.isConnected) origin.focus();
}
