import type { SpaceItem, SpaceSessionActivity, WorkspaceProject, WorkspaceSnapshot } from "#neoastra";
import { isBrandIcon } from "../BrandIcon";
import { brandColor, type Brand } from "../brands";
import { isSymbolIcon } from "../symbolIcons";

/**
 * A space: a named group of projects the user works on together. The window shows one space at a time. The
 * default space holds every project; a project can be in several spaces.
 */
export type Space = Readonly<{
  /** Its identifier, worked out from its first name and kept when it is renamed. */
  id: string;
  name: string;
  /** What it is for, for the user and for the agents. */
  description: string | null;
  /** The name of its icon: a general icon or the logo of a brand; null for the usual one. */
  icon: string | null;
  color: string | null;
  isDefault: boolean;
  /** The projects in it, archived ones included: every project for the default space. */
  projectIds: readonly string[];
  /** The path of its file; null for a default space that was never described. */
  file: string | null;
}>;

/** The identifier of the default space, which holds every project and is always there. */
export const defaultSpaceId = "default";
/** Where the page keeps the space it shows. */
export const shownSpaceKey = "codealta.desktop.space.v1";
/** The longest name of a space. */
export const maximumSpaceName = 64;
/** The longest description of a space. */
export const maximumSpaceDescription = 2000;

const text = (value: unknown, maximum: number): value is string => typeof value === "string" && value.length > 0 && value.length <= maximum;
const identifier = (value: unknown): value is string => typeof value === "string" && /^[a-z0-9][a-z0-9\-_.]{1,63}$/.test(value);

/** The default space of a window whose host lists none: it holds every project. */
export const defaultSpace: Space = Object.freeze({ id: defaultSpaceId, name: "Default", description: null, icon: null, color: null, isDefault: true,
  projectIds: Object.freeze([]) as readonly string[], file: null });

/**
 * The spaces as the host lists them: the default one first, each once. What is not a space is left out, and a
 * list without a default space gets one.
 */
export function readSpaces(items: readonly SpaceItem[] | null | undefined): readonly Space[] {
  const spaces: Space[] = [];
  const seen = new Set<string>();
  for (const item of Array.isArray(items) ? items.slice(0, 64) : []) {
    if (!item || !identifier(item.id) || seen.has(item.id) || !text(item.name, 256)) continue;
    seen.add(item.id);
    spaces.push({ id: item.id, name: item.name, description: text(item.description, 4096) ? item.description : null,
      icon: text(item.icon, 64) ? item.icon : null, color: brandColor(item.color) ?? null, isDefault: item.id === defaultSpaceId,
      projectIds: Array.isArray(item.projectIds) ? item.projectIds.filter((id: unknown) => text(id, 256)) : [], file: text(item.file, 4096) ? item.file : null });
  }
  const first = spaces.findIndex(space => space.isDefault);
  if (first < 0) return [defaultSpace, ...spaces];
  return first === 0 ? spaces : [spaces[first], ...spaces.slice(0, first), ...spaces.slice(first + 1)];
}

/** Whether two readings list the same spaces with the same projects: a reading that changes nothing is not shown again. */
export function sameSpaces(left: readonly Space[], right: readonly Space[]): boolean {
  return left === right || left.length === right.length && left.every((space, index) => {
    const other = right[index];
    return space.id === other.id && space.name === other.name && space.description === other.description && space.icon === other.icon
      && space.color === other.color && space.file === other.file && space.projectIds.length === other.projectIds.length
      && space.projectIds.every((id, at) => id === other.projectIds[at]);
  });
}

/** The space with an identifier; the default space for an identifier no space has. */
export function findSpace(spaces: readonly Space[], id: string | null | undefined): Space {
  return spaces.find(space => space.id === id) ?? spaces.find(space => space.isDefault) ?? defaultSpace;
}

/** The projects a space shows: null for the default space, which shows every project. */
export function spaceMembers(spaces: readonly Space[], id: string | null | undefined): ReadonlySet<string> | null {
  const space = findSpace(spaces, id);
  return space.isDefault ? null : new Set(space.projectIds);
}

/** Whether two sets of members are the same projects. */
export function sameMembers(left: ReadonlySet<string> | null, right: ReadonlySet<string> | null): boolean {
  if (left === right) return true;
  if (!left || !right || left.size !== right.size) return false;
  for (const id of left) if (!right.has(id)) return false;
  return true;
}

/**
 * What a space shows of the catalog: its projects with their sessions, and the chats, which belong to no
 * project. A session of a project that is in another space is left out; a session whose project the catalog
 * no longer has stays where it was, with the chats. The default space shows the catalog as it is.
 */
export function scopeSnapshot(snapshot: WorkspaceSnapshot, members: ReadonlySet<string> | null): WorkspaceSnapshot {
  if (!members) return snapshot;
  const projects = snapshot.projects.filter(project => members.has(project.id));
  if (projects.length === snapshot.projects.length) return snapshot;
  const elsewhere = snapshot.projects.filter(project => !members.has(project.id));
  const otherIds = new Map(elsewhere.map(project => [project.id, project.path]));
  const otherPaths = new Set(elsewhere.map(project => project.path));
  const keptPaths = new Set(projects.map(project => project.path));
  const sessions = snapshot.sessions.filter(session => {
    if (session.scopeKind === "global") return true;
    if (session.scopeKind === "project") return !(session.projectId !== null && otherIds.get(session.projectId) === session.workspacePath);
    // A session without a recorded scope is listed by its folder.
    return !session.workspacePath || keptPaths.has(session.workspacePath) || !otherPaths.has(session.workspacePath);
  });
  return { ...snapshot, projects, sessions };
}

/** Where a project that something asks to show is: `space` is null in the shown space, else the space to show for it. */
export type ProjectPlace = Readonly<{ project: WorkspaceProject; space: Space | null }>;

/**
 * Where the project of a request is (an agent asks for its code editor or its changes, a link names one of its
 * files). The window serves the request only for a project the shown space has: for another one it stays where
 * the user is and offers the space of the project, the first of its spaces, else the default one, which has
 * every project. Null for a project the catalog does not have, or an archived one.
 */
export function placeProject(shown: readonly WorkspaceProject[], catalog: readonly WorkspaceProject[], spaces: readonly Space[], shownId: string,
  projectId: string): ProjectPlace | null {
  const here = shown.find(project => project.id === projectId && !project.archived);
  if (here) return { project: here, space: null };
  const project = catalog.find(value => value.id === projectId && !value.archived);
  if (!project) return null;
  const space = spaces.find(candidate => !candidate.isDefault && candidate.id !== shownId && candidate.projectIds.includes(projectId))
    ?? spaces.find(candidate => candidate.isDefault) ?? defaultSpace;
  // The default space has every project: one it is asked for while it is shown is shown there.
  return { project, space: space.id === findSpace(spaces, shownId).id ? null : space };
}

/** The key the tabs of a space are kept under: the default space keeps the key the window always had. */
export function spaceStorageKey(base: string, spaceId: string): string {
  return spaceId === defaultSpaceId ? base : `${base}.${spaceId}`;
}

/** The space the page showed last; the default space when none is recorded. */
export function restoreShownSpace(read: () => string | null): string {
  try {
    const value = read();
    return identifier(value) ? value : defaultSpaceId;
  } catch { return defaultSpaceId; }
}

/** Records the space the page shows. */
export function persistShownSpace(write: (value: string) => void, id: string): boolean {
  try { write(id); return true; } catch { return false; }
}

/** The space before or after another one, around the list. */
export function neighborSpace(spaces: readonly Space[], id: string, delta: 1 | -1): Space {
  const at = Math.max(0, spaces.findIndex(space => space.id === id));
  return spaces[(at + delta + spaces.length) % spaces.length] ?? defaultSpace;
}

/** What the icon of a space is drawn with: the logo of a brand or a general icon, in its color. */
export function spaceBrand(space: Readonly<{ icon: string | null; color: string | null }>): Brand {
  const color = brandColor(space.color);
  if (isBrandIcon(space.icon)) return { icon: space.icon, color };
  return { icon: null, symbol: isSymbolIcon(space.icon) ? space.icon : undefined, color };
}

/** What the sessions of a space are doing. */
export type SpaceActivity = Readonly<{
  /** The sessions that run. */
  running: number;
  /** The sessions whose provider works in the background without a run. */
  background: number;
  /** The sessions that wait for the user: a question, a command to review, a form. */
  waiting: number;
  /** The sessions whose last run failed. */
  failed: number;
  /** The session to open first for this space: one that waits, else one that failed. */
  attention: Readonly<{ sessionId: string; projectId: string | null; title: string }> | null;
}>;

const quiet: SpaceActivity = Object.freeze({ running: 0, background: 0, waiting: 0, failed: 0, attention: null });

/** Whether nothing happens in a space. */
export const spaceQuiet = (activity: SpaceActivity | undefined) => !activity || activity.running + activity.background + activity.waiting + activity.failed === 0;

/** What one session is doing, as the host says it; null for what is not such a reading. */
export function readSessionActivity(value: SpaceSessionActivity | null | undefined): SpaceSessionActivity | null {
  if (!value || !text(value.sessionId, 256)) return null;
  return { sessionId: value.sessionId, projectId: text(value.projectId, 256) ? value.projectId : null, title: typeof value.title === "string" ? value.title.slice(0, 256) : "",
    running: value.running === true, backgroundTasks: Number.isInteger(value.backgroundTasks) && value.backgroundTasks > 0 ? Math.min(value.backgroundTasks, 99) : 0,
    failed: value.failed === true, waiting: value.waiting === true };
}

/**
 * What each space is doing, from what the sessions do: a session counts for every space its project is in,
 * and a chat, which belongs to no project, for the default space alone.
 */
export function spaceActivities(spaces: readonly Space[], sessions: readonly SpaceSessionActivity[]): ReadonlyMap<string, SpaceActivity> {
  const result = new Map<string, SpaceActivity>();
  for (const space of spaces) {
    const members = space.isDefault ? null : new Set(space.projectIds);
    let running = 0, background = 0, waiting = 0, failed = 0;
    let attention: SpaceActivity["attention"] = null, attentionWaits = false;
    for (const session of sessions) {
      if (members && (session.projectId === null || !members.has(session.projectId))) continue;
      if (session.running) running++; else if (session.backgroundTasks > 0) background++;
      if (session.waiting) waiting++;
      if (session.failed) failed++;
      if ((session.waiting || session.failed) && (!attention || session.waiting && !attentionWaits)) {
        attention = { sessionId: session.sessionId, projectId: session.projectId, title: session.title };
        attentionWaits = session.waiting;
      }
    }
    result.set(space.id, running + background + waiting + failed === 0 ? quiet : { running, background, waiting, failed, attention });
  }
  return result;
}

/** The spaces a new one is offered as: a name with an icon and a color, one click away. */
export const spaceTemplates: readonly Readonly<{ name: string; icon: string; color: string }>[] = Object.freeze([
  { name: "Work", icon: "briefcase", color: "#2d72d2" },
  { name: "Personal", icon: "house", color: "#238551" },
  { name: "Open source", icon: "globe", color: "#9d3f9d" },
  { name: "Experiments", icon: "flask-conical", color: "#c87619" },
  { name: "Learning", icon: "graduation-cap", color: "#00a396" },
  { name: "Clients", icon: "building-2", color: "#cd4246" },
]);

/** The colors a space is offered. */
export const spaceColors: readonly string[] = Object.freeze(["#2d72d2", "#238551", "#c87619", "#cd4246", "#9d3f9d", "#00a396", "#d1980b", "#7961db", "#738091"]);

/** What a name cannot be: blank, too long, or already the name of another space. Null for a name that can be given. */
export function spaceNameProblem(name: string, spaces: readonly Space[], ownId: string | null): "empty" | "long" | "taken" | null {
  const wanted = name.trim();
  if (!wanted) return "empty";
  if (wanted.length > maximumSpaceName) return "long";
  return spaces.some(space => space.id !== ownId && space.name.toLowerCase() === wanted.toLowerCase()) ? "taken" : null;
}

/** Whether the space that is shown has a session of a project: its project is in it, or it is a chat, which every space shows. */
export function spaceShows(spaces: readonly Space[], shownId: string, projectId: string | null): boolean {
  const shown = findSpace(spaces, shownId);
  return shown.isDefault || projectId === null || shown.projectIds.includes(projectId);
}

/** A session that needs the user in a space other than the one shown. */
export type SpaceCall = Readonly<{ space: Space; sessionId: string; projectId: string | null; title: string; waiting: boolean }>;

/**
 * The sessions that need the user where the user is not looking: one for each space other than the one shown,
 * a session that waits before one that failed. A session the shown space has (its project is in it, or it is a
 * chat, which every space shows) is not one of them: the Explorer and the tabs say it already. A session is
 * named for the first space of its project, and for the default space when its project is in no other.
 */
export function spaceCalls(spaces: readonly Space[], shownId: string, sessions: readonly SpaceSessionActivity[]): readonly SpaceCall[] {
  const shown = findSpace(spaces, shownId);
  if (shown.isDefault) return [];
  const visible = new Set(shown.projectIds);
  const fallback = spaces.find(space => space.isDefault) ?? defaultSpace;
  const calls = new Map<string, SpaceCall>();
  for (const session of sessions) {
    if (!session.waiting && !session.failed || session.projectId === null || visible.has(session.projectId)) continue;
    const space = spaces.find(candidate => !candidate.isDefault && candidate.projectIds.includes(session.projectId!)) ?? fallback;
    const known = calls.get(space.id);
    if (!known || session.waiting && !known.waiting)
      calls.set(space.id, { space, sessionId: session.sessionId, projectId: session.projectId, title: session.title, waiting: session.waiting });
  }
  return spaces.flatMap(space => calls.get(space.id) ?? []).sort((left, right) => Number(right.waiting) - Number(left.waiting));
}

/** Whether two readings say the same of the same sessions. */
export function sameSessions(left: readonly SpaceSessionActivity[], right: readonly SpaceSessionActivity[]): boolean {
  return left === right || left.length === right.length && left.every((session, index) => {
    const other = right[index];
    return session.sessionId === other.sessionId && session.projectId === other.projectId && session.title === other.title && session.running === other.running
      && session.backgroundTasks === other.backgroundTasks && session.failed === other.failed && session.waiting === other.waiting;
  });
}
