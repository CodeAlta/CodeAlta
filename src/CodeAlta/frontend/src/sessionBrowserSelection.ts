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
