import type { SubProjectCandidateView, SubProjectEntryView, SubProjectLabelView, SubProjectsMapView } from "#neoastra";

/** The most levels of the labels of a map, as for the labels of the user. */
export const maximumMapLabelDepth = 4;

/** What an entry of a map is for the user now. */
export type EntryState = "shown" | "hidden" | "optional" | "missing" | "outside" | "elsewhere";

/** What an entry of a map is: listed, hidden by the user, optional and not added, or a folder that cannot be used. */
export function entryState(entry: SubProjectEntryView): EntryState {
  if (entry.folder === "missing") return "missing";
  if (entry.folder === "outside") return "outside";
  if (entry.claimedBy) return "elsewhere";
  if (!entry.projectId) return "optional";
  return entry.hidden ? "hidden" : "shown";
}

/** The name an entry is shown under: its project's, the map's, else the name of its folder. */
export function entryName(entry: SubProjectEntryView): string {
  return entry.projectName ?? entry.name ?? entry.path.split("/").at(-1) ?? entry.key;
}

/** The labels above a label of a map, from the top one. */
export function mapLabelAncestors(key: string, labels: readonly SubProjectLabelView[]): readonly SubProjectLabelView[] {
  const above: SubProjectLabelView[] = [];
  for (let parent = labels.find(label => label.key === key)?.parent ?? null; parent && above.length < maximumMapLabelDepth; ) {
    const found = labels.find(label => label.key === parent);
    if (!found || above.includes(found)) break;
    above.unshift(found);
    parent = found.parent ?? null;
  }
  return above;
}

/** The path of a label of a map: `Backend › Payments`. */
export function mapLabelPath(key: string, labels: readonly SubProjectLabelView[]): string {
  const label = labels.find(value => value.key === key);
  return label ? [...mapLabelAncestors(key, labels).map(value => value.name), label.name].join(" › ") : key;
}

/** The keys of a label of a map and of every label under it. */
export function mapLabelSubtree(key: string, labels: readonly SubProjectLabelView[]): readonly string[] {
  const keys = [key];
  for (let index = 0; index < keys.length && keys.length <= labels.length + 1; index++)
    for (const label of labels) if (label.parent === keys[index] && !keys.includes(label.key)) keys.push(label.key);
  return keys;
}

/** The labels a label can go under: not itself, not one of its own, and not so deep that it would go past the bound. */
export function parentChoices(key: string | null, labels: readonly SubProjectLabelView[]): readonly SubProjectLabelView[] {
  const below = key ? new Set(mapLabelSubtree(key, labels)) : new Set<string>();
  const height = (value: string, depth = 0): number => depth > maximumMapLabelDepth ? 1
    : 1 + Math.max(0, ...labels.filter(label => label.parent === value).map(label => height(label.key, depth + 1)));
  const own = key ? height(key) : 1;
  return labels.filter(label => !below.has(label.key) && mapLabelAncestors(label.key, labels).length + 1 + own <= maximumMapLabelDepth);
}

/** The entries a filter keeps: by their name, their folder, their key and the names of their labels. */
export function filterEntries(entries: readonly SubProjectEntryView[], labels: readonly SubProjectLabelView[], text: string): readonly SubProjectEntryView[] {
  const words = text.trim().toLowerCase().split(/\s+/).filter(Boolean);
  if (!words.length) return entries;
  return entries.filter(entry => {
    const haystack = [entryName(entry), entry.path, entry.key, entry.description ?? "", ...entry.labels.map(key => mapLabelPath(key, labels))].join("\n").toLowerCase();
    return words.every(word => haystack.includes(word));
  });
}

/** How many entries are in each state, for the bar of the window. */
export function entryCounts(entries: readonly SubProjectEntryView[]): Readonly<Record<EntryState, number>> {
  const counts: Record<EntryState, number> = { shown: 0, hidden: 0, optional: 0, missing: 0, outside: 0, elsewhere: 0 };
  for (const entry of entries) counts[entryState(entry)]++;
  return counts;
}

/** What the form of an entry holds. */
export type EntryDraft = Readonly<{ name: string; path: string; description: string; labels: readonly string[]; optional: boolean; folderOnly: boolean }>;

/** The form of an entry as the map says it. */
export function entryDraft(entry: SubProjectEntryView): EntryDraft {
  return { name: entry.name ?? "", path: entry.path, description: entry.description ?? "", labels: entry.labels, optional: entry.optional,
    folderOnly: entry.write === "folder" };
}

/** What changed between an entry and its form: null for what stays, the whole list of labels when they changed. */
export function entryChanges(entry: SubProjectEntryView, draft: EntryDraft): Readonly<{
  name: string | null; path: string | null; description: string | null; labels: readonly string[] | null; optional: boolean | null; write: string | null;
}> | null {
  const before = entryDraft(entry);
  const name = draft.name.trim() !== before.name ? draft.name.trim() : null;
  const path = normalizeFolder(draft.path) !== before.path ? normalizeFolder(draft.path) : null;
  const description = draft.description.trim() !== before.description ? draft.description.trim() : null;
  const labels = draft.labels.length !== before.labels.length || draft.labels.some((key, index) => key !== before.labels[index]) ? draft.labels : null;
  const optional = draft.optional !== before.optional ? draft.optional : null;
  const write = draft.folderOnly !== before.folderOnly ? draft.folderOnly ? "folder" : "repository" : null;
  return name === null && path === null && description === null && labels === null && optional === null && write === null ? null
    : { name, path, description, labels, optional, write };
}

/** A folder as the map writes it: `/` between the names, none at the ends. */
export function normalizeFolder(path: string): string {
  return path.trim().replace(/\\/g, "/").replace(/^\.\/+/, "").replace(/\/+$/, "");
}

/** A key worked out from a name, as the host does: lower case, `-` for what is neither a letter nor a digit. */
export function keyFrom(value: string): string {
  const last = normalizeFolder(value).split("/").at(-1) ?? "";
  const key = last.toLowerCase().replace(/[^a-z0-9_]+/g, "-").replace(/^[-_]+|[-_]+$/g, "").slice(0, 48).replace(/[-_]+$/, "");
  return /^[a-z0-9][a-z0-9_-]*$/.test(key) ? key : "project";
}

/** Whether a text can be the key of a label or of a project of a map. */
export const validKey = (key: string): boolean => /^[a-z0-9][a-z0-9_-]{0,47}$/.test(key);

/** The candidates of a scan that can be added: not declared yet. */
export function addableCandidates(candidates: readonly SubProjectCandidateView[], map: SubProjectsMapView | null): readonly SubProjectCandidateView[] {
  const declared = new Set((map?.entries ?? []).map(entry => entry.path.toLowerCase()));
  return candidates.filter(candidate => !candidate.declared && !declared.has(candidate.path.toLowerCase()));
}

/** The prompt that asks an agent to set a repository up as a monorepo, with the skill that knows how. */
export const monorepoSetupPrompt = "Set this repository up as a monorepo for CodeAlta: activate the skill `codealta-monorepo` (`alta skill activate codealta-monorepo`), "
  + "read how the repository is organized, propose its sub-projects and their labels, and ask me before you write the project map.";
