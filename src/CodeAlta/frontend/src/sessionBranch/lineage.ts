// Where a session comes from when it is a fork, or what a rewind kept of another one.
import type { WorkspaceSession } from "#neoastra";

/**
 * The origin of a session that was not started empty. `kind` is `fork` for a fork and `rewind` for the session
 * that keeps the turns a rewind removed; `title` is the title of the source when the fork was made. `turnCount`
 * is the number of turns before the point: those a fork started with, and those the source kept when it was
 * rewound, while the session that keeps what the rewind removed holds every turn the source had.
 */
export type SessionLineage = Readonly<{ kind: "fork" | "rewind"; sourceId: string; title: string; turnCount: number }>;

type Lineage = Pick<WorkspaceSession, "forkedFromSessionId" | "forkedFromTitle" | "forkKind" | "forkTurnCount">;

export function sessionLineage(session: Lineage): SessionLineage | null {
  const sourceId = session.forkedFromSessionId;
  if (!sourceId) return null;
  return { kind: session.forkKind === "rewind" ? "rewind" : "fork", sourceId, title: session.forkedFromTitle?.trim() || sourceId,
    turnCount: Number.isInteger(session.forkTurnCount) && session.forkTurnCount > 0 ? session.forkTurnCount : 0 };
}

/** The title to name the source by: the one it has now when it is still listed, else the one it had. */
export function lineageTitle(lineage: SessionLineage, sessions: readonly Pick<WorkspaceSession, "id" | "title">[]): { title: string; exists: boolean } {
  const source = sessions.find(session => session.id === lineage.sourceId);
  return { title: source?.title.trim() || lineage.title, exists: !!source };
}

/** The line that says the origin of a session in plain English, for the copy of its details. */
export function lineageSummary(lineage: SessionLineage): string {
  const turns = lineage.turnCount === 1 ? "1 turn" : `${lineage.turnCount} turns`;
  return lineage.kind === "rewind"
    ? `Kept from a rewind of ${lineage.title} (${lineage.turnCount > 0 ? `rewound to the end of turn ${lineage.turnCount}` : "rewound to its start"}); session ${lineage.sourceId}`
    : `Forked from ${lineage.title} (started with ${turns}); session ${lineage.sourceId}`;
}
