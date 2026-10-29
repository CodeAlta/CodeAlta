import type { WorkspaceSession } from "#neoastra";
import type { SessionHierarchyRow } from "../sessionHierarchy";

/**
 * A line of the tree of the sessions of a scope: a session, a link to a sub-agent of a session that is listed in
 * another project or among the chats, or what stands for the sub-agents of a session that are not listed.
 */
export type SessionTreeEntry =
  | Readonly<{
    kind: "session";
    row: SessionHierarchyRow;
    /** Whether it has sub-agents, listed under it unless it is collapsed. */
    parent: boolean;
    collapsed: boolean;
  }>
  | Readonly<{
    kind: "link";
    /** The sub-agent, which its own scope lists. */
    session: WorkspaceSession;
    /** The session that started it. */
    parentId: string;
    depth: number;
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

/**
 * A line of the list of sessions of a scope: a line of its tree, or the group of the Claude Code sessions started in
 * VS Code or a terminal in the folder of the project.
 */
export type SessionListEntry =
  | SessionTreeEntry
  | Readonly<{
    kind: "claude-code";
    /** The project whose folder those sessions were started in. */
    projectId: string;
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

type Level = { parentId: string | null; limit: number; seen: number; hidden: number; extended: boolean; links: readonly WorkspaceSession[] };

/**
 * What the Explorer lists of the sessions of a scope, from its rows in tree order. Each level has its own
 * count: the sessions of the scope, then the sub-agents of each session. A collapsed session lists none of
 * its sub-agents, the selected one included. The sub-agents of a session that other scopes list follow its own,
 * as links, and count with them.
 */
export function sessionList(rows: readonly SessionHierarchyRow[], limits: SessionListLimits): { entries: SessionTreeEntry[]; hidden: number } {
  // The selected row and the rows it is under.
  const lineage = new Set<number>();
  const path: number[] = [];
  rows.forEach((row, index) => {
    path.length = row.depth;
    if (row.session.id === limits.active) { lineage.add(index); for (const parent of path) if (parent !== undefined) lineage.add(parent); }
    path[row.depth] = index;
  });

  const entries: SessionTreeEntry[] = [];
  const levels: Level[] = [{ parentId: null, limit: limits.count, seen: 0, hidden: 0, extended: false, links: [] }];
  // The sub-agents of a session end here, with the links to those listed elsewhere: what is not listed of them is said after them.
  const close = (depth: number) => {
    while (levels.length > depth + 1) {
      const level = levels.pop()!;
      for (const session of level.links) {
        if (level.seen++ >= level.limit) level.hidden++;
        else entries.push({ kind: "link", session, parentId: level.parentId!, depth: levels.length });
      }
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
    const links = row.childrenElsewhere ?? [];
    const parent = (rows[index + 1]?.depth ?? 0) > row.depth || links.length > 0;
    const collapsed = parent && limits.collapsed(id);
    entries.push({ kind: "session", row, parent, collapsed });
    if (collapsed) skip = row.depth;
    else if (parent) {
      const extra = Math.max(0, limits.extra(id));
      levels.push({ parentId: id, limit: limits.subCount + extra, seen: 0, hidden: 0, extended: extra > 0, links });
    }
  });
  close(0);
  return { entries, hidden: levels[0].hidden };
}

/** The sessions listed, in the order of the list. */
export const listedSessions = (entries: readonly SessionListEntry[]): SessionHierarchyRow[] =>
  entries.flatMap(entry => entry.kind === "session" ? [entry.row] : []);

/**
 * The list of a scope with the place of the group of the Claude Code sessions started elsewhere (in VS Code or a
 * terminal) in the folder of its project, last: only a project has one, and only when a Claude Code provider is
 * configured. The group shows itself once it found such sessions.
 */
export const withClaudeCodeSessions = (entries: readonly SessionListEntry[], projectId: string | null, claudeCode: boolean): SessionListEntry[] =>
  projectId !== null && claudeCode ? [...entries, { kind: "claude-code", projectId }] : [...entries];

/** The lines of a list that are sessions or stand for sub-agents: those the list counts as its sessions. */
export const sessionEntries = (entries: readonly SessionListEntry[]): SessionTreeEntry[] =>
  entries.flatMap(entry => entry.kind === "claude-code" ? [] : [entry]);
