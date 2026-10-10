import { filterKeys, originValues, toolKindValues, type FilterKey, type Filters } from "./frame";
import type { ModelsResult, ProjectsResult } from "./types";

// The values a filter can take, and how a filter is told apart from the others. Pure: the names come in as arguments.

/** One value of a filter: what the request carries, and the words the user reads. */
export type FilterChoice = Readonly<{ value: string; label: string; detail?: string }>;

/** What the lists of values are made of. */
export type FilterSources = Readonly<{
  models: ModelsResult | null;
  projects: ProjectsResult | null;
  spaces: readonly Readonly<{ id: string; name: string }>[];
  /** The words of a fixed value: the origin `agent`, the kind of tool `mcp`. */
  word: (key: FilterKey, value: string) => string;
}>;

/** The values a kind of filter can take, in the order they should be offered. */
export function filterChoices(key: FilterKey, sources: FilterSources): FilterChoice[] {
  switch (key) {
    case "space": return sources.spaces.map(space => ({ value: space.id, label: space.name }));
    case "project": return (sources.projects?.rows ?? []).map(row => ({ value: row.project, label: row.name })).sort((a, b) => a.label.localeCompare(b.label));
    case "provider": return unique((sources.models?.rows ?? []).map(row => row.provider)).map(value => ({ value, label: value }));
    case "model": return (sources.models?.rows ?? []).map(row => ({ value: row.model, label: row.model, detail: row.provider }));
    case "effort": return unique((sources.models?.efforts ?? []).map(row => row.effort).filter(value => value !== "")).map(value => ({ value, label: value }));
    case "origin": return originValues.map(value => ({ value, label: sources.word("origin", value) }));
    case "toolKind": return toolKindValues.map(value => ({ value, label: sources.word("toolKind", value) }));
  }
}

const unique = (values: readonly string[]) => [...new Set(values)].sort((a, b) => a.localeCompare(b));

/** The kinds of filter not set yet, in menu order. */
export const unsetFilters = (filters: Filters): readonly FilterKey[] => filterKeys.filter(key => !filters[key]);

/** The label of a filter value: the one it was set with, or the value itself. */
export const filterEntryText = (filters: Filters, key: FilterKey): string => filters[key]?.label ?? filters[key]?.value ?? "";

/** The kinds of filter a page honors: a chip of another kind is shown as not applying to it. */
export const ignoredKinds = (ignored: readonly string[]): ReadonlySet<FilterKey> => new Set(ignored.filter((name): name is FilterKey => (filterKeys as readonly string[]).includes(name)));
