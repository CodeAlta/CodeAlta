import type { WorkspaceSession } from "#neoastra";

/** A row of the list of sessions: `subAgents` is how many sessions it started (its sub-agents), shown under it. */
export type SessionHierarchyRow = { session: WorkspaceSession; depth: number; diagnostic: string | null; tooltip: string; subAgents?: number;
  /** Its parent, listed in another project, among the chats or in another space: the row is at the top of its list. */
  parentElsewhere?: WorkspaceSession;
  /** The sessions it started that are listed in another project or among the chats, newest first: links to them are shown under it. */
  childrenElsewhere?: readonly WorkspaceSession[];
  /** Those of `childrenElsewhere` it links to, when not all of them: the ones the filter of the sidebar finds. */
  linked?: readonly WorkspaceSession[] };

// Keep the server's bounded order for ties. Resolve only an exact persisted scope; a missing
// header or a missing/truncated parent can never manufacture a same-scope relationship.
export function sessionHierarchy(visibleScope: readonly WorkspaceSession[], all: readonly WorkspaceSession[],
  selectedProjectId: string | null): SessionHierarchyRow[] {
  const buckets = new Map<string, WorkspaceSession[]>();
  for (const session of all) {
    const key = session.id.toLowerCase();
    if (!buckets.has(key)) buckets.set(key, []);
    buckets.get(key)!.push(session);
  }
  const only = (id: string) => {
    const matches = buckets.get(id.toLowerCase());
    return matches?.length === 1 ? matches[0] : undefined;
  };
  const known = new Set(visibleScope.map(session => session.id));
  const issue = new Map<string, string | null>();
  const parentOf = new Map<string, WorkspaceSession>();
  const elsewhere = new Map<string, WorkspaceSession>();
  for (const session of visibleScope) {
    const parentId = session.parentSessionId;
    let diagnostic: string | null = session.lineageIssue === "invalid_parent" ? "Parent identifier is invalid; shown at root." : null;
    if (!diagnostic && parentId) {
      const parent = only(parentId);
      if (!parent) diagnostic = `Parent session '${parentId}' is absent or ambiguous in this bounded snapshot; shown at root.`;
      else if (!session.scopeKind || !parent.scopeKind)
        diagnostic = `Parent session '${parentId}' has unverified persisted scope; shown at root.`;
      else if (session.scopeKind !== parent.scopeKind || session.projectId !== parent.projectId
        || session.workspacePath !== parent.workspacePath || !known.has(parent.id)
        || selectedProjectId !== null && (session.scopeKind !== "project" || session.projectId !== selectedProjectId))
      {
        // A child of a session listed elsewhere, as a session of a project started by a chat: nothing is wrong with it.
        // The same project in another folder is: its headers disagree.
        if (session.scopeKind === parent.scopeKind && session.projectId === parent.projectId && session.workspacePath !== parent.workspacePath)
          diagnostic = `Parent session '${parentId}' belongs to another scope; shown at root.`;
        else elsewhere.set(session.id, parent);
      }
      else {
        const visited = new Set([session.id.toLowerCase()]);
        let current: WorkspaceSession | undefined = parent;
        while (current) {
          if (visited.has(current.id.toLowerCase())) { diagnostic = "Parent lineage contains a cycle; shown at root."; break; }
          visited.add(current.id.toLowerCase());
          current = current.parentSessionId ? only(current.parentSessionId) : undefined;
          if (visited.size > all.length) { diagnostic = "Parent lineage cannot be verified; shown at root."; break; }
        }
        if (!diagnostic) parentOf.set(session.id, parent);
      }
    }
    issue.set(session.id, diagnostic);
  }

  // Saved-update ordering only (not TUI LastActiveAt); never recurse through untrusted lineage.
  const activity = new Map(visibleScope.map(session => [session.id, Date.parse(session.updatedAt) || 0]));
  for (const session of visibleScope) {
    const seen = new Set<string>();
    let node: WorkspaceSession | undefined = session;
    while (node && !seen.has(node.id)) {
      seen.add(node.id);
      activity.set(node.id, Math.max(activity.get(node.id) ?? 0, activity.get(session.id) ?? 0));
      node = parentOf.get(node.id);
    }
  }
  const order = (a: WorkspaceSession, b: WorkspaceSession) => (activity.get(b.id) ?? 0) - (activity.get(a.id) ?? 0)
    || a.title.localeCompare(b.title, undefined, { sensitivity: "base" }) || a.id.localeCompare(b.id);
  const rows: SessionHierarchyRow[] = [];
  const childrenById = new Map<string, WorkspaceSession[]>();
  for (const session of visibleScope) {
    const parent = parentOf.get(session.id);
    if (parent) {
      if (!childrenById.has(parent.id)) childrenById.set(parent.id, []);
      childrenById.get(parent.id)!.push(session);
    }
  }
  // The children of a listed session that are listed elsewhere: those of the snapshot that this scope does not list.
  // An archived child is not shown, as it is not in its own list.
  const away = new Map<string, WorkspaceSession[]>();
  for (const session of all) {
    if (!session.parentSessionId || session.archived || known.has(session.id)) continue;
    const parent = only(session.parentSessionId);
    if (!parent || !known.has(parent.id)) continue;
    if (!away.has(parent.id)) away.set(parent.id, []);
    away.get(parent.id)!.push(session);
  }
  const newest = (a: WorkspaceSession, b: WorkspaceSession) => (Date.parse(b.updatedAt) || 0) - (Date.parse(a.updatedAt) || 0) || a.id.localeCompare(b.id);
  const stack = visibleScope.filter(session => !parentOf.has(session.id)).sort(order)
    .reverse().map(session => ({ session, depth: 0 }));
  const seen = new Set<string>();
  while (stack.length) {
    const { session, depth } = stack.pop()!;
    if (seen.has(session.id)) continue;
    seen.add(session.id);
    const diagnostic = issue.get(session.id) ?? null;
    const parent = parentOf.get(session.id);
    const home = elsewhere.get(session.id);
    const tooltip = [session.fullTitle + (session.fullTitleTruncated ? " [title truncated]" : ""),
      diagnostic ?? (parent ? `Child of '${parent.fullTitle}'` : home ? `Child of '${home.fullTitle}', ${home.archived ? "archived" : "listed elsewhere"}` : null)].filter(Boolean).join(" | ");
    const children = (childrenById.get(session.id) ?? []).sort(order);
    const links = away.get(session.id)?.sort(newest);
    rows.push({ session, depth, diagnostic, tooltip, subAgents: children.length, ...(home ? { parentElsewhere: home } : {}), ...(links ? { childrenElsewhere: links } : {}) });
    for (const child of children.reverse()) stack.push({ session: child, depth: depth + 1 });
  }
  // A malformed graph must not silently drop a row, even if its parent was excluded.
  for (const session of visibleScope) if (!seen.has(session.id))
    rows.push({ session, depth: 0, diagnostic: "Lineage cannot be verified; shown at root.",
      tooltip: `${session.fullTitle} | Lineage cannot be verified; shown at root.` });
  return rows;
}
