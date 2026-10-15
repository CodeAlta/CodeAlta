// Archives a session through the host, takes it out of the archive, and says what came of it.
import type { WorkspaceArchiveSessionRequest, WorkspaceArchiveSessionResponse, WorkspaceSession } from "#neoastra";
import type { MessageKey } from "../localization";

type Invoke = (request: WorkspaceArchiveSessionRequest, options: { timeoutMilliseconds: number }) => Promise<WorkspaceArchiveSessionResponse>;

/** What an archive or a restore did: the sessions whose state changed, or why none (or not all) did. */
export type SessionArchiveResult = Readonly<{ kind: "done"; changed: readonly string[] }>
  | Readonly<{ kind: "error"; code: string; changed: readonly string[]; blocking: string | null }>;

/**
 * Asks the host to archive a session (`archived`), or to take it out of the archive, with the sessions below it when
 * `withChildren` says so. A session at work, or one below it, is refused with the session that works.
 */
export async function setSessionArchived(invoke: Invoke, epoch: string | null | undefined, sessionId: string, withChildren: boolean): Promise<SessionArchiveResult> {
  if (!epoch) return { kind: "error", code: "unconfigured", changed: [], blocking: null };
  try {
    const reply = await invoke({ expectedHostEpoch: epoch, sessionId, withChildren }, { timeoutMilliseconds: 60_000 });
    if (!reply || reply.hostEpoch !== epoch || reply.sessionId !== sessionId) return { kind: "error", code: "archive_unconfirmed", changed: [], blocking: null };
    const changed = reply.changed.filter(id => typeof id === "string");
    if (reply.status !== "ok") return { kind: "error", code: reply.status, changed, blocking: reply.blockingSessionId ?? null };
    return { kind: "done", changed };
  } catch { return { kind: "error", code: "archive_unconfirmed", changed: [], blocking: null }; }
}

/**
 * The sessions below a session that its archive takes with it: the ones that are not archived yet, wherever they
 * are listed. `sessions` is the whole catalog, archived sessions included.
 */
export function archivableDescendants(sessions: readonly WorkspaceSession[], id: string): WorkspaceSession[] {
  const found: WorkspaceSession[] = [];
  const seen = new Set([id]);
  for (let parents = [id]; parents.length > 0;) {
    const next: string[] = [];
    for (const session of sessions) {
      if (!session.parentSessionId || !parents.includes(session.parentSessionId) || seen.has(session.id)) continue;
      seen.add(session.id);
      next.push(session.id);
      if (!session.archived) found.push(session);
    }
    parents = next;
  }
  return found;
}

/** Why a session was not archived or restored, in the words of the window. */
export function sessionArchiveMessage(code: string): MessageKey {
  switch (code) {
    case "session_in_use": return "A session at work cannot be archived, nor one whose child sessions are at work. Wait for it to end, or stop it, then archive.";
    case "queue_pending": return "This session has queued prompts. Send or remove them, then archive.";
    case "interaction_pending": return "This session waits for an answer. Answer it, then archive.";
    case "session_missing": return "This session is no longer in the catalog.";
    case "busy": return "Another change of sessions is under way. Try again once it has ended.";
    case "archive_partial": return "Only some sessions changed. The lists show which.";
    default: return "The archive could not be confirmed. Check the session before trying again.";
  }
}
