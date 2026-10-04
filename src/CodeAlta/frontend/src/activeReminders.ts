import type { ReminderActiveResponse, WorkspaceSnapshot } from "#neoastra";
import { sessionsForProject } from "./workspace";

/** Active reminder count per session id, as last reported by the host. */
export type ActiveReminders = ReadonlyMap<string, number>;

/** Counts from an exact, successful host reply; anything else (other epoch, failure, malformed rows) is unknown. */
export function activeReminderCounts(response: ReminderActiveResponse, epoch: string): ActiveReminders | null {
  if (response.status !== "ok" || response.epoch !== epoch || !Array.isArray(response.sessions)) return null;
  const counts = new Map<string, number>();
  for (const row of response.sessions) {
    if (typeof row?.sessionId !== "string" || !Number.isInteger(row.activeCount) || row.activeCount <= 0 || counts.has(row.sessionId)) return null;
    counts.set(row.sessionId, row.activeCount);
  }
  return counts;
}

/** Keeps the previous map when nothing changed, so a poll does not re-render the explorer. */
export function sameActiveReminders(left: ActiveReminders, right: ActiveReminders): boolean {
  if (left.size !== right.size) return false;
  for (const [id, count] of left) if (right.get(id) !== count) return false;
  return true;
}

/** Active reminders of every session shown under one explorer scope (a project, or "Global sessions" for null). */
export function scopeReminderCount(reminders: ActiveReminders, snapshot: WorkspaceSnapshot | undefined, projectId: string | null): number {
  if (!snapshot || reminders.size === 0) return 0;
  return sessionsForProject(snapshot, projectId).reduce((sum, session) => sum + (reminders.get(session.id) ?? 0), 0);
}
