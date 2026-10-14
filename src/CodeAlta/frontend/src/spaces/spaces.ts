import type { SpaceAssignmentsResponse, SpaceItem, SpaceSessionActivity, WorkspaceProject, WorkspaceSnapshot } from "#neoastra";
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
  /** The agent prompt its new sessions start with when none is chosen; null for the default one. */
  agentPrompt: string | null;
  /** The built-in tools its sessions do without. */
  disabledTools: readonly string[];
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
  projectIds: Object.freeze([]) as readonly string[], file: null, agentPrompt: null, disabledTools: Object.freeze([]) as readonly string[] });

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
      projectIds: Array.isArray(item.projectIds) ? item.projectIds.filter((id: unknown) => text(id, 256)) : [], file: text(item.file, 4096) ? item.file : null,
      agentPrompt: text(item.agentPrompt, 128) ? item.agentPrompt : null,
      disabledTools: toolNames(item.disabledTools) });
  }
  const first = spaces.findIndex(space => space.isDefault);
  if (first < 0) return [defaultSpace, ...spaces];
  return first === 0 ? spaces : [spaces[first], ...spaces.slice(0, first), ...spaces.slice(first + 1)];
}

// The names of tools a space turns off, each once.
function toolNames(value: unknown): readonly string[] {
  const names: string[] = [];
  for (const name of Array.isArray(value) ? (value as readonly unknown[]).slice(0, 64) : []) if (text(name, 128) && !names.includes(name)) names.push(name);
  return names;
}

/** Whether two readings list the same spaces with the same projects: a reading that changes nothing is not shown again. */
export function sameSpaces(left: readonly Space[], right: readonly Space[]): boolean {
  return left === right || left.length === right.length && left.every((space, index) => {
    const other = right[index];
    return space.id === other.id && space.name === other.name && space.description === other.description && space.icon === other.icon
      && space.color === other.color && space.file === other.file && space.agentPrompt === other.agentPrompt && space.disabledTools.join("\n") === other.disabledTools.join("\n")
      && space.projectIds.length === other.projectIds.length
      && space.projectIds.every((id, at) => id === other.projectIds[at]);
  });
}

/** The kinds of resource that are assigned to spaces, as the host names them. */
export type SpaceResourceKind = "provider" | "mcp" | "plugin" | "skill" | "prompt";
/** The spaces of the resources that list some, by kind and by key without case. A resource that is not there is in every space. */
export type ResourceAssignments = Readonly<Record<SpaceResourceKind, ReadonlyMap<string, readonly string[]>>>;
const noMap: ReadonlyMap<string, readonly string[]> = new Map();
/** Assignments that list nothing: every resource is in every space. */
export const noAssignments: ResourceAssignments = Object.freeze({ provider: noMap, mcp: noMap, plugin: noMap, skill: noMap, prompt: noMap });

/** The assignments as the host lists them; what is not one is left out. */
export function readAssignments(reply: SpaceAssignmentsResponse | null | undefined): ResourceAssignments {
  if (!reply || reply.status !== "ok") return noAssignments;
  const read = (items: SpaceAssignmentsResponse["mcpServers"] | null | undefined): ReadonlyMap<string, readonly string[]> => {
    const map = new Map<string, readonly string[]>();
    for (const item of Array.isArray(items) ? items.slice(0, 512) : []) {
      if (!item || !text(item.key, 256) || !Array.isArray(item.spaces)) continue;
      const spaces: string[] = [];
      for (const id of item.spaces as readonly unknown[]) if (identifier(id) && !spaces.includes(id)) spaces.push(id);
      if (spaces.length > 0) map.set(item.key.toLowerCase(), spaces);
    }
    return map;
  };
  return { provider: read(reply.providers), mcp: read(reply.mcpServers), plugin: read(reply.plugins), skill: read(reply.skills), prompt: read(reply.prompts) };
}

/** The spaces a resource is assigned to; empty when it is in every space. */
export function resourceSpaces(assignments: ResourceAssignments, kind: SpaceResourceKind, key: string): readonly string[] {
  return assignments[kind].get(key.toLowerCase()) ?? [];
}

/** Whether a resource is in a space: one that lists no space is in every space, the default space only when it is listed. */
export function inSpace(spaces: readonly string[], spaceId: string): boolean {
  return spaces.length === 0 || spaces.includes(spaceId);
}

/** The spaces of a resource once it is made available in one more space; a shared resource stays shared. */
export function spacesWith(spaces: readonly string[], spaceId: string): readonly string[] {
  return spaces.length === 0 || spaces.includes(spaceId) ? spaces : [...spaces, spaceId];
}

/**
 * The spaces of a resource once it leaves one: a shared resource is then listed for every other space, the default
 * one included, so a space created later does not get it. Null when it would be in no space, which no list can say.
 */
export function spacesWithout(spaces: readonly string[], spaceId: string, every: readonly string[]): readonly string[] | null {
  const kept = [...new Set((spaces.length === 0 ? every : spaces).filter(id => id !== spaceId))];
  return kept.length === 0 ? null : kept;
}

/** The space with an identifier; the default space for an identifier no space has. */
export function findSpace(spaces: readonly Space[], id: string | null | undefined): Space {
  return spaces.find(space => space.id === id) ?? spaces.find(space => space.isDefault) ?? defaultSpace;
}

/** Where the page keeps whether the default space shows what belongs to other spaces too. */
export const showOthersKey = "codealta.desktop.space.showOthers.v1";
/** Where the page keeps that it said once what the default space shows now. */
export const toldDefaultKey = "codealta.desktop.space.toldDefault.v1";

/**
 * What one space shows of the catalog. The default space shows the projects that are in no other space and the
 * sessions of the default space, unless it is asked to show what belongs to the other spaces too (`showOthers`):
 * then it shows the whole catalog.
 */
export type SpaceView = Readonly<{
  space: Space;
  /** The projects it shows; null for every project. */
  projects: Readonly<{ has: (projectId: string) => boolean }> | null;
  /** Whether it shows every session, whatever space it belongs to. */
  everything: boolean;
  spaces: readonly Space[];
}>;

/** What a space shows: see {@link SpaceView}. */
export function spaceView(spaces: readonly Space[], id: string | null | undefined, showOthers: boolean): SpaceView {
  const space = findSpace(spaces, id);
  if (space.isDefault && (showOthers || spaces.every(other => other.isDefault))) return { space, projects: null, everything: true, spaces };
  if (!space.isDefault) return { space, projects: new Set(space.projectIds), everything: false, spaces };
  const elsewhere = new Set(spaces.filter(other => !other.isDefault).flatMap(other => other.projectIds));
  return { space, projects: elsewhere.size === 0 ? null : { has: (projectId: string) => !elsewhere.has(projectId) }, everything: false, spaces };
}

/** A text that changes when what a view shows does: the window is given the catalog again when it changes. */
export function spaceViewKey(view: SpaceView): string {
  return JSON.stringify([view.space.id, view.everything, view.spaces.map(space => [space.id, space.projectIds])]);
}

/**
 * The spaces a session is listed in. A session that records a space is in that space alone, the default one for a
 * space that no longer exists. A session created before sessions had a space is in every space of its project, and
 * in the default space when its project is in no other; such a chat is in the default space.
 */
export function sessionSpaces(spaces: readonly Space[], session: Readonly<{ spaceId: string | null; projectId: string | null }>): readonly string[] {
  if (session.spaceId) return [spaces.some(space => space.id === session.spaceId) ? session.spaceId : defaultSpaceId];
  if (session.projectId === null) return [defaultSpaceId];
  const own = spaces.filter(space => !space.isDefault && space.projectIds.includes(session.projectId!)).map(space => space.id);
  return own.length === 0 ? [defaultSpaceId] : own;
}

/** The space a session is shown in first: the one it records, else the first of the spaces it is listed in. */
export function sessionHome(spaces: readonly Space[], session: Readonly<{ spaceId: string | null; projectId: string | null }>): Space {
  return findSpace(spaces, sessionSpaces(spaces, session)[0]);
}

/** Whether a view lists a session: its project is shown there (or it is a chat), and the session belongs to that space. */
export function viewLists(view: SpaceView, session: Readonly<{ spaceId: string | null; projectId: string | null }>): boolean {
  if (view.everything) return true;
  if (session.projectId !== null && view.projects && !view.projects.has(session.projectId)) return false;
  return sessionSpaces(view.spaces, session).includes(view.space.id);
}

/**
 * What a space shows of the catalog: its projects with their sessions, and its chats. A chat, and a session of a
 * project, is in its own space; one created before sessions had a space is in every space of its project, such a
 * chat in the default space. The default space shows the projects that are in no other space, unless it shows
 * everything. A session whose project the catalog no longer has stays where it was, with the chats.
 */
export function scopeSnapshot(snapshot: WorkspaceSnapshot, view: SpaceView): WorkspaceSnapshot {
  if (view.everything) return snapshot;
  const members = view.projects;
  const projects = members ? snapshot.projects.filter(project => members.has(project.id)) : snapshot.projects;
  const elsewhere = members ? snapshot.projects.filter(project => !members.has(project.id)) : [];
  const otherIds = new Map(elsewhere.map(project => [project.id, project.path]));
  const otherPaths = new Set(elsewhere.map(project => project.path));
  const keptPaths = new Set(projects.map(project => project.path));
  const sessions = snapshot.sessions.filter(session => {
    if (session.scopeKind === "global") return sessionSpaces(view.spaces, { spaceId: session.spaceId, projectId: null }).includes(view.space.id);
    if (session.scopeKind === "project") {
      if (session.projectId !== null && otherIds.get(session.projectId) === session.workspacePath) return false;
      return sessionSpaces(view.spaces, { spaceId: session.spaceId, projectId: session.projectId }).includes(view.space.id);
    }
    // A session without a recorded scope is listed by its folder, and one in no folder of a project as a chat.
    if (session.workspacePath && keptPaths.has(session.workspacePath)) return true;
    if (session.workspacePath && otherPaths.has(session.workspacePath)) return false;
    return sessionSpaces(view.spaces, { spaceId: session.spaceId, projectId: null }).includes(view.space.id);
  });
  return projects.length === snapshot.projects.length && sessions.length === snapshot.sessions.length ? snapshot : { ...snapshot, projects, sessions };
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
    failed: value.failed === true, waiting: value.waiting === true,
    // The sessions above one that waits, nearest first: the session that created it, and so on.
    ancestors: value.waiting === true && Array.isArray(value.ancestors) ? value.ancestors.filter(id => text(id, 256)).slice(0, maximumAncestors) : [],
    // The space the session records; null for one created before sessions had a space.
    spaceId: text(value.spaceId, 64) ? value.spaceId : null,
    // The model it went on with after a failure of its provider, a short while ago.
    fallback: value.fallback && text(value.fallback.at, 64) && typeof value.fallback.message === "string"
      ? { at: value.fallback.at, message: value.fallback.message.slice(0, 512) } : null };
}

/** A session that waits for the user, as the session above it shows it. */
export type WaitingSubSession = Readonly<{ sessionId: string; title: string }>;

/**
 * The sessions that wait for the user under each session: its children, theirs, and so on, the nearest first, a
 * session that waits under itself left out.
 */
export function waitingSubSessions(sessions: readonly SpaceSessionActivity[]): ReadonlyMap<string, readonly WaitingSubSession[]> {
  const below = new Map<string, { depth: number; session: WaitingSubSession }[]>();
  for (const session of sessions) {
    if (!session.waiting) continue;
    session.ancestors.forEach((ancestor, depth) => {
      if (ancestor === session.sessionId) return;
      const list = below.get(ancestor) ?? [];
      if (!list.some(item => item.session.sessionId === session.sessionId)) list.push({ depth, session: { sessionId: session.sessionId, title: session.title } });
      below.set(ancestor, list);
    });
  }
  return new Map([...below].map(([ancestor, list]) => [ancestor, list.sort((left, right) => left.depth - right.depth).map(item => item.session)]));
}

/** How many sessions above a waiting one are read, as many as the host names. */
const maximumAncestors = 16;

/**
 * What each space is doing, from what the sessions do: a session counts for the spaces it is listed in (its own
 * space; for one created before sessions had a space, every space of its project).
 */
export function spaceActivities(spaces: readonly Space[], sessions: readonly SpaceSessionActivity[]): ReadonlyMap<string, SpaceActivity> {
  const result = new Map<string, SpaceActivity>();
  const listed = new Map(sessions.map(session => [session.sessionId, sessionSpaces(spaces, session)]));
  for (const space of spaces) {
    let running = 0, background = 0, waiting = 0, failed = 0;
    let attention: SpaceActivity["attention"] = null, attentionWaits = false;
    for (const session of sessions) {
      if (!listed.get(session.sessionId)!.includes(space.id)) continue;
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
 * a session that waits before one that failed. A session the shown space lists is not one of them: the Explorer
 * and the tabs say it already. A session is named for its own space (see {@link sessionHome}).
 */
export function spaceCalls(spaces: readonly Space[], shownId: string, sessions: readonly SpaceSessionActivity[], showOthers = false): readonly SpaceCall[] {
  const view = spaceView(spaces, shownId, showOthers);
  if (view.everything) return [];
  const calls = new Map<string, SpaceCall>();
  for (const session of sessions) {
    if (!session.waiting && !session.failed || viewLists(view, session)) continue;
    const space = sessionHome(spaces, session);
    if (space.id === view.space.id) continue;
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
    return session.sessionId === other.sessionId && session.projectId === other.projectId && session.spaceId === other.spaceId && session.title === other.title && session.running === other.running
      && session.backgroundTasks === other.backgroundTasks && session.failed === other.failed && session.waiting === other.waiting
      && session.fallback?.at === other.fallback?.at
      && session.ancestors.length === other.ancestors.length && session.ancestors.every((id, at) => id === other.ancestors[at]);
  });
}

/** How many chats record each space: the chats deleting a space moves to the default space. */
export function spaceChatCounts(sessions: readonly Readonly<{ scopeKind: string | null; spaceId: string | null }>[]): ReadonlyMap<string, number> {
  const counts = new Map<string, number>();
  for (const session of sessions) {
    if (session.scopeKind === "global" && session.spaceId) counts.set(session.spaceId, (counts.get(session.spaceId) ?? 0) + 1);
  }
  return counts;
}
