// What the window asks of the host for a fork or a rewind: the turns of a session, a fork, a rewind. One change
// at a time, sent once: an answer that does not come, or does not match what was asked, leaves the outcome
// unknown and is never asked again by itself.
import type { WorkspaceForkSessionRequest, WorkspaceForkSessionResponse, WorkspaceRewindSessionRequest, WorkspaceRewindSessionResponse,
  WorkspaceSession, WorkspaceSessionTurnsRequest, WorkspaceSessionTurnsResponse, WorkspaceSnapshot } from "#neoastra";
import type { createMutationCapability } from "../sessionOperations";
import type { BranchMode, BranchTurn } from "./branchPoints";

export type BranchTarget = { scope: "global"; projectPath: string } | { scope: "project"; projectId: string; projectPath: string };

type Capability = ReturnType<typeof createMutationCapability>;
type Options = { timeoutMilliseconds: number; signal?: AbortSignal };
type ReadTurns = (request: WorkspaceSessionTurnsRequest, options: Options) => Promise<WorkspaceSessionTurnsResponse>;
type InvokeFork = (request: WorkspaceForkSessionRequest, options: Options) => Promise<WorkspaceForkSessionResponse>;
type InvokeRewind = (request: WorkspaceRewindSessionRequest, options: Options) => Promise<WorkspaceRewindSessionResponse>;

export type TurnsPage = Readonly<{ kind: "ok"; turns: readonly BranchTurn[]; total: number; activeRunId: string | null }>;
export type TurnsRead = TurnsPage | Readonly<{ kind: "error"; code: string }>;

export type ForkResult = Readonly<{ kind: "forked"; target: BranchTarget; sourceId: string; id: string; title: string; turnCount: number;
  prompt: string | null; promptTruncated: boolean }> | Readonly<{ kind: "error"; code: string }>;
export type RewindResult = Readonly<{ kind: "rewound"; target: BranchTarget; id: string; turnCount: number; removedTurnCount: number;
  prompt: string | null; promptTruncated: boolean; removedTurnsSessionId: string | null; removedTurnsTitle: string | null; rewindCount: number }>
  | Readonly<{ kind: "error"; code: string }>;

const epochForm = /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/u;

function valid(value: unknown, maximum: number): value is string {
  return typeof value === "string" && value.length > 0 && value.length <= maximum && value === value.trim() && !/[\u0000-\u001f\u007f-\u009f]/u.test(value);
}

function validTarget(target: BranchTarget): boolean {
  return valid(target.projectPath, 4096) && (target.scope === "global" || target.scope === "project" && valid(target.projectId, 256));
}

/**
 * The scope a session is changed in, as the host checks it: the chats, or the project the pane shows the
 * session in, when the catalog still has that project and the session works in its folder.
 */
export function branchTarget(snapshot: WorkspaceSnapshot | undefined, session: Pick<WorkspaceSession, "scopeKind" | "projectId" | "workspacePath">,
  projectId: string | null): BranchTarget | null {
  if (!snapshot || !session.workspacePath) return null;
  if (projectId === null) return session.scopeKind === "global" && session.projectId === null ? { scope: "global", projectPath: session.workspacePath } : null;
  const project = snapshot.projects.find(value => value.id === projectId);
  return project && !project.archived && session.scopeKind === "project" && session.projectId === projectId && session.workspacePath === project.path
    ? { scope: "project", projectId, projectPath: project.path } : null;
}

/** Reads one page of the turns of a session: the last ones, or the ones before `beforeTurn`. */
export async function readSessionTurns(read: ReadTurns, epoch: string | undefined, sessionId: string, beforeTurn: number | null,
  signal: AbortSignal): Promise<TurnsRead> {
  if (!epoch) return { kind: "error", code: "unconfigured" };
  try {
    const reply = await read({ expectedHostEpoch: epoch, sessionId, beforeTurn }, { timeoutMilliseconds: 20_000, signal });
    if (!reply || typeof reply.status !== "string") return { kind: "error", code: "read_failed" };
    if (reply.status !== "ok") return { kind: "error", code: reply.status };
    if (reply.hostEpoch !== epoch) return { kind: "error", code: "stale_epoch" };
    if (reply.sessionId !== sessionId || !Array.isArray(reply.turns) || !Number.isInteger(reply.total) || reply.total < reply.turns.length)
      return { kind: "error", code: "read_failed" };
    return { kind: "ok", turns: reply.turns, total: reply.total, activeRunId: reply.activeRunId ?? null };
  } catch { return { kind: "error", code: "read_failed" }; }
}

/**
 * Sends the fork and the rewind of sessions. The host makes one such change at a time, and so does this owner:
 * a second one asked while the first is under way is refused here as `busy`.
 */
export function createSessionBranch(invokeFork: InvokeFork, invokeRewind: InvokeRewind) {
  let active = false;
  const frozen = (target: BranchTarget): BranchTarget => target.scope === "global" ? { scope: "global", projectPath: target.projectPath }
    : { scope: "project", projectId: target.projectId, projectPath: target.projectPath };
  // What is wrong before the host is asked, or null when the change can be sent.
  function refusal(epoch: string | undefined, target: BranchTarget, sessionId: string, capability: Capability | undefined): string | null {
    if (!epoch || !capability?.canMutate()) return "unconfigured";
    if (!valid(sessionId, 256) || !validTarget(target)) return "invalid_scope";
    return active ? "busy" : null;
  }
  // What the answer says of the request it answers: an error code, or null when it is the answer to that request.
  function unconfirmed(response: { status: string; hostEpoch: string | null; scope: string | null; projectId: string | null; projectPath: string | null;
    sessionId: string | null } | null | undefined, request: { expectedHostEpoch: string; scope: string; projectId: string | null; projectPath: string | null;
    sessionId: string }, capability: Capability, unknown: string): string | null {
    if (!response || typeof response.status !== "string" || !epochForm.test(response.hostEpoch ?? "")) return unknown;
    capability.observe({ status: response.status, epoch: response.hostEpoch });
    if (!capability.canMutate() || response.hostEpoch !== request.expectedHostEpoch || response.status === "stale_epoch") return "stale_epoch";
    // A refusal made before the request was read names no scope.
    if (["invalid_scope", "unconfigured", "closed", "busy"].includes(response.status)) return response.status;
    if (response.scope !== request.scope || response.projectId !== request.projectId || response.projectPath !== request.projectPath
      || response.sessionId !== request.sessionId) return unknown;
    return response.status === "ok" ? null : response.status;
  }
  return {
    /** Whether a fork or a rewind is being made. */
    busy: () => active,
    /** Forks a session before the turn of `beforeRunId`, or at its end when it is null. */
    async fork(epoch: string | undefined, target: BranchTarget, sessionId: string, beforeRunId: string | null,
      capability: Capability | undefined): Promise<ForkResult> {
      const refused = refusal(epoch, target, sessionId, capability);
      if (refused) return { kind: "error", code: refused };
      if (beforeRunId !== null && !valid(beforeRunId, 256)) return { kind: "error", code: "invalid_scope" };
      active = true;
      const kept = frozen(target);
      const request: WorkspaceForkSessionRequest = { expectedHostEpoch: epoch!, scope: kept.scope, projectId: kept.scope === "project" ? kept.projectId : null,
        projectPath: kept.projectPath, sessionId, beforeRunId, title: null };
      try {
        const response = await invokeFork(request, { timeoutMilliseconds: 120_000 });
        const code = unconfirmed(response, request, capability!, "fork_unconfirmed");
        if (code) return { kind: "error", code };
        if (!valid(response.forkSessionId, 256) || response.forkSessionId === sessionId || !Number.isInteger(response.turnCount) || response.turnCount < 0)
          return { kind: "error", code: "fork_unconfirmed" };
        return { kind: "forked", target: kept, sourceId: sessionId, id: response.forkSessionId, title: response.title ?? "", turnCount: response.turnCount,
          prompt: response.prompt || null, promptTruncated: response.promptTruncated === true };
      } catch { return { kind: "error", code: "fork_unconfirmed" }; }
      finally { active = false; }
    },
    /** Rewinds a session to before the turn of `beforeRunId`; `keepRemovedTurns` keeps the session as it was in a session of its own. */
    async rewind(epoch: string | undefined, target: BranchTarget, sessionId: string, beforeRunId: string, keepRemovedTurns: boolean,
      capability: Capability | undefined): Promise<RewindResult> {
      const refused = refusal(epoch, target, sessionId, capability);
      if (refused) return { kind: "error", code: refused };
      if (!valid(beforeRunId, 256)) return { kind: "error", code: "invalid_scope" };
      active = true;
      const kept = frozen(target);
      const request: WorkspaceRewindSessionRequest = { expectedHostEpoch: epoch!, scope: kept.scope, projectId: kept.scope === "project" ? kept.projectId : null,
        projectPath: kept.projectPath, sessionId, beforeRunId, keepRemovedTurns };
      try {
        const response = await invokeRewind(request, { timeoutMilliseconds: 120_000 });
        const code = unconfirmed(response, request, capability!, "rewind_unconfirmed");
        if (code) return { kind: "error", code };
        if (!Number.isInteger(response.turnCount) || response.turnCount < 0 || !Number.isInteger(response.removedTurnCount) || response.removedTurnCount < 1
          || response.removedTurnsSessionId === sessionId) return { kind: "error", code: "rewind_unconfirmed" };
        const keptId = keepRemovedTurns && valid(response.removedTurnsSessionId, 256) ? response.removedTurnsSessionId : null;
        return { kind: "rewound", target: kept, id: sessionId, turnCount: response.turnCount, removedTurnCount: response.removedTurnCount,
          prompt: response.prompt || null, promptTruncated: response.promptTruncated === true, removedTurnsSessionId: keptId,
          removedTurnsTitle: keptId ? response.removedTurnsTitle ?? null : null, rewindCount: response.rewindCount };
      } catch { return { kind: "error", code: "rewind_unconfirmed" }; }
      finally { active = false; }
    },
  };
}

export type SessionBranch = ReturnType<typeof createSessionBranch>;

/** Why the turns of a session could not be listed. */
export function turnsMessage(code: string) {
  switch (code) {
    case "unconfigured": return "Listing the turns of a session requires an owned host." as const;
    case "stale_epoch": return "The host changed. Close this window and open it again." as const;
    case "session_missing": return "The turns of this session cannot be listed: the session is gone, or its history is in a format without turns." as const;
    default: return "The turns of this session could not be read." as const;
  }
}

/** Why a fork or a rewind was not made, or that its outcome is not known. */
export function branchMessage(code: string, mode: BranchMode) {
  switch (code) {
    case "busy": return "Another change of a session is under way. Try again when it has ended." as const;
    case "closed": return "CodeAlta is closing: nothing was changed." as const;
    case "stale_epoch": return "The host changed. Close this window and open it again." as const;
    case "invalid_scope": case "scope_missing": return "The project of this session changed or is no longer available. Refresh the sessions and try again." as const;
    case "unconfigured": return mode === "fork" ? "Forking a session requires an owned host." as const : "Rewinding a session requires an owned host." as const;
    case "session_missing": return "This session no longer exists." as const;
    case "session_archived": return "This session is archived: it cannot be forked or rewound." as const;
    case "turn_not_found": return "This turn is no longer in the session. Choose another one." as const;
    case "turn_running": return "This turn is still running: a session can be rewound once it has ended." as const;
    case "session_in_use": return "The session is working or being changed. Wait until it is idle, then try again." as const;
    case "interaction_pending": return "A question or a permission request of this session waits for an answer. Answer it first." as const;
    case "queue_pending": return "A prompt is queued in this session. Send or remove it first." as const;
    case "background_work": return "A command or a task of this session still runs in the background. Wait for it or stop it first." as const;
    case "session_changed": return "The session changed while this window was open. The turns were read again: check the turn and try again." as const;
    case "unsupported_format": return "The history of this session is in a format that cannot be forked or rewound." as const;
    case "io_failed": return "The session files could not be written. Nothing was changed." as const;
    default: return mode === "fork"
      ? "The fork may have been created. Refresh the sessions and look for it; it is not created again automatically." as const
      : "The rewind may have been made. Refresh the sessions and check this one; it is not made again automatically." as const;
  }
}

/** Whether a refusal says the turns that are listed are no longer the turns of the session. */
export const turnsOutdated = (code: string): boolean => code === "session_changed" || code === "turn_not_found";

/** Whether the outcome of a change is not known: nothing more is sent until the user has looked. */
export const outcomeUnknown = (code: string): boolean => code === "fork_unconfirmed" || code === "rewind_unconfirmed";
