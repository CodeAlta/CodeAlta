import type { WorkspaceSnapshot } from "#neoastra";
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

export function browserActivation(snapshot: WorkspaceSnapshot | undefined, tab: SessionTab, capturedRevision: number, currentRevision: number): boolean {
  return capturedRevision === currentRevision && !!snapshot && !!resolveSessionTab(snapshot, tab);
}
