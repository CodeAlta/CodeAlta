// What the page shows of the work items of the projects: the follow-up tasks agents propose and the plans.
import type { WorkItemRow, WorkItemsProject, WorkItemsSettings, WorkItemTagRow } from "#neoastra";
import { seriesColorCount, seriesVariable } from "../charts/palette";
import type { IconName } from "../AppIcon";
import type { MessageKey } from "../localization";

export type WorkKind = "task" | "plan" | "review";
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

/** Whether the session that shows an item carries it out: the card says so instead of offering to start it. */
export const startedBySession = (item: WorkItem, sessionId: string) => item.stage === "running" && item.runner === sessionId;

/**
 * The items a session shows as cards: the tasks it proposed that still wait for a decision, the tasks it proposed
 * and started itself until the user put their card away, and the plans it had approved that nobody carries out
 * yet. The plans come first: they are what the session was about.
 */
export function sessionCards(items: readonly WorkItem[], sessionId: string): readonly WorkItem[] {
  return items.filter(item => item.proposedBy === sessionId && !item.acknowledged
    && (item.kind === "task" ? item.status === "pending" && (item.stage === "todo" || startedBySession(item, sessionId)) : item.stage === "todo" && item.status === "approved"))
    .sort((a, b) => (a.kind === b.kind ? 0 : a.kind === "plan" ? -1 : 1) || (a.created ?? "").localeCompare(b.created ?? "") || a.id.localeCompare(b.id));
}

/** The items a session carries out. */
export function carriedBy(items: readonly WorkItem[], sessionId: string): readonly WorkItem[] {
  return items.filter(item => item.runner === sessionId && item.stage === "running");
}

/** The name of the status of an item, as the user reads it. */
export function workStatusLabel(item: Pick<WorkItem, "kind" | "status" | "stage">): MessageKey {
  // A review says who it waits for.
  if (item.kind === "review") return item.stage === "todo" ? "Waiting for you" : item.stage === "running" ? "Waiting for the agent" : "Done";
  if (item.stage === "running") return "In progress";
  if (item.kind === "task") return item.status === "later" ? "Backlog" : item.status === "done" ? "Done" : item.status === "dismissed" ? "Dismissed" : "Inbox";
  return item.status === "approved" ? "Ready" : item.status === "blocked" ? "Blocked" : item.status === "done" ? "Done" : "Draft";
}

export type WorkTone = "none" | "primary" | "success" | "warning" | "danger";
export function workStatusTone(item: Pick<WorkItem, "kind" | "status" | "stage">): WorkTone {
  if (item.kind === "review") return item.stage === "todo" ? "warning" : item.stage === "running" ? "primary" : "success";
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

/** The color of an item's icon and short id: a task's category, the warning of a review, the accent of a plan. */
export function workItemTone(item: Pick<WorkItem, "kind" | "category">): WorkTone {
  return item.kind === "task" ? taskCategoryTone(item.category) : item.kind === "review" ? "warning" : "primary";
}

export const workKindIcon = (kind: string): IconName => kind === "plan" ? "plan" : kind === "review" ? "eye" : "task";

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
  return filter === "todo" ? "Inbox" : filter === "running" ? "In progress" : filter === "later" ? "Backlog" : "Closed";
}

/**
 * The items the list shows: of one project or of all, of one kind or of both, at one stage, that have one of the
 * chosen tags, matching what is typed. A typed word that starts with `#` is a tag the item must have.
 */
export function filterWorkItems(items: readonly WorkItem[], filter: Readonly<{ projectId: string | null; kind: WorkKind | "all"; stage: WorkFilter; query: string;
  tags?: ReadonlySet<string> }>): readonly WorkItem[] {
  const all = filter.query.trim().toLowerCase().split(/\s+/).filter(Boolean);
  const typedTags = all.filter(word => word.startsWith("#") && word.length > 1).map(word => word.slice(1));
  const words = all.filter(word => !word.startsWith("#"));
  const chosen = filter.tags ?? noTags;
  return items.filter(item => (filter.projectId === null || item.projectId === filter.projectId)
    && (filter.kind === "all" || item.kind === filter.kind) && item.stage === filter.stage
    && (chosen.size === 0 || (item.tags ?? []).some(tag => chosen.has(tag)))
    && typedTags.every(typed => (item.tags ?? []).includes(typed))
    && words.every(word => `${item.title} ${item.summary ?? ""} ${item.id}`.toLowerCase().includes(word)));
}

const noTags: ReadonlySet<string> = new Set();

/** The tags of the projects shown: one project's, or those of all the projects by name, with their open tasks added. */
export function shownTags(projects: readonly WorkItemsProject[], projectId: string | null): readonly WorkItemTagRow[] {
  const byName = new Map<string, WorkItemTagRow>();
  for (const project of projects) {
    if (projectId !== null && project.projectId !== projectId) continue;
    for (const tag of project.tags ?? []) {
      const known = byName.get(tag.name);
      byName.set(tag.name, known ? { ...known, description: known.description ?? tag.description, openTasks: known.openTasks + tag.openTasks, listed: known.listed || tag.listed } : tag);
    }
  }
  return [...byName.values()];
}

/** The color of a tag, which comes from its name: one of the colors of the charts, which read on both themes. */
export function tagColor(name: string): string {
  let hash = 0;
  for (const character of name) hash = (hash * 31 + character.charCodeAt(0)) >>> 0;
  return `var(${seriesVariable(hash % seriesColorCount)})`;
}

/** A tag name as a person may type it: trimmed, in lower case, without a leading `#`; null when it is not one. */
export function tagNameOf(text: string): string | null {
  const name = text.trim().replace(/^#/, "").toLowerCase();
  return name.length <= 32 && /^[a-z0-9]+(-[a-z0-9]+)*$/.test(name) ? name : null;
}

/** The order of the list: the newest items first, or the oldest. */
export type WorkOrder = "newest" | "oldest";
/** What the list is grouped by: the day an item was created (Today, Yesterday, This week…), or its project. */
export type WorkGrouping = "date" | "project";

/**
 * The day an item was created, at midnight here; null when it does not say. A task or a plan says only its day
 * (`2026-11-08`), which is that day here, not the midnight of UTC; a review says the moment it changed.
 */
export function createdDay(created: string | null | undefined): Date | null {
  const text = created?.trim();
  if (!text) return null;
  const day = /^(\d{4})-(\d{2})-(\d{2})$/u.exec(text);
  const date = day ? new Date(Number(day[1]), Number(day[2]) - 1, Number(day[3])) : new Date(text);
  return Number.isNaN(date.getTime()) ? null : new Date(date.getFullYear(), date.getMonth(), date.getDate());
}

/**
 * The items in the order chosen: by the moment they were created, to the day for a task or a plan and to the moment for a
 * review; an item that does not say comes last. The same moment is ordered by title, so that the list does not move.
 */
export function sortWorkItems(items: readonly WorkItem[], order: WorkOrder): WorkItem[] {
  const sign = order === "newest" ? -1 : 1;
  return items.map(item => ({ item, at: createdTime(item.created) })).sort((left, right) => {
    if (left.at === null || right.at === null) return left.at === right.at ? left.item.title.localeCompare(right.item.title) : left.at === null ? 1 : -1;
    return (left.at - right.at) * sign || left.item.title.localeCompare(right.item.title);
  }).map(entry => entry.item);
}

// The moment an item was created: the midnight here of the day of a task or a plan, the moment of a review.
function createdTime(created: string | null | undefined): number | null {
  const text = created?.trim();
  if (!text) return null;
  if (/^\d{4}-\d{2}-\d{2}$/u.test(text)) return createdDay(text)?.getTime() ?? null;
  const time = new Date(text).getTime();
  return Number.isNaN(time) ? null : time;
}

/** The group of the day an item was created, as the list names it: a day close to today, a week, or a month. */
export type WorkDateGroup = Readonly<{ key: string; label: Readonly<{ key: MessageKey }> | Readonly<{ month: Date }> | null }>;

/**
 * The group an item falls in, seen from `now`: Today, Yesterday, This week (from Monday), Last week, then the month it was
 * created in; an item that does not say when it was created is in a group of its own.
 */
export function workDateGroup(created: string | null | undefined, now: Date): WorkDateGroup {
  const day = createdDay(created);
  if (!day) return { key: "unknown", label: null };
  const today = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  const days = Math.round((today.getTime() - day.getTime()) / 86_400_000);
  // Monday starts a week; a day of the future (a clock that differs) is today.
  const monday = new Date(today.getFullYear(), today.getMonth(), today.getDate() - (today.getDay() + 6) % 7);
  const lastMonday = new Date(monday.getFullYear(), monday.getMonth(), monday.getDate() - 7);
  if (days <= 0) return { key: "today", label: { key: "Today" } };
  if (days === 1) return { key: "yesterday", label: { key: "Yesterday" } };
  if (day >= monday) return { key: "this-week", label: { key: "This week" } };
  if (day >= lastMonday) return { key: "last-week", label: { key: "Last week" } };
  return { key: `${day.getFullYear()}-${day.getMonth() + 1}`, label: { month: new Date(day.getFullYear(), day.getMonth(), 1) } };
}

/** How many items each stage has, for the tabs of the list. */
export function stageCounts(items: readonly WorkItem[], projectId: string | null, kind: WorkKind | "all"): Readonly<Record<WorkStage, number>> {
  const counts = { todo: 0, running: 0, later: 0, closed: 0 };
  for (const item of items) if ((projectId === null || item.projectId === projectId) && (kind === "all" || item.kind === kind)) counts[item.stage]++;
  return counts;
}