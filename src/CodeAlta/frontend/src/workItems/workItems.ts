// What the page shows of the work items of the projects: the follow-up tasks agents propose and the plans.
import type { WorkItemRow, WorkItemsProject, WorkItemsSettings } from "#neoastra";
import type { IconName } from "../AppIcon";
import type { MessageKey } from "../localization";

export type WorkKind = "task" | "plan";
/** Where an item is for the user: waiting for a decision, being done, set aside, or closed. */
export type WorkStage = "todo" | "running" | "later" | "closed";
/** A task or a plan with its project and its stage. */
export type WorkItem = WorkItemRow & Readonly<{ projectId: string; stage: WorkStage }>;
/** A way of starting the work of an item. */
export type WorkStart = "worktree" | "session" | "here";

export const workStarts: readonly WorkStart[] = ["worktree", "session", "here"];
export const defaultWorkSettings: WorkItemsSettings = { propose: true, notify: true, completedTasks: "delete", dismissedTasks: "delete", completedPlans: "keep", start: "worktree" };

/** The key of an item among all the items of the window. */
export const workItemKey = (item: Readonly<{ projectId: string; kind: string; id: string }>) => `${item.projectId}\n${item.kind}\n${item.id}`;

/**
 * The stage of an item. A session carries it out while that session exists: the link to a session that was
 * deleted says nothing. A plan that says it is in progress is, whoever carries it out.
 */
export function workStage(row: WorkItemRow, sessions: ReadonlySet<string>): WorkStage {
  const carried = !!row.runner && sessions.has(row.runner);
  if (row.kind === "task") {
    if (row.status === "done" || row.status === "dismissed") return "closed";
    if (row.status === "later") return "later";
    return carried ? "running" : "todo";
  }
  if (row.status === "done") return "closed";
  return carried || row.status === "in-progress" ? "running" : "todo";
}

/** Every item of the listed projects, the plans first within a project, the newest first within a kind. */
export function workItems(projects: readonly WorkItemsProject[], sessions: ReadonlySet<string>): readonly WorkItem[] {
  return projects.flatMap(project => [...project.plans, ...project.tasks].map(row => ({ ...row, projectId: project.projectId, stage: workStage(row, sessions) })));
}

/**
 * The order in which the work items of the projects are read: the selected project, then the projects by the
 * last time one of their sessions changed, then the others as the window lists them.
 */
export function readingOrder(projects: readonly Readonly<{ id: string; archived?: boolean }>[],
  sessions: readonly Readonly<{ projectId?: string | null; updatedAt?: string | null }>[], selectedId: string | null): readonly string[] {
  const latest = new Map<string, string>();
  for (const session of sessions) {
    if (session.projectId && (session.updatedAt ?? "") > (latest.get(session.projectId) ?? "")) latest.set(session.projectId, session.updatedAt ?? "");
  }
  return projects.filter(project => !project.archived).map((project, index) => ({ id: project.id, index, at: latest.get(project.id) ?? "" }))
    .sort((a, b) => Number(b.id === selectedId) - Number(a.id === selectedId) || b.at.localeCompare(a.at) || a.index - b.index).map(project => project.id);
}

/**
 * How many items of a project wait for the user, and how many a session is working on right now. A plan whose
 * file says it is in progress, with no session at work on it, is neither: it is not waiting, and nothing runs.
 */
export type WorkCounts = Readonly<{ open: number; running: number }>;
export function workCounts(items: readonly WorkItem[], working: ReadonlySet<string>): ReadonlyMap<string, WorkCounts> {
  const counts = new Map<string, { open: number; running: number }>();
  for (const item of items) {
    const active = item.stage === "running" && !!item.runner && working.has(item.runner);
    if (item.stage !== "todo" && !active) continue;
    const count = counts.get(item.projectId) ?? { open: 0, running: 0 };
    if (active) count.running++; else count.open++;
    counts.set(item.projectId, count);
  }
  return counts;
}

/**
 * The items a session shows as cards: the tasks it proposed that still wait for a decision, and the plans it
 * had approved that nobody carries out yet. The plans come first: they are what the session was about.
 */
export function sessionCards(items: readonly WorkItem[], sessionId: string): readonly WorkItem[] {
  return items.filter(item => item.proposedBy === sessionId && !item.acknowledged && item.stage === "todo"
    && (item.kind === "task" ? item.status === "pending" : item.status === "approved"))
    .sort((a, b) => (a.kind === b.kind ? 0 : a.kind === "plan" ? -1 : 1) || (a.created ?? "").localeCompare(b.created ?? "") || a.id.localeCompare(b.id));
}

/** The items a session carries out. */
export function carriedBy(items: readonly WorkItem[], sessionId: string): readonly WorkItem[] {
  return items.filter(item => item.runner === sessionId && item.stage === "running");
}

/** The name of the status of an item, as the user reads it. */
export function workStatusLabel(item: Pick<WorkItem, "kind" | "status" | "stage">): MessageKey {
  if (item.stage === "running") return "In progress";
  if (item.kind === "task") return item.status === "later" ? "Later" : item.status === "done" ? "Done" : item.status === "dismissed" ? "Dismissed" : "To do";
  return item.status === "approved" ? "Ready" : item.status === "blocked" ? "Blocked" : item.status === "done" ? "Done" : "Draft";
}

export type WorkTone = "none" | "primary" | "success" | "warning" | "danger";
export function workStatusTone(item: Pick<WorkItem, "kind" | "status" | "stage">): WorkTone {
  if (item.stage === "running") return "primary";
  if (item.status === "done") return "success";
  if (item.status === "blocked") return "danger";
  if (item.status === "approved" || item.status === "pending") return "warning";
  return "none";
}

/** Why a task exists. */
export function taskCategoryLabel(category: string | null | undefined): MessageKey {
  return category === "gap" ? "Gap" : category === "problem" ? "Problem" : "Improvement";
}
export function taskCategoryIcon(category: string | null | undefined): IconName {
  return category === "gap" ? "gap" : category === "problem" ? "problem" : "improvement";
}
export function taskCategoryTone(category: string | null | undefined): WorkTone {
  return category === "problem" ? "danger" : category === "gap" ? "warning" : "primary";
}

export const workKindIcon = (kind: string): IconName => kind === "plan" ? "plan" : "task";

/** The ways of starting an item, the one the user prefers first. `here` needs a session to start it in. */
export function startChoices(preferred: string, here: boolean): readonly WorkStart[] {
  const available = workStarts.filter(start => here || start !== "here");
  const first = available.find(start => start === preferred) ?? available[0];
  return [first, ...available.filter(start => start !== first)];
}
export function startLabel(start: WorkStart): MessageKey {
  return start === "worktree" ? "Start in a new worktree" : start === "session" ? "Start in a new session" : "Do it in this session";
}
export function startDetail(start: WorkStart): MessageKey {
  return start === "worktree" ? "A new session works on it in its own copy of the repository, on a new branch."
    : start === "session" ? "A new session works on it in the folder of the project."
    : "This session does it after its current work.";
}
export const startIcon = (start: WorkStart): IconName => start === "worktree" ? "worktree" : start === "session" ? "newSession" : "queue";

/** What the user chooses among in the list: what waits, what is being done, what is set aside, what is closed. */
export type WorkFilter = WorkStage;
export const workFilters: readonly WorkFilter[] = ["todo", "running", "later", "closed"];
export function workFilterLabel(filter: WorkFilter): MessageKey {
  return filter === "todo" ? "To do" : filter === "running" ? "In progress" : filter === "later" ? "Later" : "Closed";
}

/** The items the list shows: of one project or of all, of one kind or of both, at one stage, matching what is typed. */
export function filterWorkItems(items: readonly WorkItem[], filter: Readonly<{ projectId: string | null; kind: WorkKind | "all"; stage: WorkFilter; query: string }>): readonly WorkItem[] {
  const words = filter.query.trim().toLowerCase().split(/\s+/).filter(Boolean);
  return items.filter(item => (filter.projectId === null || item.projectId === filter.projectId)
    && (filter.kind === "all" || item.kind === filter.kind) && item.stage === filter.stage
    && words.every(word => `${item.title} ${item.summary ?? ""} ${item.id}`.toLowerCase().includes(word)));
}

/** How many items each stage has, for the tabs of the list. */
export function stageCounts(items: readonly WorkItem[], projectId: string | null, kind: WorkKind | "all"): Readonly<Record<WorkStage, number>> {
  const counts = { todo: 0, running: 0, later: 0, closed: 0 };
  for (const item of items) if ((projectId === null || item.projectId === projectId) && (kind === "all" || item.kind === kind)) counts[item.stage]++;
  return counts;
}
