import type { WorkspaceProject, WorkspaceSession } from "#neoastra";
import { inCloud } from "./cloudSessions/cloudSessions";
import { commandDefinitions, commandKeys, isMacPlatform, resolveCommandKey } from "./commandRegistry";

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
  // A session that runs in the cloud has no conversation here yet: what acts on it (a fork, Remote Control, a move of
  // its folder) waits until it is continued here.
  const local = mutate && !inCloud(row);
  return { open, rename: mutate, fork: local, delete: mutate, archive: mutate, "remote-control": local, move: local && selectedProjectId !== null, space: mutate,
    "open-vs-code": mutate, "copy-resume-command": read, "run-in-terminal": mutate };
}

/**
 * Whether the row of a session can be dragged onto another project: a session of the exact scope of its project, on
 * an owned host that can change sessions now. Unlike the menu, it does not ask for the session to be the selected one.
 */
export function sessionMoveAccess(row: WorkspaceSession, project: WorkspaceProject | undefined, hostEpoch: string | null,
  owned: boolean, canMutate: boolean, busy: boolean, uncertain: boolean) {
  return !!project && !project.archived && row.scopeKind === "project" && row.projectId === project.id && row.workspacePath === project.path
    && owned && !!hostEpoch && canMutate && !busy && !uncertain && !inCloud(row);
}

export function isSessionContextKey(key: string, shiftKey: boolean, composing: boolean, editing: boolean) {
  return !composing && !editing && (key === "ContextMenu" || key === "F10" && shiftKey);
}

/** Whether a key pressed on the row of a session asks to delete it: Delete or Backspace (the delete key of a Mac) alone, pressed once. */
export function isSessionDeleteKey(event: Readonly<{ key: string; altKey: boolean; ctrlKey: boolean; metaKey: boolean; shiftKey: boolean; repeat: boolean }>, composing: boolean) {
  return (event.key === "Delete" || event.key === "Backspace") && !event.altKey && !event.ctrlKey && !event.metaKey && !event.shiftKey && !event.repeat && !composing;
}

/**
 * The action a key pressed on the row of a session asks for: F2 alone renames it, as it renames a project, a file or a
 * terminal; the keys of the **Fork Session** and **Archive Session** commands fork and archive it, the session of that
 * row rather than the selected one. A letter alone is no shortcut there: it is typed into the prompt (#322).
 * Pressed once, not while a text is being composed; Delete has its own key.
 */
export function sessionRowShortcut(event: Readonly<{ key: string; altKey: boolean; ctrlKey: boolean; metaKey: boolean; shiftKey: boolean; repeat: boolean }>,
  composing: boolean, mac = isMacPlatform()): "rename" | "fork" | "archive" | null {
  if (event.repeat || composing) return null;
  if (event.key === "F2") return event.altKey || event.ctrlKey || event.metaKey || event.shiftKey ? null : "rename";
  const command = resolveCommandKey(event, false, "none", mac).command;
  return command === "fork" ? "fork" : command === "archiveSession" ? "archive" : null;
}

/** The key that forks or archives a session, shown at the end of its line in the menu of a row: that of its command. */
export function sessionRowGesture(action: "fork" | "archive", mac = isMacPlatform()): string {
  const id = action === "fork" ? "fork" : "archiveSession";
  return commandKeys(commandDefinitions.find(command => command.id === id)!, mac)[0];
}

/**
 * Whether the keyboard is on the row of a session (its own button, in a list that marks its rows with
 * `data-session-keys`), where the keys of {@link sessionRowShortcut} act on the session of that row.
 */
export function isSessionRowKeyTarget(target: Element | null): boolean {
  const row = target?.parentElement;
  return !!row && row.hasAttribute("data-session-keys") && row.firstElementChild === target;
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
