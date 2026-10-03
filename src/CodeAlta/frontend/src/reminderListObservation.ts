import type { ReminderListResponse, WorkspaceSession, WorkspaceSnapshot } from "#neoastra";
import type { ReminderTarget } from "./reminderActions";
import { selectedSessionInfoAvailable } from "./sessionInfo";

// An unmatched row in the global rail, a duplicate/omitted catalog identity or an archived
// project does not establish the selected persisted owner even if its session ID is present.
export function verifiedReminderCountTarget(snapshot: WorkspaceSnapshot, session: WorkspaceSession,
  projectId: string | null): boolean {
  // A truncated catalog still lists this exact session once; the host reads reminders by session id.
  if (!selectedSessionInfoAvailable(snapshot, session, projectId)) return false;
  if (projectId === null) return session.scopeKind === "global" && session.projectId === null;
  return session.scopeKind === "project" && session.projectId === projectId &&
    snapshot.projects.filter(project => project.id === projectId).length === 1 &&
    snapshot.projects.some(project => project.id === projectId && project.path === session.workspacePath && !project.archived);
}

// The host's list contains all retained rows (at most 32), including completed rows.
// Status/error-path zeros and omitted or inconsistent rows are not count observations.
export function validReminderList(target: ReminderTarget, value: ReminderListResponse): boolean {
  return value.epoch === target.epoch && value.sessionId === target.sessionId && value.status === "ok" &&
    Array.isArray(value.reminders) && value.reminders.length <= 32 &&
    new Set(value.reminders.map(row => row.id)).size === value.reminders.length &&
    !value.reminders.some(row => typeof row.id !== "string" || !row.id || row.id.length > 256 ||
      !["active", "completed"].includes(row.state) || typeof row.preview !== "string" || row.preview.length > 160 ||
      !Number.isInteger(row.delaySeconds) || row.delaySeconds < 1 || row.delaySeconds > 86400 ||
      !Number.isInteger(row.repeatCount) || row.repeatCount < 1 || row.repeatCount > 20 ||
      !Number.isInteger(row.firedCount) || row.firedCount < 0 || row.firedCount > row.repeatCount ||
      row.lastError != null && (typeof row.lastError !== "string" || row.lastError.length > 128)) &&
    value.activeCount === value.reminders.filter(row => row.state === "active").length &&
    value.completedCount === value.reminders.filter(row => row.state === "completed").length;
}
