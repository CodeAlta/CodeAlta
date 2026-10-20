// Which sessions a review can go to, and which checkout a session works in, as the host decides it for `alta review`.
import type { WorkspaceSession } from "#neoastra";
import { sameFolder, sessionWorktree } from "../worktrees/worktrees";

type SessionRow = Pick<WorkspaceSession, "id" | "title" | "projectId" | "archived" | "updatedAt" | "parentSessionId" | "worktreePath" | "worktreeRoot" | "worktreeName" | "worktreeMissing">;

/** The checkout a session works in: its worktree, else the project folder (null). */
export function sessionCheckout(session: Pick<WorkspaceSession, "worktreePath" | "worktreeRoot" | "worktreeName" | "worktreeMissing">): string | null {
  return sessionWorktree(session)?.path ?? null;
}

/** Whether two checkouts are the same folder; null is the project folder. */
export function sameCheckout(first: string | null, second: string | null): boolean {
  return first === null || second === null ? first === second : sameFolder(first, second);
}

/** A session a round can be sent to. */
export type ReviewSessionChoice = Readonly<{ id: string; title: string }>;

/**
 * The sessions a review of a checkout can go to: the sessions of the project that work in that checkout, the one
 * the user is in first, then the most recent; sub-sessions and archived sessions are not offered.
 */
export function reviewSessions(sessions: readonly SessionRow[], projectId: string, checkout: string | null, current: string | null = null): ReviewSessionChoice[] {
  return sessions.filter(session => session.projectId === projectId && !session.archived && !session.parentSessionId && sameCheckout(sessionCheckout(session), checkout))
    .sort((a, b) => (a.id === current ? -1 : 0) - (b.id === current ? -1 : 0) || (a.updatedAt < b.updatedAt ? 1 : a.updatedAt > b.updatedAt ? -1 : 0))
    .map(session => ({ id: session.id, title: session.title }));
}
