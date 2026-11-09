// The sessions checked in the browser of the saved sessions, and what deleting them together means.
import type { WorkspaceSession } from "#neoastra";

/** The checked sessions with `ids` checked or not, as `value` says. */
export function withChecked(checked: ReadonlySet<string>, ids: readonly string[], value: boolean): ReadonlySet<string> {
  const next = new Set(checked);
  for (const id of ids) {
    if (value) next.add(id); else next.delete(id);
  }
  return next;
}

/**
 * The rows a Shift-click checks or unchecks: from the row clicked before to this one, both included, in the order the
 * table shows them. Without a row clicked before, or one no longer shown, the row clicked alone.
 */
export function checkedRange(shown: readonly string[], anchor: string | null, clicked: string): readonly string[] {
  const to = shown.indexOf(clicked);
  if (to < 0) return [];
  const from = anchor === null ? -1 : shown.indexOf(anchor);
  if (from < 0) return [clicked];
  return shown.slice(Math.min(from, to), Math.max(from, to) + 1);
}

/** A row of the tree of the sessions of a scope: how deep it is, and whether the search found it or it is shown with what it found. */
export type SessionTreeRow = Readonly<{ session: WorkspaceSession; depth: number; match: boolean }>;

/**
 * The sessions of a scope as a tree: each session under the one it comes from, the sessions of a level in the order
 * `compare` gives. A session whose parent is not in the scope is at the top. With a search, `matched` are the sessions
 * it found: each is shown with the whole tree it belongs to, from the session at its top down, since checking a session
 * checks all that is below it. The sessions shown with a match are not matches themselves.
 */
export function sessionTreeRows(sessions: readonly WorkspaceSession[], matched: ReadonlySet<string> | null,
  compare: (a: WorkspaceSession, b: WorkspaceSession) => number): readonly SessionTreeRow[] {
  const byId = new Map(sessions.map(session => [session.id, session]));
  const children = new Map<string, WorkspaceSession[]>();
  const roots: WorkspaceSession[] = [];
  for (const session of sessions) {
    const parent = session.parentSessionId;
    if (parent !== null && parent !== session.id && byId.has(parent)) {
      const list = children.get(parent) ?? [];
      list.push(session);
      children.set(parent, list);
    } else roots.push(session);
  }
  let shown: Set<string> | null = null;
  if (matched) {
    shown = new Set<string>();
    for (const id of matched) {
      if (!byId.has(id) || shown.has(id)) continue;
      // The session at the top of its tree, then all that is below it.
      let top = id;
      for (const seen = new Set([id]); ;) {
        const parent = byId.get(top)!.parentSessionId;
        if (parent === null || !byId.has(parent) || seen.has(parent)) break;
        seen.add(parent);
        top = parent;
      }
      shown.add(top);
      for (let level = [top]; level.length > 0;) {
        level = level.flatMap(parent => (children.get(parent) ?? []).map(child => child.id)).filter(child => !shown!.has(child));
        for (const child of level) shown.add(child);
      }
    }
  }
  const rows: SessionTreeRow[] = [];
  const visited = new Set<string>();
  const visit = (session: WorkspaceSession, depth: number) => {
    if (visited.has(session.id) || shown && !shown.has(session.id)) return;
    visited.add(session.id);
    rows.push({ session, depth, match: !matched || matched.has(session.id) });
    for (const child of [...(children.get(session.id) ?? [])].sort(compare)) visit(child, depth + 1);
  };
  for (const root of [...roots].sort(compare)) visit(root, 0);
  // A session of a cycle has no top: it is shown at the top, once.
  for (const session of sessions) if (!visited.has(session.id)) visit(session, 0);
  return rows;
}

/** The sessions of `sessions` below a session, and those above it, by id. */
function relatives(sessions: readonly WorkspaceSession[], id: string): Readonly<{ below: readonly string[]; above: readonly string[] }> {
  const below = descendants(sessions, id).map(session => session.id);
  const above: string[] = [];
  const byId = new Map(sessions.map(session => [session.id, session]));
  for (let parent = byId.get(id)?.parentSessionId ?? null; parent !== null && byId.has(parent) && !above.includes(parent) && parent !== id; parent = byId.get(parent)!.parentSessionId) above.push(parent);
  return { below, above };
}

/**
 * The checked sessions after `ids` are checked or unchecked as a tree does: checking a session checks the sessions
 * below it, and each session above it all of whose sessions below are now checked; unchecking one unchecks the
 * sessions below it and the ones above it, which are no longer checked whole. `canCheck` says which sessions can be
 * checked: one that cannot is never checked, and the sessions above it are not checked whole.
 */
export function withCheckedTree(checked: ReadonlySet<string>, sessions: readonly WorkspaceSession[], ids: readonly string[], value: boolean,
  canCheck: (id: string) => boolean = () => true): ReadonlySet<string> {
  const next = new Set(checked);
  for (const id of ids) {
    const { below, above } = relatives(sessions, id);
    if (!value) { for (const each of [id, ...below, ...above]) next.delete(each); continue; }
    const added = [id, ...below].filter(canCheck);
    for (const each of added) next.add(each);
    // The deepest first: a session is checked only with all that is below it, which one that cannot be checked prevents.
    for (const each of [...added].reverse()) if (!descendants(sessions, each).every(child => next.has(child.id))) next.delete(each);
    // The nearest first: a session above is checked once all that is below it is.
    for (const parent of above) {
      if (!canCheck(parent) || !descendants(sessions, parent).every(below => next.has(below.id))) break;
      next.add(parent);
    }
  }
  return next;
}

/**
 * The checked sessions as they can be acted on: without the sessions of `sessions` that cannot be checked (any more: one
 * that started to work), and without those of `sessions` that are no longer checked whole. Other checked sessions stay.
 */
export function checkableChecked(checked: ReadonlySet<string>, sessions: readonly WorkspaceSession[], canCheck: (id: string) => boolean): ReadonlySet<string> {
  const next = new Set(checked);
  for (const session of sessions) if (!canCheck(session.id)) next.delete(session.id);
  // The deepest first, so that a session is looked at after the ones below it.
  const deepestFirst = sessionTreeRows(sessions, null, () => 0).map(row => row.session).reverse();
  for (const session of deepestFirst) if (next.has(session.id) && !descendants(sessions, session.id).every(child => next.has(child.id))) next.delete(session.id);
  return next;
}

/** Whether a session is checked with all the sessions below it, partly (it or some of them), or not at all. */
export function treeCheckState(checked: ReadonlySet<string>, sessions: readonly WorkspaceSession[], id: string): "checked" | "partial" | "none" {
  const below = descendants(sessions, id).map(session => session.id);
  const all = [id, ...below];
  const count = all.filter(each => checked.has(each)).length;
  return count === all.length ? "checked" : count > 0 ? "partial" : "none";
}

/** The sessions below each session of the catalog, wherever they are, archived ones included. */
function descendants(catalog: readonly WorkspaceSession[], id: string): readonly WorkspaceSession[] {
  const found: WorkspaceSession[] = [];
  const seen = new Set([id]);
  for (let at = [id]; at.length > 0;) {
    const below = catalog.filter(session => session.parentSessionId !== null && at.includes(session.parentSessionId) && !seen.has(session.id));
    for (const session of below) seen.add(session.id);
    found.push(...below);
    at = below.map(session => session.id);
  }
  return found;
}

/** How many sessions of the catalog a session comes from. */
function depth(catalog: readonly WorkspaceSession[], session: WorkspaceSession): number {
  const seen = new Set<string>();
  let count = 0;
  for (let parent = session.parentSessionId; parent !== null && !seen.has(parent); count++) {
    seen.add(parent);
    parent = catalog.find(value => value.id === parent)?.parentSessionId ?? null;
  }
  return count;
}

export type SessionDeletionPlan = Readonly<{
  /** The checked sessions that are deleted, the deepest first: a session is deleted once the ones below it are. */
  order: readonly WorkspaceSession[];
  /** The checked sessions that are kept: some session below them is not checked, and would lose its parent. */
  kept: readonly WorkspaceSession[];
  /** The sessions below the kept ones that are not checked. */
  missing: readonly WorkspaceSession[];
}>;

/**
 * What a deletion of the checked sessions does. A session is deleted alone, never with sessions that were not
 * checked: one that others come from is deleted only when all of them are checked too, after them.
 */
export function sessionDeletionPlan(checked: readonly WorkspaceSession[], catalog: readonly WorkspaceSession[]): SessionDeletionPlan {
  const ids = new Set(checked.map(session => session.id));
  const kept: WorkspaceSession[] = [];
  const missing = new Map<string, WorkspaceSession>();
  const deleted: WorkspaceSession[] = [];
  for (const session of checked) {
    const unchecked = descendants(catalog, session.id).filter(below => !ids.has(below.id));
    if (unchecked.length === 0) { deleted.push(session); continue; }
    kept.push(session);
    for (const below of unchecked) missing.set(below.id, below);
  }
  const order = deleted.map(session => ({ session, depth: depth(catalog, session) }))
    .sort((a, b) => b.depth - a.depth).map(item => item.session);
  return { order, kept, missing: [...missing.values()] };
}
