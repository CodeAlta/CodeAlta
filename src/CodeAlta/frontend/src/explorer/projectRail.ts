import type { WorkspaceProject, WorkspaceSnapshot } from "#neoastra";

export type ProjectSort = "name" | "recent";
export const projectSortStorageKey = "codealta.desktop.projectSort.v1";

export function restoreProjectSort(read: () => string | null): ProjectSort {
  try { return read() === "recent" ? "recent" : "name"; }
  catch { return "name"; }
}

export function persistProjectSort(write: (value: string) => void, sort: ProjectSort): boolean {
  try { write(sort); return true; }
  catch { return false; }
}

function compareText(a: string, b: string): number {
  const left = a.toLowerCase(); const right = b.toLowerCase();
  return left < right ? -1 : left > right ? 1 : 0;
}

function compareProjects(a: WorkspaceProject, b: WorkspaceProject): number {
  return compareText(a.name, b.name) || compareText(a.id, b.id)
    || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0);
}

// Only canonical offset-bearing timestamps can establish visible saved activity.
function activityTime(value: string): number | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$/.exec(value);
  if (!match) return null;
  const [year, month, day, hour, minute, second] = match.slice(1).map(Number);
  const leap = year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
  const days = [31, leap ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
  if (year < 2 || month < 1 || month > 12 || day < 1 || day > days[month - 1]
    || hour > 23 || minute > 59 || second > 59) return null;
  const timestamp = Date.parse(value);
  return Number.isFinite(timestamp) ? timestamp : null;
}

/**
 * The projects the Explorer lists: those the filter keeps, in the chosen order, the favorite ones first.
 * `favorites` in the result is how many of them, from the first, are favorites.
 */
export function projectRailProjection(snapshot: WorkspaceSnapshot, filter: string, sort: ProjectSort, favorites: readonly string[] = []): {
  projects: WorkspaceProject[]; favorites: number; evidenceNotice: string | null;
} {
  const ordered = orderedProjects(snapshot, filter, sort);
  if (!favorites.length) return { ...ordered, favorites: 0 };
  const chosen = new Set(favorites);
  const first = ordered.projects.filter(project => chosen.has(project.id));
  return { projects: [...first, ...ordered.projects.filter(project => !chosen.has(project.id))], favorites: first.length, evidenceNotice: ordered.evidenceNotice };
}

function orderedProjects(snapshot: WorkspaceSnapshot, filter: string, sort: ProjectSort): { projects: WorkspaceProject[]; evidenceNotice: string | null } {
  const term = filter.trim().toLowerCase();
  const projects = snapshot.projects.filter(project => !term
    || project.name.toLowerCase().includes(term) || project.path.toLowerCase().includes(term));
  if (sort === "name") return { projects: projects.sort(compareProjects), evidenceNotice: null };

  const idCounts = new Map<string, number>();
  for (const session of snapshot.sessions) idCounts.set(session.id, (idCounts.get(session.id) ?? 0) + 1);
  const byProject = new Map<string, number>();
  for (const session of snapshot.sessions) {
    if (session.scopeKind !== "project" || !session.id?.trim() || !session.projectId || !session.workspacePath
      || idCounts.get(session.id) !== 1) continue;
    const project = snapshot.projects.find(project => project.id === session.projectId && !!project.path && project.path === session.workspacePath);
    if (!project) continue;
    const time = activityTime(session.updatedAt);
    if (time !== null) byProject.set(project.id, Math.max(byProject.get(project.id) ?? -Infinity, time));
  }
  projects.sort((a, b) => (byProject.has(b.id) ? 1 : 0) - (byProject.has(a.id) ? 1 : 0)
    || (byProject.get(b.id) ?? 0) - (byProject.get(a.id) ?? 0) || compareProjects(a, b));
  const unknown = projects.filter(project => !byProject.has(project.id)).length;
  const evidenceNotice = `${unknown < projects.length ? "Recent order uses only verified visible saved session updates" : "No verified visible session updates; name order used"}. `
    + `${unknown} shown project(s) have no dated evidence and follow name/ID order. `
    + (snapshot.sessionsTruncated || snapshot.projectsTruncated
      ? "Snapshot is truncated; recent order may be incomplete. " : "")
    + "Unverified, omitted and invalid-date sessions cannot establish recency; this is not the TUI's complete last-active order.";
  return { projects, evidenceNotice };
}
