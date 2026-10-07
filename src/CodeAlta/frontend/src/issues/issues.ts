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

/** Whether a listing that was asked is still the one the page shows. */
export type IssueListKey = Readonly<{ projectId: string; service: string; kind: IssueKind; filter: IssueFilter; query: string }>;
export function sameList(a: IssueListKey | null, b: IssueListKey | null): boolean {
  return !!a && !!b && a.projectId === b.projectId && a.service === b.service && a.kind === b.kind && a.filter === b.filter && a.query === b.query;
}
