import type { IssueFilterEntry, IssueFilterField, IssueFilterValue, IssueViewDetail, IssueViewName } from "#neoastra";
import type { MessageKey } from "../localization";
import { joinValues, splitValues } from "./issues";

/** Whose a view is: the user's (`.alta/local/issues.toml`, never committed) or the team's (`.alta/issues.toml`). */
export type ViewSource = "personal" | "team";
export const viewSources: readonly ViewSource[] = ["personal", "team"];

/** The name of the group of the views of a source, as the lists say it. */
export function viewSourceLabel(source: ViewSource | string): MessageKey {
  return source === "team" ? "For the team" : "For me";
}

/** What names a view in a list: its source and its name. */
export function viewKey(view: Pick<IssueViewName, "name" | "source">): string {
  return `${view.source}\n${view.name}`;
}

/** A name no view has yet: the one given, else the same with a number. */
export function freeViewName(views: readonly Pick<IssueViewName, "name">[], wanted: string): string {
  const taken = new Set(views.map(view => view.name));
  if (!taken.has(wanted)) return wanted;
  for (let index = 2; ; index++) if (!taken.has(`${wanted} ${index}`)) return `${wanted} ${index}`;
}

/** Whether a view lists by a filter (`on`) or keeps it, with its value and its name, unused (`off`). */
export type FilterMode = "on" | "off";

/**
 * What a view says of a filter it names (a filter it does not name is off): whether it lists by it, its value (null for
 * every item), its name (null for the tracker's), and whether the tab may change its value for a while (`pick`).
 */
export type FilterEntry = Readonly<{ id: string; mode: FilterMode; value: string | null; label: string | null; pick: boolean }>;

/** Whether a view lists by a filter: it names it, and does not turn it off. */
export function isActive(entries: readonly FilterEntry[], id: string): boolean {
  const entry = entries.find(candidate => candidate.id === id);
  return !!entry && entry.mode !== "off";
}

/** The name of the type of a field, as the window of the views says it; a filter of the tracker's own is built in. */
export function fieldTypeLabel(type: string | null | undefined): MessageKey {
  switch (type) {
    case "list": return "List";
    case "number": return "Number";
    case "person": return "Person";
    case "tags": return "Tags";
    case "path": return "Path";
    case "boolean": return "Yes or no";
    case "text": return "Text";
    default: return "Built in";
  }
}

// A filter a view names is on unless it says off.
const asMode = (value: string | null | undefined): FilterMode => value === "off" ? "off" : "on";

/** The filters of a view, as the window of the views edits them. */
export function entriesOf(view: Pick<IssueViewDetail, "filters"> | null | undefined): FilterEntry[] {
  return (view?.filters ?? []).map(entry => ({ id: entry.id, mode: asMode(entry.mode), value: entry.value, label: entry.label, pick: entry.pick === true }));
}

/** The entry of a filter; a filter the view does not name is off, of every item. */
export function entryFor(entries: readonly FilterEntry[], id: string): FilterEntry {
  return entries.find(entry => entry.id === id) ?? { id, mode: "off", value: null, label: null, pick: false };
}

/**
 * The entries after one filter changed; a filter the view did not name comes last, off unless the change turns it on:
 * a name given to a filter does not make the view list by it.
 */
export function changeEntry(entries: readonly FilterEntry[], id: string, change: Partial<Omit<FilterEntry, "id">>): FilterEntry[] {
  const next = { ...entryFor(entries, id), ...change };
  return entries.some(entry => entry.id === id) ? entries.map(entry => entry.id === id ? next : entry) : [...entries, next];
}

/** Whether two lists of filters say the same. */
export function sameEntries(a: readonly FilterEntry[], b: readonly FilterEntry[]): boolean {
  return a.length === b.length && a.every(entry => {
    const other = b.find(candidate => candidate.id === entry.id);
    return !!other && other.mode === entry.mode && other.value === entry.value && other.label === entry.label && other.pick === entry.pick;
  });
}

/** The filters a view can be given: those of the tracker's own it does not name yet, then the fields it does not name. */
export function addableFilters(filters: readonly IssueFilterField[], fields: readonly Readonly<{ id: string }>[], entries: readonly FilterEntry[]): { own: string[]; fields: string[] } {
  const named = new Set(entries.map(entry => entry.id));
  return { own: filters.filter(filter => !filter.fieldType && !named.has(filter.id)).map(filter => filter.id), fields: fields.map(field => field.id).filter(id => !named.has(id)) };
}

/** The value of a filter a text of a view names: by its id, else by its label, without case. */
export function valueOf(filter: Pick<IssueFilterField, "values">, text: string | null | undefined): IssueFilterValue | null {
  if (!text) return null;
  const lower = text.toLowerCase();
  return filter.values.find(value => value.id.toLowerCase() === lower) ?? filter.values.find(value => value.label.toLowerCase() === lower) ?? null;
}

/**
 * What a view keeps for a value: a symbolic value by its id (`@current`, `@me`), any other by its label,
 * which a person reads in the file and which stays the same in every copy of the project.
 */
export function configuredText(value: IssueFilterValue): string {
  return value.id.startsWith("@") ? value.id : value.label;
}

/** The values of a filter a text of a view names, one or several. */
export function valuesOf(filter: Pick<IssueFilterField, "values">, text: string | null | undefined): IssueFilterValue[] {
  return splitValues(text).map(part => valueOf(filter, part)).filter((value): value is IssueFilterValue => value !== null);
}

/** What a view keeps for the value ids of a filter: each by a name a person reads; empty for every item. */
export function configuredValue(filter: Pick<IssueFilterField, "values">, ids: string): string {
  return joinValues(splitValues(ids).map(id => { const value = filter.values.find(candidate => candidate.id === id); return value ? configuredText(value) : id; }));
}

/**
 * The values to ask the tracker with, so that a filter that depends on another offers the values of the one the view
 * being edited names (the sprints of its team): the id of each value the entries name.
 */
export function formSelections(filters: readonly IssueFilterField[], entries: readonly FilterEntry[]): Record<string, string> {
  const selections: Record<string, string> = {};
  for (const filter of filters) {
    const value = valueOf(filter, entries.find(entry => entry.id === filter.id)?.value);
    if (value && filter.kind !== "text" && !filter.multiple) selections[filter.id] = value.id;
  }
  return selections;
}

/** The entries after a filter of the view is turned on or off: off, it stays in the view with its value and its name. */
export function switchEntry(entries: readonly FilterEntry[], id: string, on: boolean): FilterEntry[] {
  return changeEntry(entries, id, { mode: on ? "on" : "off" });
}

/** The entries to send to the host, in the order of the view. */
export function savedEntries(entries: readonly FilterEntry[]): IssueFilterEntry[] {
  return entries.map(entry => ({ id: entry.id, mode: entry.mode, value: entry.value, label: entry.label, pick: entry.pick ? true : null }));
}
