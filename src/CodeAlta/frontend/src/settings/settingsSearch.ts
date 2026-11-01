import { translate, type MessageKey } from "../localization";
import type { SettingResult } from "../search/searchResults";
import { settingText, type SettingEntry } from "./settingsCatalog";
import { settingsPageOf, settingsPages, type SettingsPageEntry } from "./settingsPages";

/** A setting a query found, with its score: lower is better. */
export type SettingMatch = Readonly<{ entry: SettingEntry; score: number }>;

/** A page of Settings a query found by its name or its words. */
export type SettingsPageMatch = Readonly<{ page: SettingsPageEntry & { group: MessageKey }; score: number }>;

/** What a query found: the settings, best first, and the pages. */
export type SettingsSearchResult = Readonly<{ settings: readonly SettingMatch[]; pages: readonly SettingsPageMatch[] }>;

/** A text as it is compared: lower case, without accents, so that "parametre" finds "paramètre". */
export function foldText(text: string): string {
  return text.normalize("NFD").replace(/\p{M}/gu, "").toLowerCase();
}

/** The words of a query, folded; empty for a blank query. */
export function queryWords(query: string): readonly string[] {
  return foldText(query).split(/\s+/).filter(word => word.length > 0);
}

// The best weight of a word among the texts of a setting: the label first, in the shown language and in English, then
// the part of its page, the page, the words that find it, its help and its id. -1 when the word is in none.
function wordWeight(word: string, fields: readonly (readonly [string | null, number])[]): number {
  let best = -1;
  for (const [text, weight] of fields) {
    if (!text) continue;
    const folded = foldText(text);
    const at = folded.indexOf(word);
    if (at < 0) continue;
    // A word that starts a word of the text weighs a little more than one inside a word.
    const start = at === 0 || /[\s\p{P}]/u.test(folded[at - 1]);
    const score = weight + (start ? 0 : 3);
    if (best < 0 || score < best) best = score;
  }
  return best;
}

/** The name of the page of a setting as the window shows it: its group and its name, translated. */
export function settingPagePath(locale: string, entry: SettingEntry): string {
  const page = settingsPageOf(entry.page);
  if (page) return `${translate(locale, page.group)} › ${translate(locale, page.label)}`;
  return entry.pluginName ? `${translate(locale, "Extensions")} › ${entry.pluginName}` : entry.page;
}

/**
 * Finds the settings and the pages of a query, in the shown language and in English: every word must be in one of
 * their texts. Settings that are not available here are left out.
 */
export function searchSettings(entries: readonly SettingEntry[], query: string, locale: string): SettingsSearchResult {
  const words = queryWords(query);
  if (words.length === 0) return Object.freeze({ settings: [], pages: [] });
  const settings: SettingMatch[] = [];
  for (const entry of entries) {
    if (!entry.available) continue;
    const shown = (text: string | null) => text === null ? null : settingText(locale, text, entry.builtIn);
    const page = settingsPageOf(entry.page);
    const fields: (readonly [string | null, number])[] = [
      [shown(entry.label), 0], [entry.label, 4],
      [shown(entry.section), 10], [entry.section, 12],
      [page ? translate(locale, page.label) : entry.pluginName, 14], [page?.label ?? null, 16],
      [entry.keywords.join(" "), 20],
      [shown(entry.help), 30], [entry.help, 32],
      [entry.id.replace(/[._]/g, " "), 40],
    ];
    let score = 0;
    for (const word of words) {
      const weight = wordWeight(word, fields);
      if (weight < 0) { score = -1; break; }
      score += weight;
    }
    if (score >= 0) settings.push(Object.freeze({ entry, score }));
  }
  settings.sort((a, b) => a.score - b.score);
  const pages: SettingsPageMatch[] = [];
  for (const page of settingsPages) {
    const fields: (readonly [string | null, number])[] = [[translate(locale, page.label), 0], [page.label, 4], [translate(locale, page.group), 10], [page.keywords.join(" "), 20]];
    let score = 0;
    for (const word of words) {
      const weight = wordWeight(word, fields);
      if (weight < 0) { score = -1; break; }
      score += weight;
    }
    if (score >= 0) pages.push(Object.freeze({ page, score }));
  }
  pages.sort((a, b) => a.score - b.score);
  return Object.freeze({ settings, pages });
}

/** A group of results: the settings of one page, in the order of the page, under the parts of the page. */
export type SettingsResultGroup = Readonly<{ page: string; settings: readonly SettingEntry[]; score: number }>;

/**
 * The results by page: the pages in the order of the navigation (the pages of plugins after), and in each page its
 * settings in the order of the catalog, so that the results read like the pages they come from.
 */
export function groupResults(result: SettingsSearchResult, entries: readonly SettingEntry[]): readonly SettingsResultGroup[] {
  const found = new Map(result.settings.map(match => [match.entry.id, match.score]));
  const pageOrder = new Map(settingsPages.map((page, index) => [page.id as string, index]));
  const groups = new Map<string, { settings: SettingEntry[]; score: number }>();
  for (const entry of entries) {
    const score = found.get(entry.id);
    if (score === undefined) continue;
    const group = groups.get(entry.page) ?? { settings: [], score };
    group.settings.push(entry);
    group.score = Math.min(group.score, score);
    groups.set(entry.page, group);
  }
  return [...groups.entries()]
    .sort(([a], [b]) => (pageOrder.get(a) ?? 1000) - (pageOrder.get(b) ?? 1000) || a.localeCompare(b))
    .map(([page, group]) => Object.freeze({ page, settings: Object.freeze(group.settings), score: group.score }));
}

/**
 * The settings and the pages of Settings for the search of the window: the settings first, then the pages found by their
 * name, scored like commands so that the groups of the search compare.
 */
export function settingResults(entries: readonly SettingEntry[], query: string, locale: string, open: (page: string, setting: string | null) => void): readonly SettingResult[] {
  const found = searchSettings(entries, query, locale);
  const settings = found.settings.map(({ entry, score }): SettingResult => Object.freeze({
    kind: "setting", key: `setting:${entry.id}`, score: 2000 + score * 20, label: settingText(locale, entry.label, entry.builtIn), path: settingPagePath(locale, entry),
    description: entry.help ? settingText(locale, entry.help, entry.builtIn) : null, page: false, run: () => open(entry.page, entry.id),
  }));
  const pages = found.pages.map(({ page, score }): SettingResult => Object.freeze({
    kind: "setting", key: `settings-page:${page.id}`, score: 2500 + score * 20, label: translate(locale, page.label),
    path: `${translate(locale, "Settings")} › ${translate(locale, page.group)}`, description: null, page: true, run: () => open(page.id, null),
  }));
  return [...settings, ...pages].sort((a, b) => a.score - b.score);
}

/** How many settings each page has among the results, for the navigation of Settings. */
export function matchCounts(result: SettingsSearchResult): ReadonlyMap<string, number> {
  const counts = new Map<string, number>();
  for (const match of result.settings) counts.set(match.entry.page, (counts.get(match.entry.page) ?? 0) + 1);
  for (const match of result.pages) if (!counts.has(match.page.id)) counts.set(match.page.id, 0);
  return counts;
}
