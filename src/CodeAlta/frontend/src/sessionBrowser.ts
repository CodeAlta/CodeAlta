import type { WorkspaceSession, WorkspaceSnapshot } from "#neoastra";
import { hasLabelWords, splitLabelWords } from "./labels/labels";
import { sessionTreeRows } from "./sessionBrowserSelection";
import { resolveSessionTab, type SessionTab } from "./sessionTabs";

export const browserLimit = 200;
export function browseSessions(snapshot: WorkspaceSnapshot, projectId: string | null, query: string) {
  const needle = query.slice(0, 256).trim().toLocaleLowerCase();
  const scoped = snapshot.sessions.filter(row => projectId === null
    ? row.scopeKind === "global" && row.projectId === null : row.scopeKind === "project" && row.projectId === projectId);
  const matched = scoped.filter(row => `${row.fullTitle}\n${row.title}\n${row.id}`.toLocaleLowerCase().includes(needle));
  const rows = matched.slice(0, browserLimit).map(row => {
    const tab: SessionTab = { sessionId: row.id, projectId, path: row.workspacePath };
    return { row, tab, valid: !!resolveSessionTab(snapshot, tab) };
  });
  return { rows, loaded: scoped.length, matched: matched.length, hidden: Math.max(0, matched.length - rows.length),
    incomplete: snapshot.projectsTruncated || snapshot.sessionsTruncated || snapshot.displayTextTruncated };
}

/**
 * The sessions of a scope as the browser shows them: a tree of all of them, each match of the search with the whole tree
 * it belongs to, then at most `limit` rows. `matched` counts what the search finds, `shownMatches` those the rows
 * show, and `hidden` the matches the limit leaves out.
 */
export function browseTree(snapshot: WorkspaceSnapshot, projectId: string | null, query: string, compare: (a: WorkspaceSession, b: WorkspaceSession) => number,
  limit = browserLimit) {
  // A word `label:audit` keeps the sessions with a label whose id starts with `audit`; the rest is looked for as one text.
  const typed = query.slice(0, 256).trim().toLocaleLowerCase();
  const { words, labels } = splitLabelWords(typed.split(/\s+/u).filter(Boolean));
  const needle = labels.length ? words.join(" ") : typed;
  const scoped = snapshot.sessions.filter(row => projectId === null
    ? row.scopeKind === "global" && row.projectId === null : row.scopeKind === "project" && row.projectId === projectId);
  const matched = needle || labels.length ? new Set(scoped.filter(row => hasLabelWords(row, labels)
    && `${row.fullTitle}\n${row.title}\n${row.id}`.toLocaleLowerCase().includes(needle)).map(row => row.id)) : null;
  const rows = sessionTreeRows(scoped, matched, compare).slice(0, limit).map(node => {
    const tab: SessionTab = { sessionId: node.session.id, projectId, path: node.session.workspacePath };
    return { row: node.session, tab, valid: !!resolveSessionTab(snapshot, tab), depth: node.depth, match: node.match };
  });
  const total = matched ? matched.size : scoped.length;
  const shownMatches = rows.filter(item => item.match).length;
  return { rows, matched: total, shownMatches, hidden: Math.max(0, total - shownMatches),
    incomplete: snapshot.projectsTruncated || snapshot.sessionsTruncated || snapshot.displayTextTruncated };
}

export function browserActivation(snapshot: WorkspaceSnapshot | undefined, tab: SessionTab, capturedRevision: number, currentRevision: number): boolean {
  return capturedRevision === currentRevision && !!snapshot && !!resolveSessionTab(snapshot, tab);
}
