// How the Plugins page of Settings shows its cards: what is kept between runs (the pictures on or off), the filters
// above the cards, and the search. Pure: the page draws what these say.

/** What the page keeps in the storage of the window. */
export type PluginViewPreferences = Readonly<{
  /** Whether the cards show their picture; off, they are compact. */
  images: boolean;
}>;

/** Where the preferences of the page are kept. */
export const pluginViewKey = "codealta.desktop.plugins.view.v1";
const defaults: PluginViewPreferences = { images: true };

/** The preferences as they were kept; the defaults for a storage that has none, cannot be read, or holds something else. */
export function restorePluginView(read: () => string | null): PluginViewPreferences {
  try {
    const value: unknown = JSON.parse(read() ?? "null");
    if (!value || typeof value !== "object") return defaults;
    const images = (value as { images?: unknown }).images;
    return { images: typeof images === "boolean" ? images : defaults.images };
  } catch { return defaults; }
}

/** Keeps the preferences; a storage that refuses leaves them for this run only. */
export function persistPluginView(write: (value: string) => void, preferences: PluginViewPreferences): void {
  try { write(JSON.stringify(preferences)); } catch { /* Kept for this run only. */ }
}

/** Where a plugin comes from, as the page filters it: every plugin, the ones that ship with CodeAlta, the user's, the project's. */
export type PluginSourceFilter = "all" | "builtIn" | "user" | "project";
/** Whether a plugin is turned on, as the page filters it. */
export type PluginStateFilter = "all" | "on" | "off";
export type PluginFilters = Readonly<{ query: string; source: PluginSourceFilter; state: PluginStateFilter }>;
export const noPluginFilters: PluginFilters = { query: "", source: "all", state: "all" };

/** What the filters and the search read of a plugin. */
export type PluginFacts = Readonly<{
  id: string; name: string; description: string | null; enabled: boolean; builtIn: boolean; project: boolean;
  /** What else names it: its author, its tags, the titles of its canvases. */
  words?: readonly (string | null | undefined)[];
}>;

/** Where a plugin comes from. */
export function pluginSource(plugin: Pick<PluginFacts, "builtIn" | "project">): Exclude<PluginSourceFilter, "all"> {
  return plugin.builtIn ? "builtIn" : plugin.project ? "project" : "user";
}

/** Whether a plugin matches every word of a search, in its name, its id, its description or what else names it; an empty search matches all. */
export function matchesPluginQuery(plugin: PluginFacts, query: string): boolean {
  const terms = query.toLocaleLowerCase().split(/\s+/u).filter(Boolean);
  if (terms.length === 0) return true;
  const text = [plugin.name, plugin.id, plugin.description, ...(plugin.words ?? [])].filter(Boolean).join("\n").toLocaleLowerCase();
  return terms.every(term => text.includes(term));
}

/** Whether a plugin passes the filters. */
export function matchesPluginFilters(plugin: PluginFacts, filters: PluginFilters): boolean {
  return (filters.source === "all" || pluginSource(plugin) === filters.source)
    && (filters.state === "all" || plugin.enabled === (filters.state === "on"))
    && matchesPluginQuery(plugin, filters.query);
}

/** How many plugins each source has, for the labels of the filter: counted among those the search and the state keep. */
export function pluginSourceCounts(plugins: readonly PluginFacts[], filters: PluginFilters): Readonly<Record<PluginSourceFilter, number>> {
  const counts = { all: 0, builtIn: 0, user: 0, project: 0 };
  for (const plugin of plugins) {
    if (!matchesPluginFilters(plugin, { ...filters, source: "all" })) continue;
    counts.all++; counts[pluginSource(plugin)]++;
  }
  return counts;
}

/** Whether any filter narrows the list. */
export function hasPluginFilters(filters: PluginFilters): boolean {
  return filters.query.trim().length > 0 || filters.source !== "all" || filters.state !== "all";
}

/**
 * The color of a plugin that gives none: one of a few, chosen by its id, so that a plugin keeps its color from one run to
 * the next and two plugins seldom share one.
 */
export function pluginAccent(id: string, color?: string | null): string {
  if (color && /^#(?:[0-9a-f]{3}|[0-9a-f]{6})$/iu.test(color)) return color;
  let hash = 0;
  for (const character of id.toLowerCase()) hash = (hash * 31 + character.charCodeAt(0)) >>> 0;
  return accents[hash % accents.length];
}
const accents = ["#5b8def", "#2dd4bf", "#f59e0b", "#e879f9", "#34d399", "#f87171", "#a78bfa", "#38bdf8"] as const;
