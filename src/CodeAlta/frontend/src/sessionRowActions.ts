import type { WorkspaceProject, WorkspaceSession } from "#neoastra";

export type SessionMenuTarget = { id: string; projectId: string | null; hostEpoch: string | null };
export type SessionAction = "open" | "rename" | "fork" | "delete" | "archive" | "remote-control" | "move" | "space"
  // A Claude Code session opened in VS Code or a terminal (claudeCodeOpen/claudeCodeOpen.ts).
  | "open-vs-code" | "copy-resume-command" | "run-in-terminal";

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
  // Remote Control acts on the session's provider: the same authority as a change of the session.
  // A fork leaves the session as it is, and is made while it works; it creates a session in the same scope.
  // Only the session of a project moves to another project; a chat and a session of a project move to another space.
  // Opening a Claude Code session in VS Code or a terminal closes the process CodeAlta keeps for it: a change of the
  // session too. Copying the command that resumes it asks the host to close that process as well, which it refuses
  // while the session works, and copies all the same: the session of an owned window is enough.
  const read = open && exactScope && owned && !!hostEpoch;
  return { open, rename: mutate, fork: mutate, delete: mutate, archive: mutate, "remote-control": mutate, move: mutate && selectedProjectId !== null, space: mutate,
    "open-vs-code": mutate, "copy-resume-command": read, "run-in-terminal": mutate };
}

/**
 * Whether the row of a session can be dragged onto another project: a session of the exact scope of its project, on
 * an owned host that can change sessions now. Unlike the menu, it does not ask for the session to be the selected one.
 */
export function sessionMoveAccess(row: WorkspaceSession, project: WorkspaceProject | undefined, hostEpoch: string | null,
  owned: boolean, canMutate: boolean, busy: boolean, uncertain: boolean) {
  return !!project && !project.archived && row.scopeKind === "project" && row.projectId === project.id && row.workspacePath === project.path
    && owned && !!hostEpoch && canMutate && !busy && !uncertain;
}

export function isSessionContextKey(key: string, shiftKey: boolean, composing: boolean, editing: boolean) {
  return !composing && !editing && (key === "ContextMenu" || key === "F10" && shiftKey);
}

/** Whether a key pressed on the row of a session asks to delete it: Delete alone, pressed once. */
export function isSessionDeleteKey(event: Readonly<{ key: string; altKey: boolean; ctrlKey: boolean; metaKey: boolean; shiftKey: boolean; repeat: boolean }>, composing: boolean) {
  return event.key === "Delete" && !event.altKey && !event.ctrlKey && !event.metaKey && !event.shiftKey && !event.repeat && !composing;
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
