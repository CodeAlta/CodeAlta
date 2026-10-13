import type { WorkspaceMovedSession, WorkspaceMoveSessionRequest, WorkspaceMoveSessionResponse, WorkspaceSnapshot } from "#neoastra";
import type { MessageKey } from "./localization";
import type { createMutationCapability } from "./sessionOperations";
import type { SessionTab, SessionTabs } from "./sessionTabs";

/** A project a session belongs to, or goes to: its identity and its folder. */
export type MoveScope = Readonly<{ projectId: string; projectPath: string }>;

type Capability = ReturnType<typeof createMutationCapability>;
type Invoke = (request: WorkspaceMoveSessionRequest, options: { timeoutMilliseconds: number }) => Promise<WorkspaceMoveSessionResponse>;
export type SessionMoveResult = { kind: "moved"; id: string; target: MoveScope; moved: readonly WorkspaceMovedSession[]; warnings: readonly string[] }
  | { kind: "error"; code: string; moved: readonly WorkspaceMovedSession[] };

function valid(value: string, maximum: number): boolean {
  if (!value || value.length > maximum || value !== value.trim() || /[\u0000-\u001f\u007f-\u009f]/u.test(value)) return false;
  for (let i = 0; i < value.length; i++) {
    const code = value.charCodeAt(i);
    if (code < 0xd800 || code > 0xdfff) continue;
    if (code > 0xdbff || ++i === value.length || value.charCodeAt(i) < 0xdc00 || value.charCodeAt(i) > 0xdfff) return false;
  }
  return true;
}

// The sessions the host says it moved: each into the target, with a worktree path that is a path or none.
function movedSessions(response: WorkspaceMoveSessionResponse, target: MoveScope): readonly WorkspaceMovedSession[] | null {
  if (!Array.isArray(response.moved) || response.moved.length > 1000) return null;
  return response.moved.every(moved => moved && valid(moved.sessionId, 256) && moved.projectId === target.projectId
    && moved.projectPath === target.projectPath && (moved.worktreePath === null || valid(moved.worktreePath, 4096)))
    ? response.moved.map(moved => ({ sessionId: moved.sessionId, projectId: moved.projectId, projectPath: moved.projectPath, worktreePath: moved.worktreePath }))
    : null;
}

/** Moves a session, with the sessions it started in its project, to another project. One move at a time. */
export function createSessionMove(invoke: Invoke) {
  let active = false;
  return async function move(epoch: string | undefined, from: MoveScope, target: MoveScope, id: string,
    capability: Capability | undefined): Promise<SessionMoveResult> {
    if (!epoch || !capability?.canMutate()) return { kind: "error", code: "unconfigured", moved: [] };
    if (!valid(id, 256) || !valid(from.projectId, 256) || !valid(from.projectPath, 4096)
      || !valid(target.projectId, 256) || !valid(target.projectPath, 4096)) return { kind: "error", code: "invalid_scope", moved: [] };
    if (from.projectId === target.projectId) return { kind: "error", code: "same_project", moved: [] };
    if (active) return { kind: "error", code: "busy", moved: [] };
    active = true;
    const frozen: MoveScope = { projectId: target.projectId, projectPath: target.projectPath };
    const request: WorkspaceMoveSessionRequest = { expectedHostEpoch: epoch, sessionId: id, projectId: from.projectId,
      projectPath: from.projectPath, targetProjectId: frozen.projectId, targetProjectPath: frozen.projectPath };
    try {
      const response = await invoke(request, { timeoutMilliseconds: 60_000 });
      if (!response || !/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/u.test(response.hostEpoch ?? ""))
        return { kind: "error", code: "move_unconfirmed", moved: [] };
      capability.observe({ status: response.status, epoch: response.hostEpoch });
      if (!capability.canMutate() || response.hostEpoch !== epoch || response.status === "stale_epoch")
        return { kind: "error", code: "stale_epoch", moved: [] };
      if (response.status === "invalid_scope") return { kind: "error", code: "invalid_scope", moved: [] };
      const moved = movedSessions(response, frozen);
      if (response.sessionId !== id || response.projectId !== request.projectId || response.projectPath !== request.projectPath
        || response.targetProjectId !== frozen.projectId || response.targetProjectPath !== frozen.projectPath || !moved)
        return { kind: "error", code: "move_unconfirmed", moved: [] };
      if (response.status !== "ok") return { kind: "error", code: response.status, moved };
      if (!moved.some(value => value.sessionId === id)) return { kind: "error", code: "move_unconfirmed", moved };
      const warnings = Array.isArray(response.warnings) ? response.warnings.filter(value => typeof value === "string").slice(0, 8) : [];
      return { kind: "moved", id, target: frozen, moved, warnings };
    } catch { return { kind: "error", code: "move_unconfirmed", moved: [] }; }
    finally { active = false; }
  };
}

const shownMoved = (snapshot: WorkspaceSnapshot, value: WorkspaceMovedSession) => snapshot.sessions.some(session =>
  session.id === value.sessionId && session.scopeKind === "project" && session.projectId === value.projectId && session.workspacePath === value.projectPath);

/** Whether a fresh snapshot lists every moved session in the project it went to. */
export function movedSessionsVisible(snapshot: WorkspaceSnapshot, moved: readonly WorkspaceMovedSession[]): boolean {
  return snapshot.configured && moved.every(value => shownMoved(snapshot, value));
}

/**
 * The moves whose tabs are still to follow them, split by a list about to be published: those it shows in the
 * project they went to are retargeted with it; the others wait for a list that shows them. Retargeted earlier, the
 * reconciliation with the list shown then would drop their tabs.
 */
export function splitPendingTabMoves(snapshot: WorkspaceSnapshot, pending: readonly WorkspaceMovedSession[]):
  { shown: readonly WorkspaceMovedSession[]; waiting: readonly WorkspaceMovedSession[] } {
  if (!snapshot.configured) return { shown: [], waiting: pending };
  const shown = pending.filter(value => shownMoved(snapshot, value));
  return { shown, waiting: shown.length === 0 ? pending : pending.filter(value => !shown.includes(value)) };
}

/**
 * The tabs of moved sessions, pointed at the project each went to: a tab keeps its place, and the active one stays
 * active. Without it, the tab of a session whose project changed is dropped by the next reconciliation.
 */
export function retargetSessionTabs(state: SessionTabs, moved: readonly WorkspaceMovedSession[]): SessionTabs {
  if (moved.length === 0) return state;
  const targets = new Map(moved.map(value => [value.sessionId, value]));
  let changed = false;
  const retarget = (tab: SessionTab): SessionTab => {
    const target = targets.get(tab.sessionId);
    if (!target || tab.projectId === target.projectId && tab.path === target.projectPath) return tab;
    changed = true;
    return { sessionId: tab.sessionId, projectId: target.projectId, path: target.projectPath };
  };
  const open = state.open.map(retarget);
  const closed = state.closed.map(retarget);
  const active = state.active ? retarget(state.active) : null;
  // The transient tab stays the transient one, and the prompts of new sessions keep their tabs.
  return changed ? { ...state, open, active, closed, ...(state.preview ? { preview: retarget(state.preview) } : {}) } : state;
}

/** What the window says when a move did not happen as asked. */
export function sessionMoveMessage(code: string): MessageKey {
  switch (code) {
    case "unconfigured": return "Moving a session requires an owned host.";
    case "invalid_scope": case "scope_missing": case "session_missing": return "The session or its project changed. Refresh before moving it.";
    case "target_missing": return "The project to move the session to is gone or archived. Refresh and choose another one.";
    case "same_project": return "The session already belongs to this project.";
    case "stale_epoch": return "The host changed. Reload before moving the session.";
    case "closed": return "The host is closing; the session was not moved.";
    case "busy": return "Another change of sessions is in progress. Try again when it ends.";
    case "session_in_use": return "The session, or a session it started, is running or has background tasks. Move it once it is idle.";
    case "interaction_pending": return "The session, or a session it started, waits for an answer. Answer it before moving the session.";
    case "requires_recovery": return "The session has unfinished or queued work. Let it finish before moving the session.";
    case "move_partial": return "Some sessions it started moved, but not the session itself. Check both projects before trying again.";
    default: return "The move may have completed. Refresh and inspect the session; no retry will be sent.";
  }
}
