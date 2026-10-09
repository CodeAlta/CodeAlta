import type { SessionHierarchyRow } from "../sessionHierarchy";

/**
 * A line of the list of sessions of a scope: a session, or what stands for the sub-agents of a session that
 * are not listed.
 */
export type SessionListEntry =
  | Readonly<{
    kind: "session";
    row: SessionHierarchyRow;
    /** Whether it has sub-agents, listed under it unless it is collapsed. */
    parent: boolean;
    collapsed: boolean;
  }>
  | Readonly<{
    kind: "more";
    /** The session whose sub-agents these are. */
    parentId: string;
    /** The depth of those sub-agents. */
    depth: number;
    /** How many of them are not listed. */
    hidden: number;
    /** Whether more of them than at first are listed. */
    extended: boolean;
  }>;

export type SessionListLimits = Readonly<{
  /** How many sessions of the scope are listed. */
  count: number;
  /** How many sub-agents are listed under a session. */
  subCount: number;
  /** How many more sub-agents than `subCount` a session lists. */
  extra: (sessionId: string) => number;
  /** Whether the sub-agents of a session are hidden. */
  collapsed: (sessionId: string) => boolean;
  /** The selected session: it stays listed beyond the counts, with the sessions it is under. */
  active: string | null;
}>;

type Level = { parentId: string | null; limit: number; seen: number; hidden: number; extended: boolean };

/**
 * What the Explorer lists of the sessions of a scope, from its rows in tree order. Each level has its own
 * count: the sessions of the scope, then the sub-agents of each session. A collapsed session lists none of
 * its sub-agents, the selected one included.
 */
export function sessionList(rows: readonly SessionHierarchyRow[], limits: SessionListLimits): { entries: SessionListEntry[]; hidden: number } {
  // The selected row and the rows it is under.
  const lineage = new Set<number>();
  const path: number[] = [];
  rows.forEach((row, index) => {
    path.length = row.depth;
    if (row.session.id === limits.active) { lineage.add(index); for (const parent of path) if (parent !== undefined) lineage.add(parent); }
    path[row.depth] = index;
  });

  const entries: SessionListEntry[] = [];
  const levels: Level[] = [{ parentId: null, limit: limits.count, seen: 0, hidden: 0, extended: false }];
  // The sub-agents of a session end here: what is not listed of them is said after them.
  const close = (depth: number) => {
    while (levels.length > depth + 1) {
      const level = levels.pop()!;
      if (level.parentId !== null && (level.hidden > 0 || level.extended))
        entries.push({ kind: "more", parentId: level.parentId, depth: levels.length, hidden: level.hidden, extended: level.extended });
    }
  };
  // Rows deeper than this are under a row that is not listed, or that is collapsed.
  let skip = Number.POSITIVE_INFINITY;
  rows.forEach((row, index) => {
    if (row.depth > skip) return;
    skip = Number.POSITIVE_INFINITY;
    close(row.depth);
    // A row deeper than its place in the tree allows is listed where the tree stands.
    const level = levels[Math.min(row.depth, levels.length - 1)];
    if (level.seen++ >= level.limit && !lineage.has(index)) { level.hidden++; skip = row.depth; return; }
    const id = row.session.id;
    const parent = (rows[index + 1]?.depth ?? 0) > row.depth;
    const collapsed = parent && limits.collapsed(id);
    entries.push({ kind: "session", row, parent, collapsed });
    if (collapsed) skip = row.depth;
    else if (parent) {
      const extra = Math.max(0, limits.extra(id));
      levels.push({ parentId: id, limit: limits.subCount + extra, seen: 0, hidden: 0, extended: extra > 0 });
    }
  });
  close(0);
  return { entries, hidden: levels[0].hidden };
}

/** The sessions listed, in the order of the list. */
export const listedSessions = (entries: readonly SessionListEntry[]): SessionHierarchyRow[] =>
  entries.flatMap(entry => entry.kind === "session" ? [entry.row] : []);
