import type { WorkspaceSession, WorkspaceSnapshot } from "#neoastra";
import { sessionsForProject } from "./workspace";

export type SessionInfoView = Readonly<{
  id: string;
  title: string;
  titleTruncated: boolean;
  scope: string;
  scopeWarning: string | null;
  path: string | null;
  /** The git worktree the session works in, and whether its folder is gone; null for the folder of the project. */
  worktree: Readonly<{ path: string; missing: boolean }> | null;
  provider: string | null;
  updatedAt: string | null;
  createdAt: string | null;
  canCopyId: boolean;
}>;

function recordedDate(value: string): boolean {
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$/.exec(value);
  if (!match) return false;
  const [year, month, day, hour, minute, second] = match.slice(1).map(Number);
  const leap = year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
  const days = [31, leap ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
  return year > 1 && month >= 1 && month <= 12 && day >= 1 && day <= days[month - 1]
    && hour <= 23 && minute <= 59 && second <= 59 && Number.isFinite(Date.parse(value));
}

export function sessionInfoView(snapshot: WorkspaceSnapshot, session: WorkspaceSession, selectedProjectId: string | null): SessionInfoView {
  const unique = !!session.id?.trim() && snapshot.sessions.filter(value => value.id === session.id).length === 1
    && snapshot.sessions.some(value => value === session);
  if (!unique) return {
    id: session.id, title: "Unverified", titleTruncated: false, scope: "Unverified: ambiguous session identity",
    scopeWarning: null, path: null, worktree: null, provider: null, updatedAt: null, createdAt: null, canCopyId: false,
  };

  const project = session.scopeKind === "project" && session.projectId && session.workspacePath
    ? snapshot.projects.filter(value => value.id === session.projectId && value.path === session.workspacePath)
    : [];
  const verifiedProject = project.length === 1 ? project[0] : null;
  const scope = verifiedProject ? `Project: ${verifiedProject.name}`
    : session.scopeKind === "global" && session.projectId === null ? "Chat" : "Unverified / unmatched scope";
  const scopeWarning = verifiedProject && selectedProjectId !== verifiedProject.id
    ? "The selected project does not match this session's verified project."
    : scope === "Chat" && selectedProjectId !== null
      ? "This chat does not belong to the selected project." : null;
  // The bounded display title is already in the header; do not surface longer summary/prompt-derived text here.
  const title = session.title;
  const date = session.updatedAt;
  const updatedAt = typeof date === "string" && recordedDate(date) ? date : null;
  return {
    id: session.id, title: title?.trim() ? title : "Not recorded",
    titleTruncated: session.fullTitleTruncated || !!session.fullTitle && session.fullTitle !== title,
    scope, scopeWarning, path: session.workspacePath?.trim() ? session.workspacePath : null,
    worktree: session.worktreePath?.trim() ? { path: session.worktreePath, missing: session.worktreeMissing === true } : null,
    provider: session.providerKey?.trim() ? session.providerKey : null,
    updatedAt, createdAt: typeof session.createdAt === "string" && recordedDate(session.createdAt) ? session.createdAt : null,
    canCopyId: true,
  };
}

export function selectedSessionInfoAvailable(snapshot: WorkspaceSnapshot | undefined, session: WorkspaceSession | undefined,
  selectedProjectId: string | null): boolean {
  return !!snapshot && !!session && sessionsForProject(snapshot, selectedProjectId).includes(session)
    && sessionInfoView(snapshot, session, selectedProjectId).canCopyId;
}

export type SessionInfoSelection = Readonly<{ sessionId: string; projectId: string | null }>;

export function selectedSessionInfoSelection(snapshot: WorkspaceSnapshot | undefined, session: WorkspaceSession | undefined,
  projectId: string | null, selectedSessionId: string | null, selectedScope: string | null): SessionInfoSelection | null {
  return session && session.id === selectedSessionId && selectedScope === projectId
    && selectedSessionInfoAvailable(snapshot, session, projectId) ? { sessionId: session.id, projectId } : null;
}

export async function copySessionId(id: string, writer: () => ((text: string) => Promise<void>) | undefined):
  Promise<"copied" | "unavailable" | "failed"> {
  if (!id?.trim()) return "failed";
  try {
    const write = writer();
    if (!write) return "unavailable";
    await write(id);
    return "copied";
  } catch { return "failed"; }
}

export function sessionInfoCopyFeedback(result: "copied" | "unavailable" | "failed"): string {
  return result === "copied" ? "Session ID copied."
    : result === "unavailable" ? "Clipboard unavailable; nothing copied." : "Could not copy session ID.";
}

export function restoreSessionInfoFocus(trigger: Pick<HTMLButtonElement, "isConnected" | "focus"> | null): boolean {
  if (!trigger?.isConnected) return false;
  trigger.focus();
  return true;
}

export function dismissSessionInfoOnKey(event: { key: string; isComposing?: boolean; keyCode?: number }): boolean {
  return event.key === "Escape" && !event.isComposing && event.keyCode !== 229;
}
