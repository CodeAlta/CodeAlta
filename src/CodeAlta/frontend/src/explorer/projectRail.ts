import type { WorkspaceProject, WorkspaceSnapshot } from "#neoastra";
import { labelAncestors, type Label } from "../labels/labels";

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
 * The projects the Explorer lists, in the chosen order, the favorite ones first. `favorites` in the result is
 * how many of them, from the first, are favorites. A project is looked for in the search of the window.
 */
export function projectRailProjection(snapshot: WorkspaceSnapshot, sort: ProjectSort, favorites: readonly string[] = []): {
  projects: WorkspaceProject[]; favorites: number; evidenceNotice: string | null;
} {
  const ordered = orderedProjects(snapshot, sort);
  if (!favorites.length) return { ...ordered, favorites: 0 };
  const chosen = new Set(favorites);
  const first = ordered.projects.filter(project => chosen.has(project.id));
  return { projects: [...first, ...ordered.projects.filter(project => !chosen.has(project.id))], favorites: first.length, evidenceNotice: ordered.evidenceNotice };
}

function orderedProjects(snapshot: WorkspaceSnapshot, sort: ProjectSort): { projects: WorkspaceProject[]; evidenceNotice: string | null } {
  const projects = [...snapshot.projects];
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

/** Whether the Explorer lists the projects under their labels, or in one list with their labels as badges. */
export type ProjectGrouping = "labels" | "flat";
export const projectGroupingStorageKey = "codealta.desktop.projectGrouping.v1";

/** A label of the Explorer, as the title of the projects that have it. */
export type RailLabelGroup = Readonly<{
  kind: "label";
  label: Label;
  /** The labels above it that its title names, dimmed: those of a label shown away from its parent. */
  path: readonly Label[];
  /** How far its title steps in: 0 at the top, 1 under a label shown above it (deeper labels step in no further). */
  depth: 0 | 1;
  /** The labels shown above it in this place, whose folding hides it. */
  ancestors: readonly string[];
  /** Its projects, in the order of the list. */
  projects: readonly WorkspaceProject[];
}>;

/** A part of the Explorer: the favorite projects, the projects of a label, and those of no label. */
export type RailGroup = Readonly<{ kind: "favorites"; projects: readonly WorkspaceProject[] }> | Readonly<{ kind: "other"; projects: readonly WorkspaceProject[] }> | RailLabelGroup;

/**
 * The parts of the Explorer when it lists the projects under their labels: the favorite projects first, then the
 * favorite labels (a favorite sub-label moves up alone, with its own sub-labels, under its path), then the other
 * labels in the order of their tree, then the projects of no label. A project with several labels is listed under
 * each. Labels are titles, never parents of rows: a sub-label steps in once, whatever its depth. Empty when the space
 * has no label: the Explorer then lists its projects as it does without labels.
 * @param projects The projects listed, in their order, the favorite ones first.
 * @param favorites How many of them, from the first, are favorites.
 * @param labels The labels of the space, in the order of their tree.
 * @param keepEmpty Whether a label without a project listed is shown; not while the list is filtered.
 */
export function projectRailGroups(projects: readonly WorkspaceProject[], favorites: number, labels: readonly Label[], keepEmpty = true): readonly RailGroup[] {
  if (!labels.length) return [];
  const first = projects.slice(0, favorites), rest = projects.slice(favorites);
  const groups: RailGroup[] = [];
  if (first.length) groups.push({ kind: "favorites", projects: first });
  const placed = new Set<string>();
  const place = (label: Label, above: readonly string[], detached: boolean, skipFavorites: boolean) => {
    if (placed.has(label.id) || above.length >= labels.length) return;
    placed.add(label.id);
    groups.push({ kind: "label", label, path: detached ? labelAncestors(label, labels) : [], depth: above.length ? 1 : 0, ancestors: above,
      projects: rest.filter(project => label.projectIds.includes(project.id)) });
    for (const child of labels) if (child.parent === label.id && !(skipFavorites && child.favorite)) place(child, [...above, label.id], false, skipFavorites);
  };
  // A favorite label under a favorite one stays in it.
  for (const label of labels) if (label.favorite && !labelAncestors(label, labels).some(above => above.favorite)) place(label, [], label.parent !== null, false);
  for (const label of labels) if (label.parent === null && !label.favorite) place(label, [], false, true);
  const labeled = new Set(labels.flatMap(label => label.projectIds));
  const other = rest.filter(project => !labeled.has(project.id));
  if (other.length) groups.push({ kind: "other", projects: other });
  if (keepEmpty) return groups;
  // A label is left out when neither it nor a label under it lists a project.
  const kept = new Set<string>();
  for (const group of groups) if (group.kind === "label" && group.projects.length) { kept.add(group.label.id); for (const id of group.ancestors) kept.add(id); }
  return groups.filter(group => group.kind !== "label" || kept.has(group.label.id));
}

/** Whether the title of a label is hidden: a label shown above it in the same place is folded. */
export const railGroupHidden = (group: RailLabelGroup, folded: (id: string) => boolean): boolean => group.ancestors.some(folded);
