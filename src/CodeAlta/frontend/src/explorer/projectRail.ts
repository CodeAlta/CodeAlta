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

// A sub-project is ordered by its repository, then its name: it comes right after its repository.
const sortName = (project: WorkspaceProject): string => project.repositoryName && project.subPath ? `${project.repositoryName} › ${project.name}` : project.name;

function compareProjects(a: WorkspaceProject, b: WorkspaceProject): number {
  return compareText(sortName(a), sortName(b)) || compareText(a.id, b.id)
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
  // A sub-project the user hid is archived: it leaves the Explorer, and the window of the sub-projects shows it again.
  // One of an archived repository goes with it, also once it has a file of its own.
  const archived = new Set(snapshot.projects.filter(project => project.archived).map(project => project.id));
  const projects = snapshot.projects.filter(project => !(isSubProject(project) && (project.archived || archived.has(project.parentId!))));
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
  /** How many labels it is shown under: 0 at the top. */
  depth: number;
  /** The labels shown above it in this place, from the top: it is listed in the last one, and their folding hides it. */
  ancestors: readonly string[];
  /** Its projects, in the order of the list. */
  projects: readonly WorkspaceProject[];
  /**
   * The repository whose group this is: its title stands for the repository, its first project is the repository itself
   * (the sessions at its root), then its sub-projects. Absent for a label.
   */
  repository?: WorkspaceProject;
}>;

/** The identifier of the group of a repository among the labels of the Explorer: it folds as a label does. */
export const repositoryGroupId = (projectId: string): string => `repository:${projectId}`;

/** Whether a project is a sub-project of a repository. */
export const isSubProject = (project: WorkspaceProject): boolean => !!project.parentId && !!project.subPath;

/**
 * The name of a project with its repository before it, for the one list and the composer: `contoso › Billing`. A
 * project that is no sub-project, or whose repository is not listed, is named alone.
 */
export function projectTitle(project: WorkspaceProject, projects: readonly WorkspaceProject[] = []): Readonly<{ repository: string | null; name: string }> {
  if (!isSubProject(project)) return { repository: null, name: project.name };
  return { repository: project.repositoryName ?? projects.find(value => value.id === project.parentId)?.name ?? null, name: project.name };
}

/** The projects of the group of a repository: the repository, then its sub-projects. */
export function repositoryProjectIds(repositoryId: string, projects: readonly WorkspaceProject[]): ReadonlySet<string> {
  return new Set([repositoryId, ...projects.filter(project => project.parentId === repositoryId && isSubProject(project)).map(project => project.id)]);
}

/** A part of the Explorer: the favorite projects, the projects of a label, and those of no label. */
export type RailGroup = Readonly<{ kind: "favorites"; projects: readonly WorkspaceProject[] }> | Readonly<{ kind: "other"; projects: readonly WorkspaceProject[] }> | RailLabelGroup;

/**
 * The parts of the Explorer when it lists the projects under their labels: the favorite projects first, then the
 * favorite labels (a favorite sub-label moves up alone, with its own sub-labels, under its path), then the other
 * labels in the order of their tree, then the projects of no label. A project with several labels is listed under
 * each. A label is a branch of the tree: its projects and the labels under it are listed in it. Empty when the space
 * has no label: the Explorer then lists its projects as it does without labels.
 * @param projects The projects listed, in their order, the favorite ones first.
 * @param favorites How many of them, from the first, are favorites.
 * @param labels The labels of the space, in the order of their tree.
 * @param keepEmpty Whether a label without a project listed is shown; not while the list is filtered.
 */
export function projectRailGroups(projects: readonly WorkspaceProject[], favorites: number, allLabels: readonly Label[], keepEmpty = true): readonly RailGroup[] {
  // The labels of the user make the tree; those of the map of a repository nest in the group of that repository.
  const labels = allLabels.filter(label => !label.repository);
  const listed = new Set(projects.map(project => project.id));
  // A repository is a group when one of its sub-projects is listed with it.
  const subProjects = new Map<string, WorkspaceProject[]>();
  for (const project of projects) {
    if (!isSubProject(project) || !listed.has(project.parentId!)) continue;
    subProjects.set(project.parentId!, [...subProjects.get(project.parentId!) ?? [], project]);
  }
  if (!labels.length && !subProjects.size) return [];
  const first = projects.slice(0, favorites), rest = projects.slice(favorites);
  const groups: RailGroup[] = [];
  if (first.length) groups.push({ kind: "favorites", projects: first });
  const repositories = projects.filter(project => subProjects.has(project.id));
  const grouped = new Set(repositories.flatMap(repository => subProjects.get(repository.id)!.map(project => project.id)));
  // Where each repository goes: under the first label of the user it has, in the order of the tree, else at the top.
  const home = new Map<string, string>();
  for (const label of labels) for (const repository of repositories) if (!home.has(repository.id) && label.projectIds.includes(repository.id)) home.set(repository.id, label.id);
  const placedRepositories = new Set<string>();
  const placeRepository = (repository: WorkspaceProject, above: readonly string[]) => {
    if (placedRepositories.has(repository.id)) return;
    placedRepositories.add(repository.id);
    const id = repositoryGroupId(repository.id);
    const members = subProjects.get(repository.id)!;
    const declared = allLabels.filter(label => label.repository === repository.id);
    const declaredIds = new Set(declared.map(label => label.id));
    const ownLabels = (project: WorkspaceProject) => declared.some(label => label.projectIds.includes(project.id));
    const projectIds = [repository.id, ...members.map(project => project.id)];
    const label: Label = { id, name: repository.name, description: repository.path, icon: null, color: null, order: 0, parent: null, favorite: false,
      space: null, projectIds, repository: repository.id };
    groups.push({ kind: "label", label, path: [], depth: above.length, ancestors: above, repository,
      projects: [repository, ...members.filter(project => !ownLabels(project))] });
    const inner = [...above, id];
    // A label of the map is shown while it, or a label under it, has a sub-project listed.
    const used = (value: Label, depth = 0): boolean => depth <= declared.length
      && (members.some(project => value.projectIds.includes(project.id)) || declared.some(child => child.parent === value.id && used(child, depth + 1)));
    const placeDeclared = (value: Label, depth: number, path: readonly string[]) => {
      if (depth > declared.length || !used(value)) return;
      groups.push({ kind: "label", label: value, path: [], depth: path.length, ancestors: path,
        projects: members.filter(project => value.projectIds.includes(project.id)) });
      for (const child of declared) if (child.parent === value.id) placeDeclared(child, depth + 1, [...path, value.id]);
    };
    for (const value of declared) if (!value.parent || !declaredIds.has(value.parent)) placeDeclared(value, 0, inner);
  };
  const placed = new Set<string>();
  const place = (label: Label, above: readonly string[], detached: boolean, skipFavorites: boolean) => {
    if (placed.has(label.id) || above.length >= labels.length) return;
    placed.add(label.id);
    groups.push({ kind: "label", label, path: detached ? labelAncestors(label, labels) : [], depth: above.length, ancestors: above,
      projects: rest.filter(project => label.projectIds.includes(project.id) && home.get(project.id) !== label.id) });
    for (const repository of repositories) if (home.get(repository.id) === label.id) placeRepository(repository, [...above, label.id]);
    for (const child of labels) if (child.parent === label.id && !(skipFavorites && child.favorite)) place(child, [...above, label.id], false, skipFavorites);
  };
  // A favorite label under a favorite one stays in it.
  for (const label of labels) if (label.favorite && !labelAncestors(label, labels).some(above => above.favorite)) place(label, [], label.parent !== null, false);
  // The repositories that no label of the user holds come after the favorite labels.
  for (const repository of repositories) if (!home.has(repository.id)) placeRepository(repository, []);
  for (const label of labels) if (label.parent === null && !label.favorite) place(label, [], false, true);
  // A repository whose label is in no group shown here (a favorite sub-label moved up) is listed at the top.
  for (const repository of repositories) placeRepository(repository, []);
  const labeled = new Set(labels.flatMap(label => label.projectIds));
  const other = rest.filter(project => !labeled.has(project.id) && !grouped.has(project.id) && !subProjects.has(project.id));
  if (other.length) groups.push({ kind: "other", projects: other });
  if (keepEmpty) return groups;
  // A label is left out when neither it nor a label under it lists a project.
  const kept = new Set<string>();
  for (const group of groups) if (group.kind === "label" && group.projects.length) { kept.add(group.label.id); for (const id of group.ancestors) kept.add(id); }
  return groups.filter(group => group.kind !== "label" || kept.has(group.label.id));
}
