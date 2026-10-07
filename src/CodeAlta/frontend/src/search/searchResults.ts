import type { WorkspaceProject, WorkspaceSession, WorkspaceSnapshot } from "#neoastra";
import { plainTitle } from "../sessionTitle";

/**
 * What the search of the window looks through, and in which order it shows what it found. One window searches
 * the projects, the sessions of every project and the chats, the files of the project in front, and the commands.
 */
export type SearchCategory = "all" | "sessions" | "projects" | "files" | "commands";
export const searchCategories: readonly SearchCategory[] = ["all", "sessions", "projects", "files", "commands"];
/** The groups of the results, in the order they are shown when their best matches are as good. */
export type SearchGroup = Exclude<SearchCategory, "all">;
export const searchGroups: readonly SearchGroup[] = ["sessions", "projects", "files", "commands"];

/** How many results of a group are shown when every category is searched, and at most when one is. */
export const groupPreview: Readonly<Record<SearchGroup, number>> = { projects: 5, sessions: 7, files: 6, commands: 6 };
export const categoryLimit = 200;
/** The commands that start something, by their slash name: they come first when nothing is typed. */
export const suggestedCommands: readonly string[] = ["new_session", "open", "terminal", "editor", "settings", "help"];

/**
 * What was typed, as the search reads it: a text that starts with `/` looks for a command, as in the prompt, and
 * the words are compared without their case.
 */
export function readQuery(text: string): Readonly<{ words: readonly string[]; commands: boolean }> {
  const trimmed = text.trim();
  const commands = trimmed.startsWith("/");
  return { commands, words: (commands ? trimmed.slice(1) : trimmed).toLowerCase().split(/\s+/u).filter(Boolean) };
}

/**
 * How well a word is found in a text, the ranking of the command palette: the whole text, then its start, then
 * the start of one of its words, then anywhere in it. Null when the word is not in the text.
 */
export function wordScore(text: string, word: string): number | null {
  const value = text.toLowerCase();
  if (value === word) return 0;
  if (value.startsWith(word)) return 1000;
  const index = value.indexOf(word);
  if (index < 0) return null;
  return /[\s_/\\.:-]/u.test(value[index - 1]) ? 2000 + index : 3000 + index;
}

/**
 * The score of something that is described by several texts, each with a bias that says how much it counts: every
 * word has to be found in one of them, and the best place of each word is what counts. Lower is better.
 */
export function fieldsScore(words: readonly string[], fields: readonly (readonly [text: string, bias: number])[]): number | null {
  let total = 0;
  for (const word of words) {
    let best: number | null = null;
    for (const [text, bias] of fields) {
      const score = wordScore(text, word);
      if (score !== null && (best === null || score + bias < best)) best = score + bias;
    }
    if (best === null) return null;
    total += best;
  }
  return total;
}

/** The parts of a text that the words of a query are found in, in order and without overlap: what a result marks. */
export function matchRanges(text: string, words: readonly string[]): readonly (readonly [start: number, end: number])[] {
  const value = text.toLowerCase();
  const found: [number, number][] = [];
  for (const word of words) {
    if (!word) continue;
    for (let at = value.indexOf(word); at >= 0; at = value.indexOf(word, at + word.length)) found.push([at, at + word.length]);
  }
  found.sort((left, right) => left[0] - right[0] || right[1] - left[1]);
  const merged: [number, number][] = [];
  for (const range of found) {
    const last = merged.at(-1);
    if (last && range[0] <= last[1]) last[1] = Math.max(last[1], range[1]);
    else merged.push(range);
  }
  return merged;
}

export type ProjectResult = Readonly<{ kind: "project"; key: string; score: number; project: WorkspaceProject; favorite: boolean; sessions: number }>;
export type SessionResult = Readonly<{ kind: "session"; key: string; score: number; session: WorkspaceSession; project: WorkspaceProject | null; child: boolean }>;
export type FileResult = Readonly<{ kind: "file"; key: string; score: number; path: string; directory: boolean; project: Pick<WorkspaceProject, "id" | "name" | "path"> }>;
export type CommandResult = Readonly<{ kind: "command"; key: string; score: number; name: string; label: string; description: string; group: string;
  keys: readonly string[]; enabled: boolean; run: () => void }>;
export type SearchResult = ProjectResult | SessionResult | FileResult | CommandResult;

const byName = (left: string, right: string) => left.localeCompare(right, undefined, { sensitivity: "base" });
const updated = (session: WorkspaceSession) => Date.parse(session.updatedAt) || 0;
const lastSegment = (path: string) => path.replace(/[\\/]+$/u, "").split(/[\\/]/u).at(-1) ?? path;

/**
 * The projects that a query finds: by their name first, then by the last folder of their path, then by the rest of
 * the path. Without a query they are all listed, the favorite ones first; an archived project comes after the others.
 */
export function searchProjects(snapshot: WorkspaceSnapshot, words: readonly string[], favorites: readonly string[] = []): ProjectResult[] {
  const starred = new Set(favorites);
  const counts = new Map<string, number>();
  for (const session of snapshot.sessions) if (session.projectId !== null) counts.set(session.projectId, (counts.get(session.projectId) ?? 0) + 1);
  const results: ProjectResult[] = [];
  for (const project of snapshot.projects) {
    const score = fieldsScore(words, [[project.name, 0], [lastSegment(project.path), 5], [project.path, 40]]);
    if (score === null) continue;
    results.push({ kind: "project", key: `project:${project.id}`, score, project, favorite: starred.has(project.id), sessions: counts.get(project.id) ?? 0 });
  }
  return results.sort((left, right) => Number(left.project.archived) - Number(right.project.archived) || left.score - right.score
    || Number(right.favorite) - Number(left.favorite) || byName(left.project.name, right.project.name) || byName(left.project.id, right.project.id));
}

/**
 * The sessions that a query finds, of every project and the chats: by their title, then by the name of their
 * project, then by their id. As good a match, the session that was updated last comes first; so does every session
 * when nothing is typed. `projectId` keeps the sessions of one project (null for the chats); undefined keeps them all.
 */
export function searchSessions(snapshot: WorkspaceSnapshot, words: readonly string[], projectId?: string | null): SessionResult[] {
  const projects = new Map(snapshot.projects.map(project => [project.id, project]));
  const results: SessionResult[] = [];
  for (const session of snapshot.sessions) {
    if (projectId !== undefined && session.projectId !== projectId) continue;
    const project = session.projectId === null ? null : projects.get(session.projectId) ?? null;
    // The title as it is shown: without the marks of Markdown that a first message may start with.
    const score = fieldsScore(words, [[plainTitle(session.title), 0], [session.fullTitle, 3], [project?.name ?? "", 60], [session.id, 90]]);
    if (score === null) continue;
    results.push({ kind: "session", key: `session:${session.id}`, score, session, project, child: session.parentSessionId !== null });
  }
  // A step of the ranking (whole text, start, start of a word, anywhere) outweighs the time; inside a step the time decides.
  const step = (result: SessionResult) => words.length ? Math.floor(result.score / 1000) : 0;
  return results.sort((left, right) => step(left) - step(right) || updated(right.session) - updated(left.session) || left.score - right.score
    || byName(left.session.id, right.session.id));
}

/** One group of results as it is shown: its results, and how many more of them the query found. */
export type ShownGroup = Readonly<{ group: SearchGroup; results: readonly SearchResult[]; total: number }>;

/**
 * What is shown for a category: one group whole (up to the limit), or, for every category, the first results of
 * each group, the group with the best match first. A query that starts with `/` only finds commands.
 */
export function shownGroups(category: SearchCategory, found: Readonly<Record<SearchGroup, readonly SearchResult[]>>, commandsOnly = false): ShownGroup[] {
  if (commandsOnly) category = "commands";
  if (category !== "all") {
    const results = found[category];
    return results.length ? [{ group: category, results: results.slice(0, categoryLimit), total: results.length }] : [];
  }
  return searchGroups
    .filter(group => found[group].length > 0)
    .map((group, order) => ({ group, order, best: Math.floor(found[group][0].score / 1000), results: found[group].slice(0, groupPreview[group]), total: found[group].length }))
    .sort((left, right) => left.best - right.best || left.order - right.order)
    .map(({ group, results, total }) => ({ group, results, total }));
}

/** The results of the groups, in the order they are shown: what the arrow keys move through. */
export const flatResults = (groups: readonly ShownGroup[]): readonly SearchResult[] => groups.flatMap(group => group.results);

/** The category after or before another one, around: what Tab and Shift+Tab choose. */
export function nextCategory(category: SearchCategory, delta: 1 | -1, available: readonly SearchCategory[] = searchCategories): SearchCategory {
  const at = Math.max(0, available.indexOf(category));
  return available[(at + delta + available.length) % available.length];
}
