import type { IssueRow, IssueSource } from "#neoastra";
import type { IconName } from "../AppIcon";
import type { MessageKey } from "../localization";

/** What an item of a tracker is. */
export type IssueKind = "issue" | "pull_request";
/** Which items a listing asks for. */
export type IssueFilter = "open" | "closed" | "merged" | "all";

/** The lists of a kind, in the order of their tabs: a pull request can be merged, an issue cannot. */
export function issueFilters(kind: IssueKind): readonly IssueFilter[] {
  return kind === "pull_request" ? ["open", "merged", "closed", "all"] : ["open", "closed", "all"];
}
export function issueFilterLabel(filter: IssueFilter): MessageKey {
  return filter === "open" ? "Open" : filter === "closed" ? "Closed" : filter === "merged" ? "Merged" : "All";
}

/** The kinds a tracker has, issues first. */
export function sourceKinds(source: IssueSource | null | undefined): readonly IssueKind[] {
  const kinds = source?.kinds ?? [];
  return (["issue", "pull_request"] as const).filter(kind => kinds.includes(kind));
}

/** What the items of a kind are called in a service, as its users say it. */
export function kindLabel(kind: IssueKind, service: string | null | undefined, plural: boolean): MessageKey {
  if (kind === "pull_request") return service === "gitlab" ? (plural ? "Merge requests" : "Merge request") : plural ? "Pull requests" : "Pull request";
  return service === "azure_devops" ? (plural ? "Work items" : "Work item") : plural ? "Issues" : "Issue";
}

/** An item as it is named in a sentence: `#128` for a number, the key itself (`ALTA-12`) otherwise. */
export function issueReference(item: Pick<IssueRow, "id">): string {
  return /^\d+$/.test(item.id) ? `#${item.id}` : item.id;
}

export function issueKey(item: Pick<IssueRow, "kind" | "id">): string { return `${item.kind}\n${item.id}`; }

export function issueIcon(item: Pick<IssueRow, "kind" | "state">): IconName {
  return item.kind === "pull_request" ? "pullRequest" : item.state === "closed" ? "issueClosed" : "issueOpen";
}

/** The tone of a state: open is what is alive, merged and done are what ended well, closed is what ended otherwise. */
export function issueTone(item: Pick<IssueRow, "kind" | "state">): "open" | "done" | "closed" | "draft" {
  return item.state === "open" ? "open" : item.state === "draft" ? "draft" : item.state === "merged" || item.kind === "issue" ? "done" : "closed";
}

export function issueStateLabel(item: Pick<IssueRow, "state">): MessageKey {
  return item.state === "draft" ? "Draft" : item.state === "merged" ? "Merged" : item.state === "closed" ? "Closed" : "Open";
}

/** What signing in to a service takes, as a command to run in a terminal; null when it is not a command. */
export function signInCommand(service: string): string | null {
  return service === "github" ? "gh auth login" : service === "gitlab" ? "glab auth login" : service === "azure_devops" ? "az login" : null;
}

/** A readable text color for a label of a given background, and the background as CSS; null without a color. */
export function labelColors(color: string | null | undefined): { background: string; color: string } | null {
  if (!color || !/^[0-9a-f]{6}$/i.test(color)) return null;
  const [red, green, blue] = [0, 2, 4].map(index => parseInt(color.slice(index, index + 2), 16));
  // The perceived brightness says which of a dark or a light text is read on it.
  const bright = (red * 299 + green * 587 + blue * 114) / 1000 > 150;
  return { background: `#${color}`, color: bright ? "#1b1f24" : "#ffffff" };
}

/**
 * The part of a tracker a list is limited to: the one that was chosen, as long as the tracker has it, otherwise
 * all of them. A choice made for one tracker says nothing of another.
 */
export function shownPart(source: Pick<IssueSource, "parts"> | null | undefined, chosen: string | null): string | null {
  return chosen !== null && (source?.parts ?? []).includes(chosen) ? chosen : null;
}

/** What the parts of a tracker are, for the choice of all of them: the projects of Jira. */
export function allPartsLabel(service: string | null | undefined): MessageKey {
  return service === "jira" ? "All projects" : "All";
}

/** Whether a listing that was asked is still the one the page shows. */
export type IssueListKey = Readonly<{ projectId: string; service: string; kind: IssueKind; filter: IssueFilter; query: string; part: string | null; filters?: string }>;
export function sameList(a: IssueListKey | null, b: IssueListKey | null): boolean {
  return !!a && !!b && a.projectId === b.projectId && a.service === b.service && a.kind === b.kind && a.filter === b.filter && a.query === b.query && a.part === b.part
    && (a.filters ?? "") === (b.filters ?? "");
}

/** The values of the filters of a tracker, by filter id; an empty value lists every item. */
export type FilterChoices = Readonly<Record<string, string>>;
/**
 * The values a listing is asked with: those that say something, and the filters of `kept` even empty (a filter the tab
 * may change, chosen for every item), as a text that is equal for equal choices.
 */
export function listedFilters(selected: FilterChoices, kept: readonly string[] = []): { values: Record<string, string>; text: string } {
  const entries = Object.entries(selected).filter(([id, value]) => value.length > 0 || kept.includes(id)).sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0));
  return { values: Object.fromEntries(entries), text: entries.map(([id, value]) => `${id}=${value}`).join("\n") };
}

/** The text of a value in a choice: its label, then what more it says (the sprint that is the current one, its dates). */
export function filterValueText(value: Readonly<{ label: string; detail: string | null }>): string {
  return value.detail ? `${value.label} (${value.detail})` : value.label;
}

const pickStorage = "codealta.desktop.issue-picks.v1";

/** What names the values chosen in the tab for the filters a view lets it change: the project, the tracker and the view. */
export function pickKey(projectId: string, service: string, view: Readonly<{ name: string; source: string }>): string {
  return `${projectId}\n${service}\n${view.source}\n${view.name}`;
}

/** Reads the values chosen in the tab for a view, by filter id; none when the storage has nothing usable. */
export function readPicks(key: string, storage: Pick<Storage, "getItem"> | null = globalThis.localStorage ?? null): Readonly<Record<string, string>> {
  try {
    const all = JSON.parse(storage?.getItem(pickStorage) ?? "{}") as unknown;
    const found = all && typeof all === "object" ? (all as Record<string, unknown>)[key] : null;
    return found && typeof found === "object"
      ? Object.fromEntries(Object.entries(found as Record<string, unknown>).filter((entry): entry is [string, string] => typeof entry[1] === "string")) : {};
  } catch {
    return {};
  }
}

/** Keeps the values chosen in the tab for a view for the next time it is shown; none removes them. */
export function writePicks(key: string, values: Readonly<Record<string, string>>, storage: Pick<Storage, "getItem" | "setItem"> | null = globalThis.localStorage ?? null): void {
  try {
    const parsed = JSON.parse(storage?.getItem(pickStorage) ?? "{}") as unknown;
    const all: Record<string, unknown> = parsed && typeof parsed === "object" ? { ...(parsed as Record<string, unknown>) } : {};
    if (Object.keys(values).length === 0) delete all[key]; else all[key] = values;
    storage?.setItem(pickStorage, JSON.stringify(all));
  } catch { /* The choice lasts as long as the window. */ }
}

/** Moves the values chosen in the tab for a view to another key: a renamed view keeps them. */
export function movePicks(from: string, to: string, storage: Pick<Storage, "getItem" | "setItem"> | null = globalThis.localStorage ?? null): void {
  const values = readPicks(from, storage);
  if (Object.keys(values).length === 0) return;
  writePicks(to, values, storage);
  writePicks(from, {}, storage);
}

/**
 * The values chosen after the user chose one: the filters whose values depend on it lose their choice, so that they
 * take the value of the view again (the sprints of another team).
 */
export function choosePick(picks: Readonly<Record<string, string>>, filters: readonly Readonly<{ id: string; dependsOn: readonly string[] }>[], id: string, value: string): Record<string, string> {
  const next: Record<string, string> = { ...picks, [id]: value };
  const dropped = new Set([id]);
  for (let changed = true; changed;) {
    changed = false;
    for (const filter of filters) {
      if (!dropped.has(filter.id) && filter.dependsOn.some(other => dropped.has(other))) { dropped.add(filter.id); delete next[filter.id]; changed = true; }
    }
  }
  return next;
}

/** The values chosen after one is forgotten (↺): the filters whose values depend on it are forgotten too. */
export function forgetPick(picks: Readonly<Record<string, string>>, filters: readonly Readonly<{ id: string; dependsOn: readonly string[] }>[], id: string): Record<string, string> {
  const next = choosePick(picks, filters, id, "");
  delete next[id];
  return next;
}

// The words trackers write for their filters and their symbolic values, which the window translates.
const trackerWords: ReadonlySet<string> = new Set<MessageKey>(["Team", "Sprint", "Assigned to", "Type", "Current sprint", "Next sprint", "Previous sprint", "Me", "Nobody",
  "Your team", "None today", "Yes", "No", "Order", "Last changed first", "Backlog order", "Highest priority first", "Newest first"]);

/**
 * A text a tracker gives in the language of the window when it is one of the words of filters, as the tracker wrote
 * it otherwise: the name of a team, of a sprint or of a type (`Task`) is data, not a word of the window.
 */
export function trackerText(text: string, t: (key: MessageKey) => string): string {
  return trackerWords.has(text) ? t(text as MessageKey) : text;
}

/** What joins the values of a filter that takes several (the states New, Ready, On hold), as the host joins them. */
export const valueSeparator = "\u001f";

/** The values a value of a filter holds: none for an empty one. */
export function splitValues(value: string | null | undefined): string[] {
  return value ? value.split(valueSeparator).map(part => part.trim()).filter(part => part.length > 0) : [];
}

/** The value of a filter that holds several values. */
export function joinValues(values: readonly string[]): string {
  return values.join(valueSeparator);
}
