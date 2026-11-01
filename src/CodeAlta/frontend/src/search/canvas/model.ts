import type { LocalSearchHit, LocalSearchMark } from "#neoastra";
import type { DayValue } from "../../charts";
import type { MessageKey } from "../../localization";

// What the Search canvas computes from the answers of the host, as pure functions: the kinds and their names, the tiles, the history,
// the treemap of the projects, the rank of a hit, its snippet and where it leads. The canvas (SearchCanvas.tsx) only draws them.

/** The kinds of the index, in the order the canvas lists them: what was written first, then what is installed. */
export const searchKinds = Object.freeze(["message", "session", "note", "plan", "task", "doc", "skill", "tool", "mcp", "plugin", "command", "setting"] as const);
export type SearchKindName = typeof searchKinds[number];

/** The name of a kind, as the canvas writes it. */
export const kindLabels: Readonly<Record<SearchKindName, MessageKey>> = Object.freeze({
  message: "Messages", session: "Sessions", note: "Notes", plan: "Plans", task: "Tasks", doc: "User guide", skill: "Skills", tool: "Tools", mcp: "MCP servers",
  plugin: "Plugins", command: "Commands", setting: "Settings",
});

/** Whether a name is one of the kinds of the index. */
export const isSearchKind = (value: string): value is SearchKindName => (searchKinds as readonly string[]).includes(value);

/** How long the page waits after the last key before it asks. */
export const queryDelayMilliseconds = 150;
/** The most hits of a query, and of a listing of a kind. */
export const queryLimit = 50;
export const browseLimit = 100;

/** The kinds that have items, the most first, then in the order of the canvas. */
export function kindCounts(items: Readonly<Record<string, number>>): readonly Readonly<{ kind: SearchKindName; count: number }>[] {
  return searchKinds.map(kind => ({ kind, count: Math.max(0, items[kind] ?? 0) })).filter(row => row.count > 0)
    .sort((a, b) => b.count - a.count || searchKinds.indexOf(a.kind) - searchKinds.indexOf(b.kind));
}

/** The items of every kind. */
export const totalItems = (items: Readonly<Record<string, number>>): number => Object.values(items).reduce((sum, value) => sum + Math.max(0, value), 0);

/** A size in kilobytes as it reads: `512 KB`, `3.4 MB`, `1.2 GB`. */
export function formatSize(kilobytes: number, locale: string): string {
  const [value, unit] = kilobytes >= 1024 * 1024 ? [kilobytes / (1024 * 1024), "gigabyte"] : kilobytes >= 1024 ? [kilobytes / 1024, "megabyte"] : [Math.max(0, kilobytes), "kilobyte"];
  return new Intl.NumberFormat(locale, { style: "unit", unit, unitDisplay: "short", maximumFractionDigits: value < 10 && unit !== "kilobyte" ? 1 : 0 }).format(value);
}

/** The local day of a date, `YYYY-MM-DD`. */
export function localDay(date: Date): string {
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, "0")}-${String(date.getDate()).padStart(2, "0")}`;
}

/** The year of days the history shows: from 52 weeks before today, to today. */
export function historyRange(today: Date): Readonly<{ from: string; to: string }> {
  const from = new Date(today.getFullYear(), today.getMonth(), today.getDate() - 364);
  return { from: localDay(from), to: localDay(today) };
}

/** The turns of each day the host counted, within the range the history shows. */
export function historyData(days: readonly Readonly<{ day: string; turns: number }>[], range: Readonly<{ from: string; to: string }>): DayValue[] {
  return days.filter(day => day.day >= range.from && day.day <= range.to && day.turns > 0).map(day => ({ date: day.day, value: day.turns }));
}

/** A project of the space in the treemap: its items, or a share of the map when it is kept out of search and so has none. */
export type ProjectCell = Readonly<{ id: string; name: string; items: number; value: number; keptOut: boolean }>;

/**
 * The projects of the space the treemap shows, the largest first: those with items, sized by them, and those kept out of search, which hold
 * nothing and are given a small cell of their own so that they can be seen. A project with no item that is not kept out is left out.
 */
export function projectCells(projects: readonly Readonly<{ id: string; name: string }>[], counts: Readonly<Record<string, number>>, excluded: readonly string[]): ProjectCell[] {
  const out = new Set(excluded.map(id => id.toLowerCase()));
  const countOf = (id: string) => {
    const key = Object.keys(counts).find(other => other.toLowerCase() === id.toLowerCase());
    return key ? Math.max(0, counts[key]) : 0;
  };
  const shown = projects.map(project => ({ id: project.id, name: project.name, items: countOf(project.id), keptOut: out.has(project.id.toLowerCase()) }))
    .filter(project => project.keptOut || project.items > 0);
  const total = shown.reduce((sum, project) => sum + project.items, 0);
  // A twentieth of what the others hold, never less than one item: visible, never the largest cell of a map that has others.
  const share = Math.max(1, Math.round(total / 20));
  return shown.map(project => ({ ...project, value: project.keptOut ? share : project.items }))
    .sort((a, b) => Number(a.keptOut) - Number(b.keptOut) || b.value - a.value || a.name.localeCompare(b.name));
}

/** The words of the treemap. */
export type TreemapWords = Readonly<{ name: string; keptOut: string; format: (value: number) => string }>;

/**
 * The option of the treemap of the projects: a cell per project, a project kept out of search hatched and named as such. `hatch` is the fill of
 * that cell, an image pattern the page draws (ECharts draws the decals of items only when it chooses them itself); transparent without one.
 */
export function projectTreemapOption(cells: readonly ProjectCell[], colors: readonly string[], muted: string, words: TreemapWords, hatch?: unknown) {
  const data = cells.map((cell, index) => cell.keptOut
    ? { name: `${cell.name} · ${words.keptOut}`, value: cell.value, id: cell.id,
      // The border of a cell of a treemap is its background: only the fill is changed.
      itemStyle: { color: hatch ?? "transparent" },
      label: { show: true, color: muted, fontWeight: "bold" } }
    : { name: cell.name, value: cell.value, id: cell.id, itemStyle: { color: colors.length ? colors[index % colors.length] : undefined } });
  const option = {
    tooltip: { trigger: "item", formatter: (params: { data?: { id?: string }; name?: string }) => {
      const cell = cells.find(item => item.id === params.data?.id);
      return cell ? `${cell.name}<br>${cell.keptOut ? words.keptOut : words.format(cell.items)}` : String(params.name ?? "");
    } },
    series: [{ name: words.name, type: "treemap", roam: false, nodeClick: false, breadcrumb: { show: false }, left: 0, right: 0, top: 0, bottom: 0,
      label: { show: true, formatter: "{b}", overflow: "truncate" }, upperLabel: { show: false }, itemStyle: { gapWidth: 2, borderRadius: 3 }, data }],
  };
  const table = { columns: ["", words.name], rows: cells.map(cell => [cell.name, cell.keptOut ? words.keptOut : cell.items] as (string | number)[]) };
  return { option, table };
}

/** How many of the projects of the space are kept out of search. */
export function keptOutCount(projects: readonly Readonly<{ id: string }>[], excluded: readonly string[]): number {
  const out = new Set(excluded.map(id => id.toLowerCase()));
  return projects.filter(project => out.has(project.id.toLowerCase())).length;
}

/**
 * What explains the rank of a hit: its rank by words and by meaning, each when it has one, as `words #3 · meaning #1`. A hit
 * found by meaning alone is `approximate`: none of its words are those of the query (`≈ meaning #1`).
 */
export function rankParts(hit: Pick<LocalSearchHit, "wordsRank" | "meaningRank">): readonly Readonly<{ key: "words" | "meaning"; rank: number; approximate: boolean }>[] {
  const parts: { key: "words" | "meaning"; rank: number; approximate: boolean }[] = [];
  if (hit.wordsRank) parts.push({ key: "words", rank: hit.wordsRank, approximate: false });
  if (hit.meaningRank) parts.push({ key: "meaning", rank: hit.meaningRank, approximate: !hit.wordsRank });
  return parts;
}

/** How Explore searches: by words, by meaning, or both merged by rank (`auto` for the host). */
export type SearchMode = "words" | "meaning" | "auto";
export const searchModes: readonly SearchMode[] = Object.freeze(["words", "meaning", "auto"] as const);

/** The mode a query is asked with: the one chosen while search by meaning is on, by words otherwise. */
export const queryMode = (chosen: SearchMode, meaningOn: boolean): SearchMode => meaningOn ? chosen : "words";

/** The share of passages that have a vector, in whole percents: 100 only when every one has; 0 for an empty scope. */
export function meaningPercent(coverage: Readonly<{ chunks: number; embedded: number }> | null | undefined): number {
  if (!coverage || coverage.chunks <= 0) return 0;
  return coverage.embedded >= coverage.chunks ? 100 : Math.min(99, Math.floor(coverage.embedded * 100 / coverage.chunks));
}

/** The days of the history searchable by words only: those whose messages are not all searchable by meaning yet. */
export function wordsOnlyDays(days: readonly Readonly<{ day: string; chunks: number; embedded: number }>[], range: Readonly<{ from: string; to: string }>): string[] {
  return days.filter(day => day.day >= range.from && day.day <= range.to && day.chunks > day.embedded).map(day => day.day);
}

/** The speed of the indexing by meaning, in passages a second, from the tenths the host gives; null before it is known. */
export const embeddingSpeed = (tenths: number | null | undefined): number | null => tenths && tenths > 0 ? tenths / 10 : null;

/** What the tile of search by meaning says: off (by words only), on with its share, or where its first indexing is. */
export type MeaningTile =
  | Readonly<{ kind: "words" }>
  | Readonly<{ kind: "share"; percent: number }>
  | Readonly<{ kind: "downloading"; percent: number | null }>
  | Readonly<{ kind: "loading" }>
  | Readonly<{ kind: "indexing" | "paused"; percent: number; secondsLeft: number | null }>
  | Readonly<{ kind: "failed"; reason: string }>;

/** The tile of search by meaning, from the state of search by meaning and the share of the space that has vectors. */
export function meaningTile(semantic: Readonly<{ enabled: boolean; state: string; secondsLeft: number | null; error: string | null;
  download: Readonly<{ receivedKilobytes: number; totalKilobytes: number; error: string | null }> }> | null | undefined,
coverage: Readonly<{ chunks: number; embedded: number }> | null | undefined): MeaningTile {
  if (!semantic?.enabled) return { kind: "words" };
  const percent = meaningPercent(coverage);
  switch (semantic.state) {
    case "downloading": return { kind: "downloading", percent: semantic.download.totalKilobytes > 0 ? Math.floor(semantic.download.receivedKilobytes * 100 / semantic.download.totalKilobytes) : null };
    case "loading": return { kind: "loading" };
    case "indexing": case "paused": return { kind: semantic.state, percent, secondsLeft: semantic.state === "indexing" ? semantic.secondsLeft : null };
    case "failed": return { kind: "failed", reason: semantic.error ?? semantic.download.error ?? "" };
    case "off": return { kind: "words" };
    default: return { kind: "share", percent };
  }
}

/** A snippet cut into its plain parts and its matched words. A mark outside the text or over another one is left out. */
export function snippetParts(snippet: string, marks: readonly LocalSearchMark[]): readonly Readonly<{ text: string; mark: boolean }>[] {
  const parts: { text: string; mark: boolean }[] = [];
  let at = 0;
  for (const mark of [...marks].sort((a, b) => a.start - b.start)) {
    const start = Math.max(at, mark.start), end = Math.min(snippet.length, mark.start + mark.length);
    if (end <= start) continue;
    if (start > at) parts.push({ text: snippet.slice(at, start), mark: false });
    parts.push({ text: snippet.slice(start, end), mark: true });
    at = end;
  }
  if (at < snippet.length) parts.push({ text: snippet.slice(at), mark: false });
  return parts;
}

/** The values of the `extra` of a hit: the strings of its JSON object. */
export function extraOf(extra: string | null | undefined): Readonly<Record<string, string>> {
  if (!extra) return {};
  try {
    const value: unknown = JSON.parse(extra);
    if (typeof value !== "object" || value === null || Array.isArray(value)) return {};
    return Object.fromEntries(Object.entries(value).filter((entry): entry is [string, string] => typeof entry[1] === "string"));
  } catch { return {}; }
}

/** Where a hit leads when it is opened. */
export type HitTarget =
  | Readonly<{ kind: "message"; ref: string; sessionId: string }>
  | Readonly<{ kind: "session"; sessionId: string }>
  | Readonly<{ kind: "file"; path: string }>
  | Readonly<{ kind: "documentation"; page: string | null; anchor: string | null }>
  | Readonly<{ kind: "command"; id: string }>;

/**
 * Where a hit leads, as the search window opens it: a message at its place in its session, a session or its notes, a plan or a task in the
 * editor (from the folder of its project), a page of the guide at its heading, a skill's file, a command of the window. A tool, an MCP server
 * and a plugin lead nowhere: what they offer is the agent's. Null when it leads nowhere or what it needs is unknown here.
 */
export function hitTarget(hit: Pick<LocalSearchHit, "kind" | "ref" | "sessionId" | "projectId" | "extra">, projects: readonly Readonly<{ id: string; path: string }>[]): HitTarget | null {
  const extra = extraOf(hit.extra);
  switch (hit.kind) {
    case "message": return hit.sessionId ? { kind: "message", ref: hit.ref, sessionId: hit.sessionId } : null;
    case "session": case "note": return hit.sessionId ? { kind: "session", sessionId: hit.sessionId } : null;
    case "plan": case "task": {
      const project = projects.find(item => hit.projectId && item.id.toLowerCase() === hit.projectId.toLowerCase());
      const id = extra.id;
      if (!project || !id || !/^[^/\\]+$/u.test(id) || id === "." || id === "..") return null;
      const separator = project.path.includes("\\") && !project.path.includes("/") ? "\\" : "/";
      const folder = project.path.replace(/[\\/]+$/u, "");
      return { kind: "file", path: [folder, ".alta", hit.kind === "plan" ? "plans" : "tasks", `${id}.md`].join(separator) };
    }
    case "doc": return extra.page ? { kind: "documentation", page: extra.page, anchor: extra.anchor ?? null } : { kind: "documentation", page: null, anchor: null };
    case "skill": return extra.path ? { kind: "file", path: extra.path } : null;
    case "command": return extra.id ? { kind: "command", id: extra.id } : null;
    default: return null;
  }
}

/** The name of a family of sources, as the state of the index lists them; null for a family the canvas does not know (its key is shown). */
export function sourceLabel(family: string): MessageKey | null {
  switch (family) {
    case "journal": return "Session journals";
    case "plans": return "Plans and tasks";
    case "docs": return "User guide";
    case "skills": return "Skills";
    case "tools": return "Tools";
    case "plugins": return "Plugins";
    case "mcp": return "MCP servers";
    case "commands": return "Commands of the window";
    default: return null;
  }
}

/** Where the index is, in a word: off, starting, building for the first time, updating, or ready. */
export type IndexState = "off" | "starting" | "building" | "updating" | "ready";

export function indexState(status: Readonly<{ busy: boolean; ready: boolean; settings: Readonly<{ enabled: boolean }> | null }>): IndexState {
  if (status.settings && !status.settings.enabled) return "off";
  if (!status.ready) return status.busy ? "building" : "starting";
  return status.busy ? "updating" : "ready";
}

/** The share of the journals of a pass already read, from 0 to 1, or null when no pass is counted. */
export function passProgress(status: Readonly<{ journalsDone: number; journalsTotal: number }>): number | null {
  return status.journalsTotal > 0 ? Math.min(1, Math.max(0, status.journalsDone / status.journalsTotal)) : null;
}

/** A date of the index as it is shown; null for the items that have none (the catalog is dated from 1970). */
export function shownDate(at: string | null | undefined): Date | null {
  if (!at) return null;
  const date = new Date(at);
  return Number.isNaN(date.getTime()) || date.getUTCFullYear() < 2000 ? null : date;
}

/** What Explore shows: the hits of a query, the items of one kind, or the kinds and their counts. */
export type ExploreView = "query" | "browse" | "kinds";

export const exploreView = (query: string, kind: SearchKindName | null): ExploreView => query.trim() ? "query" : kind ? "browse" : "kinds";

/** The next hit the keyboard moves to: down and up wrap around the list. */
export function moveActive(active: number, count: number, step: 1 | -1): number {
  if (count <= 0) return -1;
  if (active < 0) return step > 0 ? 0 : count - 1;
  return (active + step + count) % count;
}

/** Who asked the searches of a day, as the Activity page stacks them. */
export const activityOrigins = Object.freeze(["window", "agent", "toolSearch", "other"] as const);
export type ActivityOrigin = typeof activityOrigins[number];
export type ActivityDay = Readonly<{ day: string; window: number; agent: number; toolSearch: number; other: number; empty: number }>;

/** The days the Activity page shows: the last 90, today included, each with its searches (0 for a day without). */
export function activityDays(days: readonly ActivityDay[], today: Date, count = 90): ActivityDay[] {
  const byDay = new Map(days.map(day => [day.day, day]));
  const list: ActivityDay[] = [];
  for (let offset = count - 1; offset >= 0; offset--) {
    const key = localDay(new Date(today.getFullYear(), today.getMonth(), today.getDate() - offset));
    list.push(byDay.get(key) ?? { day: key, window: 0, agent: 0, toolSearch: 0, other: 0, empty: 0 });
  }
  return list;
}

/** The share of searches that found nothing, in whole percents; null without searches. */
export const emptyPercent = (searches: number, empty: number): number | null => searches > 0 ? Math.round(empty * 100 / searches) : null;

/** The option of the searches per day, stacked by who asked them; an origin without any search is left out of the chart. */
export function activityOption(days: readonly ActivityDay[], names: Readonly<Record<ActivityOrigin, string>>, colors: readonly string[], dayLabel: (day: string) => string) {
  const shown = activityOrigins.filter(origin => days.some(day => day[origin] > 0));
  const option = {
    legend: { data: shown.map(origin => names[origin]) },
    tooltip: { trigger: "axis", axisPointer: { type: "shadow" } },
    grid: { left: 36, right: 12, top: 12, bottom: 28, containLabel: false },
    xAxis: { type: "category", data: days.map(day => dayLabel(day.day)), axisTick: { alignWithLabel: true }, axisLabel: { hideOverlap: true } },
    yAxis: { type: "value", minInterval: 1 },
    series: shown.map(origin => ({ name: names[origin], type: "bar", stack: "origin", barMaxWidth: 14, data: days.map(day => day[origin]),
      itemStyle: { color: colors.length ? colors[activityOrigins.indexOf(origin) % colors.length] : undefined } })),
  };
  const table = { columns: ["", ...shown.map(origin => names[origin])], rows: days.filter(day => shown.some(origin => day[origin] > 0)).map(day => [dayLabel(day.day), ...shown.map(origin => day[origin])] as (string | number)[]) };
  return { option, table };
}
