import type { diskUsage, DiskUsageCheckout, DiskUsageCleanResponse, DiskUsageGitState, DiskUsageMeasurement, DiskUsageProject, DiskUsageSession } from "#neoastra";
import type { MessageKey } from "../localization";
import { seriesColorCount } from "../charts/palette";
import { storageBytes, storageSize } from "../storage/storage";

/** The calls of the tab to the host. */
export type DiskUsageApi = Pick<typeof diskUsage, "last" | "scan" | "clean">;

/** What the texts of the model are made with: the translation of the window. */
export type Translate = (key: MessageKey, parameters?: Readonly<Record<string, string | number>>) => string;

/** The end of a measurement: when it ran, and its total, which counts every byte once. */
export type DiskUsageEnd = Pick<DiskUsageMeasurement, "startedAt" | "completedAt" | "totalBytes" | "totalPartial" | "complete" | "unreadable" | "sessionsKnown">;

/** What the tab shows: a measurement that ended, or one whose records arrive. */
export type DiskUsageView = Readonly<{
  projects: readonly DiskUsageProject[];
  /** The end of the measurement; null while it runs. */
  done: DiskUsageEnd | null;
}>;

/** The view of a measurement that ended. */
export const measurementView = (measurement: DiskUsageMeasurement): DiskUsageView => ({ projects: measurement.projects, done: measurement });

/**
 * A size: `exact`; `atLeast` when part of it could not be read; `unknown` when nothing of it was measured (never 0);
 * `none` when it is counted elsewhere or does not apply.
 */
export type Size = Readonly<{ bytes: number; state: "exact" | "atLeast" | "unknown" | "none" }>;
const exact = (bytes: number): Size => ({ bytes, state: "exact" });
const unknownSize: Size = Object.freeze({ bytes: 0, state: "unknown" });
const noSize: Size = Object.freeze({ bytes: 0, state: "none" });

/** The sum of sizes: what is known, at least, when a part is not known; unknown when nothing known adds to it. */
export function sumSizes(sizes: readonly Size[]): Size {
  const counted = sizes.filter(size => size.state !== "none");
  if (counted.length === 0) return noSize;
  const known = counted.filter(size => size.state !== "unknown");
  if (known.length === 0) return unknownSize;
  const bytes = known.reduce((total, size) => total + size.bytes, 0);
  // "At least nothing" says nothing: what is known adds nothing to a part that is not known.
  if (bytes === 0 && known.length < counted.length) return unknownSize;
  return { bytes, state: known.length < counted.length || known.some(size => size.state === "atLeast") ? "atLeast" : "exact" };
}

/** A size as the user reads it: "1.2 GiB", "at least 1.2 GiB", "Unknown", or a dash. */
export function sizeText(size: Size, locale: string, t: Translate): string {
  if (size.state === "none") return "—";
  if (size.state === "unknown") return t("Unknown");
  const text = storageSize(size.bytes, locale);
  return size.state === "atLeast" ? t("at least {size}", { size: text }) : text;
}

/** What git says of a checkout: whether removing it loses something. */
export type Verdict = "nothing_local" | "ignored_files_only" | "has_local_work" | "unknown";
const verdicts: readonly Verdict[] = ["nothing_local", "ignored_files_only", "has_local_work", "unknown"];
// The order in which a verdict weighs: what loses work first.
const weight: Readonly<Record<Verdict, number>> = { has_local_work: 3, unknown: 2, ignored_files_only: 1, nothing_local: 0 };
const asVerdict = (value: string): Verdict => verdicts.includes(value as Verdict) ? value as Verdict : "unknown";

export type NodeKind = "root" | "space" | "project" | "worktrees" | "sessions" | "checkout" | "session";

/** A node of the tree of the tab. */
export type UsageNode = Readonly<{
  /** Unique in the tree and stable while records arrive: the breadcrumb is a list of these. */
  id: string;
  kind: NodeKind;
  name: string;
  /** What it holds. */
  size: Size;
  /** What it adds to its parent's map: never a byte another node counts. */
  counted: number;
  buildOutput: Size;
  lastUsed: string | null;
  /** The git verdict of a checkout; a row that sums several checkouts has none: the rows inside say it. */
  verdict: Verdict | null;
  children: readonly UsageNode[];
  /** The project record the node is part of (for a checkout: the one that carries it). */
  project?: DiskUsageProject;
  checkout?: DiskUsageCheckout;
  session?: DiskUsageSession;
  /** Sessions of the project that the record does not name. */
  moreSessions?: number;
  /** The project that carries the checkouts of this one's repository. */
  measuredWith?: string;
  /** The other projects whose repository or folder this one carries. */
  sharedWith?: readonly string[];
  /** The checkout that holds the same folder: this one adds nothing. */
  sameFolderAs?: string;
  /** Why it could not be measured, in the words of the host. */
  problem?: string | null;
}>;

const latest = (values: readonly (string | null | undefined)[]): string | null => {
  let best: string | null = null, at = -Infinity;
  for (const value of values) {
    const time = value ? Date.parse(value) : NaN;
    if (Number.isFinite(time) && time > at) { at = time; best = value!; }
  }
  return best;
};

/** The size of a checkout, as it was measured; a folder that is gone holds nothing, one measured as another adds nothing. */
function checkoutSize(checkout: DiskUsageCheckout): Size {
  if (checkout.missing) return exact(0);
  if (checkout.sameFolderAs) return noSize;
  if (!checkout.measure) return unknownSize;
  return { bytes: storageBytes(checkout.measure.totalBytes), state: storageBytes(checkout.measure.skippedEntries) > 0 ? "atLeast" : "exact" };
}

function buildOutputSize(checkout: DiskUsageCheckout): Size {
  if (checkout.missing || checkout.sameFolderAs) return noSize;
  // Without git nothing is known as ignored.
  if (!checkout.measure || !checkout.measure.gitKnown) return unknownSize;
  return exact(storageBytes(checkout.measure.buildOutputBytes));
}

function checkoutNode(record: DiskUsageProject, checkout: DiskUsageCheckout, prefix: string, names: ReadonlyMap<string, string>): UsageNode {
  const size = checkoutSize(checkout);
  const judged = !checkout.missing && !checkout.sameFolderAs;
  const same = checkout.sameFolderAs ? record.checkouts.find(other => other.path === checkout.sameFolderAs)?.name ?? checkout.sameFolderAs : undefined;
  return {
    id: `${prefix}/checkout:${checkout.path}`, kind: "checkout", name: checkout.project ? names.get(record.id) ?? record.name : checkout.name,
    size, counted: size.state === "exact" || size.state === "atLeast" ? size.bytes : 0, buildOutput: buildOutputSize(checkout),
    lastUsed: checkout.lastUsedAt, verdict: judged ? asVerdict(checkout.verdict) : null,
    children: [], project: record, checkout, sameFolderAs: same, problem: checkout.problem,
  };
}

function sessionsNode(record: DiskUsageProject, prefix: string): UsageNode {
  const { sessions } = record;
  const bytes = storageBytes(sessions.bytes);
  const size: Size = sessions.count === 0 ? exact(0) : sessions.bytesKnown ? exact(bytes) : bytes > 0 ? { bytes, state: "atLeast" } : unknownSize;
  const counted = size.state === "unknown" ? 0 : Math.max(0, bytes - storageBytes(sessions.bytesInCheckouts));
  const children = sessions.largest.map((session): UsageNode => ({
    id: `${prefix}/sessions/session:${session.id}`, kind: "session", name: session.title,
    size: size.state === "unknown" ? unknownSize : exact(storageBytes(session.bytes)), counted: storageBytes(session.bytes), buildOutput: noSize,
    lastUsed: session.updatedAt, verdict: null, children: [], project: record, session,
  }));
  return {
    id: `${prefix}/sessions`, kind: "sessions", name: "", size, counted, buildOutput: noSize, lastUsed: latest(sessions.largest.map(session => session.updatedAt)),
    verdict: null, children, project: record, moreSessions: Math.max(0, sessions.count - sessions.largest.length),
  };
}

function projectNode(record: DiskUsageProject, prefix: string, names: ReadonlyMap<string, string>): UsageNode {
  const id = `${prefix}/project:${record.id}`;
  const folders = record.checkouts.filter(checkout => checkout.project).map(checkout => checkoutNode(record, checkout, id, names));
  const others = record.checkouts.filter(checkout => !checkout.project).map(checkout => checkoutNode(record, checkout, `${id}/worktrees`, names));
  const children: UsageNode[] = [...folders];
  if (others.length > 0) {
    const size = sumSizes(others.map(node => node.size));
    children.push({ id: `${id}/worktrees`, kind: "worktrees", name: "", size, counted: others.reduce((total, node) => total + node.counted, 0),
      buildOutput: sumSizes(others.map(node => node.buildOutput)), lastUsed: latest(others.map(node => node.lastUsed)),
      verdict: null, children: others, project: record });
  }
  const sessions = sessionsNode(record, id);
  children.push(sessions);
  // A folder that could not be read, or that is gone, is a part of the project that is not known.
  const folderSize: Size[] = record.status === "failed" || record.status === "ok" && !record.measuredWith && record.checkouts.length === 0 ? [unknownSize] : [];
  const size = sumSizes([...folderSize, ...children.map(node => node.size)]);
  return {
    id, kind: "project", name: names.get(record.id) ?? record.name, size, counted: children.reduce((total, node) => total + node.counted, 0),
    buildOutput: sumSizes([...folders, ...others].map(node => node.buildOutput)), lastUsed: latest(children.map(node => node.lastUsed)),
    verdict: null, children, project: record,
    ...record.measuredWith ? { measuredWith: names.get(record.measuredWith) ?? record.measuredWith } : {},
    ...record.sharedWith.length ? { sharedWith: record.sharedWith.map(other => names.get(other) ?? other) } : {},
    problem: record.problem,
  };
}

/** A space of the window: its id and its name. */
export type SpaceName = Readonly<{ id: string; name: string }>;

/**
 * The tree of a measurement: the spaces with their projects, each project with its folder, its worktrees and its
 * sessions. A project in several
 * spaces is in each; the total of the root is the one of the measurement, which counts it once. A project measured
 * with another names it, and adds only its sessions.
 * @param spaces The spaces of the window, for their names and order; a project in none is under a node of its own.
 */
export function usageTree(view: DiskUsageView, spaces: readonly SpaceName[]): UsageNode {
  const names = new Map(view.projects.map(project => [project.id, project.name]));
  const known = new Set(spaces.map(space => space.id));
  const groups = [...spaces.map(space => ({ id: space.id, name: space.name, projects: view.projects.filter(project => project.spaces.includes(space.id)) })),
    { id: "", name: "", projects: view.projects.filter(project => !project.spaces.some(space => known.has(space))) }].filter(group => group.projects.length > 0);
  const spaceNodes = groups.map((group): UsageNode => {
    const prefix = `space:${group.id}`;
    const children = group.projects.map(project => projectNode(project, prefix, names));
    return { id: prefix, kind: "space", name: group.name, size: sumSizes(children.map(node => node.size)), counted: children.reduce((total, node) => total + node.counted, 0),
      buildOutput: sumSizes(children.map(node => node.buildOutput)), lastUsed: latest(children.map(node => node.lastUsed)),
      verdict: null, children };
  });
  // The total counts a project of several spaces once: the measurement's own, when it ended; what arrived so far while it runs.
  const once = new Map(view.projects.map(project => [project.id, project]));
  const parts = [...once.values()].map(project => projectNode(project, "", names));
  const arrived = sumSizes(parts.map(node => node.size));
  const size: Size = view.done ? { bytes: storageBytes(view.done.totalBytes), state: view.done.totalPartial ? "atLeast" : "exact" }
    // More projects may come: what arrived is a lower bound.
    : arrived.state === "exact" ? { bytes: arrived.bytes, state: "atLeast" } : arrived;
  return { id: "root", kind: "root", name: "", size, counted: size.bytes, buildOutput: sumSizes(parts.map(node => node.buildOutput)),
    lastUsed: latest(parts.map(node => node.lastUsed)),
    verdict: null, children: spaceNodes };
}

/** The nodes from the root to the one a path of ids names, as far as they still exist. */
export function nodePath(root: UsageNode, path: readonly string[]): readonly UsageNode[] {
  const nodes = [root];
  for (const id of path) {
    const next = nodes[nodes.length - 1].children.find(child => child.id === id);
    if (!next) break;
    nodes.push(next);
  }
  return nodes;
}

/** Whether a node opens a level of its own. */
export const drillable = (node: UsageNode) => node.children.length > 0 && node.kind !== "checkout";

export type SortColumn = "name" | "size" | "buildOutput" | "lastUsed" | "verdict";
export type Sort = Readonly<{ column: SortColumn; descending: boolean }>;

const sizeKey = (size: Size): number | null => size.state === "exact" || size.state === "atLeast" ? size.bytes : null;

/**
 * The nodes of a level in the order of a column. What is not known goes last whatever the direction; equal ones keep
 * the order of their names.
 */
export function sortNodes(nodes: readonly UsageNode[], sort: Sort, label: (node: UsageNode) => string = node => node.name, locale = "en"): readonly UsageNode[] {
  const collator = new Intl.Collator(locale, { sensitivity: "base", numeric: true });
  const key = (node: UsageNode): number | string | null => {
    switch (sort.column) {
      case "name": return label(node);
      case "size": return sizeKey(node.size);
      case "buildOutput": return sizeKey(node.buildOutput);
      case "lastUsed": { const time = node.lastUsed ? Date.parse(node.lastUsed) : NaN; return Number.isFinite(time) ? time : null; }
      case "verdict": return node.verdict ? weight[node.verdict] : null;
    }
  };
  return [...nodes].sort((a, b) => {
    const left = key(a), right = key(b);
    if (left === null || right === null) {
      if (left !== right) return left === null ? 1 : -1;
    } else if (left !== right) {
      const order = typeof left === "string" ? collator.compare(left, right as string) : (left as number) - (right as number);
      if (order !== 0) return sort.descending ? -order : order;
    }
    return collator.compare(label(a), label(b));
  });
}

const verdictLabels: Readonly<Record<Verdict, MessageKey>> = {
  nothing_local: "Nothing local", ignored_files_only: "Ignored files only", has_local_work: "Has local work", unknown: "Git state unknown",
};

/** The name of a verdict. */
export const verdictLabel = (verdict: Verdict, t: Translate) => t(verdictLabels[verdict]);

const operations: Readonly<Record<string, MessageKey>> = {
  merge: "A merge is in progress.", rebase: "A rebase is in progress.", "cherry-pick": "A cherry-pick is in progress.", revert: "A revert is in progress.",
  bisect: "A bisect is in progress.",
};

/**
 * Why a checkout has its verdict, one sentence each: what removing it loses, and what a clone does not bring back.
 * Empty for a checkout with nothing to say.
 */
export function verdictReasons(checkout: DiskUsageCheckout, t: Translate, locale: string,
  held: (path: string) => DiskUsageCheckout | undefined = () => undefined): readonly string[] {
  const reasons: string[] = [];
  const git: DiskUsageGitState | null = checkout.git;
  const count = (value: number) => new Intl.NumberFormat(locale).format(value);
  if (checkout.missing) return [t("Git still lists this worktree, and its folder is gone.")];
  if (!checkout.measure) reasons.push(checkout.problem ? t("It could not be measured: {problem}", { problem: checkout.problem }) : t("It was not measured."));
  else if (!checkout.measure.gitKnown || !git) reasons.push(t("Git could not read this checkout."));
  if (git) {
    if (!git.hasRemote) reasons.push(t("The repository has no remote."));
    if (git.changedFiles > 0) reasons.push(t(git.changedFiles === 1 ? "{count} changed file" : "{count} changed files", { count: count(git.changedFiles) }));
    if (git.untrackedFiles > 0) reasons.push(t(git.untrackedFiles === 1 ? "{count} untracked file" : "{count} untracked files", { count: count(git.untrackedFiles) }));
    if (git.unpushedBranches.length > 0) {
      const names = git.unpushedBranches.map(branch => t("{branch} ({count})", { branch: branch.name, count: count(branch.commits) })).join(", ");
      reasons.push(t(git.unpushedBranchesTruncated ? "Branches with commits on no remote: {names}, and more" : "Branches with commits on no remote: {names}", { names }));
    }
    if (git.stashes > 0) reasons.push(t(git.stashes === 1 ? "{count} stash" : "{count} stashes", { count: count(git.stashes) }));
    if (git.detached && git.detachedCommits > 0) reasons.push(t("A detached HEAD on {count} commits of no branch", { count: count(git.detachedCommits) }));
    if (git.submodulesWithLocalWork.length > 0) reasons.push(t("Submodules with local work: {names}", { names: git.submodulesWithLocalWork.join(", ") }));
    if (git.hiddenFiles > 0) reasons.push(t("{count} files git is told not to look at", { count: count(git.hiddenFiles) }));
    if (git.tagOnlyCommits > 0) reasons.push(t("{count} commits only a tag holds", { count: count(git.tagOnlyCommits) }));
    if (git.unexaminedSubmoduleRepositories > 0) reasons.push(t("{count} submodule repositories that are not checked out", { count: count(git.unexaminedSubmoduleRepositories) }));
    if (git.operationInProgress) reasons.push(t(operations[git.operationInProgress] ?? "An operation of git is in progress."));
    if (git.otherWorktreeDetachedCommits > 0) reasons.push(t("{count} commits only the detached HEADs of other worktrees hold", { count: count(git.otherWorktreeDetachedCommits) }));
    if (git.unexaminedBranches > 0) reasons.push(t("{count} branches were not examined: there are more than 100.", { count: count(git.unexaminedBranches) }));
    if (git.otherRefs > 0) {
      const names = git.otherRefNames.join(", ");
      reasons.push(git.otherRefNames.length >= git.otherRefs ? t("Other references: {names}", { names })
        : t("{count} other references, among them {names}", { count: count(git.otherRefs), names }));
    }
  }
  if (checkout.measure) {
    const other = storageBytes(checkout.measure.otherIgnoredBytes), entries = storageBytes(checkout.measure.otherIgnoredCount);
    if (entries > 0) reasons.push(t("{size} ignored outside build folders, in {count} entries: a clone does not bring it back.", { size: storageSize(other, locale), count: count(entries) }));
    const skipped = storageBytes(checkout.measure.skippedEntries);
    if (skipped > 0) reasons.push(t("{count} entries could not be read: the sizes are a lower bound.", { count: count(skipped) }));
  }
  // The checkouts inside its folder that made its verdict worse: removing the folder removes them too.
  for (const path of checkout.worseHeldCheckouts) {
    const inside = held(path), name = inside?.name ?? path.split(/[\\/]+/u).filter(Boolean).pop() ?? path;
    const verdict = asVerdict(inside?.verdict ?? "unknown");
    reasons.push(t(verdict === "has_local_work" ? "It holds the checkout {name}, which has local work."
      : verdict === "ignored_files_only" ? "It holds the checkout {name}, which holds ignored files outside build folders." : "It holds the checkout {name}, whose git state is unknown.", { name }));
  }
  // A verdict that is not Nothing local always says something.
  if (reasons.length === 0 && asVerdict(checkout.verdict) !== "nothing_local") reasons.push(t("Git did not say why."));
  return reasons;
}

/** The checkouts of every project of a view, by their path: what the checkouts a folder holds are called. */
export function checkoutsByPath(view: DiskUsageView): ReadonlyMap<string, DiskUsageCheckout> {
  return new Map(view.projects.flatMap(project => project.checkouts.map(checkout => [checkout.path, checkout] as const)));
}

/** Why the build output of a checkout cannot be chosen for a clean; null when it can. */
export type CleanBlock = "missing" | "same_folder" | "in_use" | "running_application" | "not_measured" | "no_build_output";

/**
 * Whether the build output of a checkout can be chosen for a clean: one measured in this run, with build folders to
 * remove, that no session works in and the application does not run from. The host decides again when it is asked.
 * @param current Whether the measurement shown was made in this run of the application.
 */
export function cleanBlock(checkout: DiskUsageCheckout, current: boolean): CleanBlock | null {
  if (checkout.missing) return "missing";
  if (checkout.sameFolderAs) return "same_folder";
  if (checkout.inUse) return "in_use";
  if (checkout.runningApplication) return "running_application";
  if (!current || !checkout.measure || !checkout.measureId) return "not_measured";
  if (checkout.planned.length === 0) return "no_build_output";
  return null;
}

const blockTexts: Readonly<Record<CleanBlock, MessageKey>> = {
  missing: "The folder of this worktree is gone.",
  same_folder: "Git lists this folder twice: it is measured as the other one.",
  in_use: "A session is working here",
  running_application: "This application runs from this folder: its build output is not cleaned.",
  not_measured: "Measure first: only a measurement of this run can be cleaned.",
  no_build_output: "No build output to clean.",
};

/** Why a checkout cannot be chosen for a clean, in a sentence. */
export const cleanBlockText = (block: CleanBlock, t: Translate) => t(blockTexts[block]);

const cleanStatuses: Readonly<Record<string, MessageKey>> = {
  canceled: "The clean was stopped: the folders not reached are kept.",
  checkout_changed: "The checkout changed since it was measured: what was not cleaned is kept. Measure again.",
  not_repository: "This folder is not a checkout git can read: nothing was deleted.",
  git_too_old: "Git is too old to clean safely here (2.23 or later is needed): nothing was deleted.",
  busy: "A session works in this checkout: it is not cleaned.",
  running_application: "A program runs from this checkout: it is not cleaned.",
  unavailable: "Disk usage is not available in this window.",
  stale_epoch: "The host changed. Reload the window.",
  invalid_request: "The folders to clean were not the ones measured: nothing was deleted.",
  not_found: "This checkout is no longer in the last measurement. Measure again.",
  not_measured: "Measure first: only a measurement of this run can be cleaned.",
  stale_measure: "The measurement shown is out of date for this checkout. Measure again.",
  scanning: "A measurement is running: clean once it ends.",
  cleaning: "Another clean is running: try again once it ends.",
  unconfirmed: "The answer did not arrive: measure again to see what is left.",
};

/** What a clean that did not end well says; null for one that did. */
export function cleanStatusText(status: string, message: string | null, t: Translate): string | null {
  if (status === "ok") return null;
  if (status === "failed") return message ? t("Git failed: {message}", { message }) : t("Git failed.");
  return t(cleanStatuses[status] ?? "The clean could not be done.");
}

const outcomeTexts: Readonly<Record<string, MessageKey>> = {
  removed: "Removed",
  partly_removed: "Partly removed: some of its files could not be deleted.",
  kept_holds_repository: "Kept: it holds a repository.",
  kept_below_link: "Kept: it is reached through a link.",
  kept_not_build_output: "Kept: git does not ignore it as build output any more.",
  not_attempted: "Not attempted.",
  unknown: "Outcome unknown.",
};

/** What became of one folder of a clean. */
export const outcomeText = (outcome: string, t: Translate) => t(outcomeTexts[outcome] ?? "Outcome unknown.");

/** What a clean freed, over all its answers. */
export const cleanedBytes = (results: readonly Pick<DiskUsageCleanResponse, "bytesFreed">[]) => results.reduce((total, result) => total + storageBytes(result.bytesFreed), 0);

/** Replaces a checkout of a view with the one a clean measured again. */
export function withCheckout(view: DiskUsageView, projectId: string, checkout: DiskUsageCheckout): DiskUsageView {
  return { ...view, projects: view.projects.map(project => project.id !== projectId ? project
    : { ...project, checkouts: project.checkouts.map(value => value.path === checkout.path ? checkout : value) }) };
}

/** Adds or replaces the record of a project in a view whose records arrive. */
export function withProject(view: DiskUsageView, project: DiskUsageProject): DiskUsageView {
  const index = view.projects.findIndex(value => value.id === project.id);
  return { ...view, projects: index < 0 ? [...view.projects, project] : view.projects.map((value, at) => at === index ? project : value) };
}

/** A segment of the donut of a level: one row, or the rows that share the segment "Other" (`id` null). */
export type DonutSlice = Readonly<{ id: string | null; value: number; color: number; nodes: readonly UsageNode[] }>;
/** The donut of a level: its segments in the order of their colors and the color of each row that has one. */
export type LevelDonut = Readonly<{ slices: readonly DonutSlice[]; colors: ReadonlyMap<string, number> }>;

/**
 * The donut of a level, by what each row adds: the largest rows each have a color of the palette, and when there are
 * more rows than colors, the last color is one segment "Other" for the rest. A row that adds nothing has no segment.
 * @param colorCount The colors of the palette.
 */
export function levelDonut(nodes: readonly UsageNode[], colorCount = seriesColorCount): LevelDonut {
  const sized = nodes.filter(node => node.counted > 0).sort((a, b) => b.counted - a.counted || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0));
  const own = sized.length <= colorCount ? sized : sized.slice(0, colorCount - 1);
  const rest = sized.slice(own.length);
  const slices: DonutSlice[] = own.map((node, color) => ({ id: node.id, value: node.counted, color, nodes: [node] }));
  if (rest.length > 0) slices.push({ id: null, value: rest.reduce((sum, node) => sum + node.counted, 0), color: colorCount - 1, nodes: rest });
  const colors = new Map(slices.flatMap(slice => slice.nodes.map(node => [node.id, slice.color] as const)));
  return { slices, colors };
}
