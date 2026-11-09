// What the `#` picker of a prompt lists: the issues of the project's repository, its tasks and plans, and its sessions,
// in one list, the most recent first. Each is inserted as the texts name it: an issue as a Markdown link to its page, a
// task by its short id in code (a chip in a message), a plan as a link to its file, a session as a `session:` link.
import { createContext } from "react";
import type { GitIssueEntry } from "#neoastra";
import type { IconName } from "./AppIcon";
import { insertIssueReference } from "./issueReferences";
import { closedTask } from "./workItems/taskReferences";
import { taskCategoryIcon, taskCategoryTone, type WorkItem, type WorkTone } from "./workItems/workItems";

/** The kinds of things `#` names. */
export type ReferenceKind = "issue" | "task" | "plan" | "session";
/** The kinds in the order of their toggles, and of their keys (Ctrl+1 to Ctrl+4). */
export const referenceKinds: readonly ReferenceKind[] = ["issue", "task", "plan", "session"];
/** The kinds `#` lists when it opens: sessions are named less often, and are a key away. */
export const defaultReferenceKinds: ReadonlySet<ReferenceKind> = new Set<ReferenceKind>(["issue", "task", "plan"]);

/** A session `#` can name. */
export type ReferenceSession = Readonly<{ id: string; title: string; updatedAt: string | null; running: boolean }>;

/** What the window gives `#` beyond the issues, which the picker searches itself: the tasks, the plans and the sessions of a project. */
export type ReferenceSources = Readonly<{
  /** The tasks and the plans of a project. */
  workItems(projectId: string): readonly WorkItem[];
  /** The sessions of a project that are not archived. */
  sessions(projectId: string): readonly ReferenceSession[];
}>;

/** Given by the window. Without it (a fixture, the TUI) `#` lists the issues only. */
export const ReferenceSourcesContext = createContext<ReferenceSources | null>(null);

/** A row of the picker. */
export type ReferenceItem = Readonly<{
  kind: ReferenceKind;
  /** Unique among the rows. */
  key: string;
  /** What names it in short: `#123`, the short id of a task; empty when nothing does. */
  label: string;
  title: string;
  /** When it last changed, in milliseconds; 0 when not known. */
  time: number;
  closed: boolean;
  /** The state, as the user reads it (the caller translates the keys of tasks, plans and sessions). */
  state: string;
  icon: IconName;
  tone: WorkTone | "open" | "closed" | "running" | "idle";
  /** The text inserted in the prompt. */
  insert: string;
  /** Other text a search finds it by (an id). */
  words: string;
  /** For an issue: what the attach menu attaches. */
  issue?: Readonly<{ number: number; url: string; title: string }>;
}>;

const timeOf = (value: string | null | undefined) => {
  const time = value ? Date.parse(value) : NaN;
  return Number.isFinite(time) ? time : 0;
};

/** The text of a Markdown link that shows a title as it is written: brackets and backslashes are escaped, lines joined. */
export function linkText(title: string): string {
  return title.replace(/\s+/gu, " ").trim().replace(/[\\[\]]/gu, "\\$&") || "…";
}

export function issueReference(issue: GitIssueEntry): ReferenceItem | null {
  const insert = insertIssueReference("", 0, 0, issue.number, issue.url)?.text;
  if (!insert) return null;
  return { kind: "issue", key: `issue:${issue.kind}:${issue.number}`, label: `#${issue.number}`, title: issue.title, time: timeOf(issue.updatedAt),
    closed: !issue.open, state: issue.state, icon: issue.kind === "pull_request" ? "pullRequest" : issue.open ? "issueOpen" : "issueClosed",
    tone: issue.open ? "open" : "closed", insert, words: String(issue.number), issue: { number: issue.number, url: issue.url, title: issue.title } };
}

export function workReference(item: WorkItem): ReferenceItem | null {
  const updated = timeOf(item.updated) || timeOf(item.created);
  if (item.kind === "task") {
    const name = item.shortId ?? item.id;
    return { kind: "task", key: `task:${item.id}`, label: item.shortId ?? "", title: item.title, time: updated, closed: closedTask(item), state: item.status,
      icon: taskCategoryIcon(item.category), tone: taskCategoryTone(item.category), insert: `\`${name}\``, words: item.id };
  }
  if (item.kind === "plan" && /^[^\s()<>]+$/u.test(item.file)) {
    return { kind: "plan", key: `plan:${item.id}`, label: "", title: item.title, time: updated, closed: item.status === "done", state: item.status,
      icon: "plan", tone: "primary", insert: `[${linkText(item.title)}](${item.file})`, words: item.id };
  }
  return null;
}

export function sessionReference(session: ReferenceSession): ReferenceItem | null {
  if (!/^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$/u.test(session.id)) return null;
  return { kind: "session", key: `session:${session.id}`, label: "", title: session.title, time: timeOf(session.updatedAt), closed: false,
    state: session.running ? "running" : "idle", icon: "chat", tone: session.running ? "running" : "idle",
    insert: `[${linkText(session.title)}](session:${session.id})`, words: session.id };
}

/** Whether a row is found by what is typed: each word of the query is in its name, its title or its id, whatever the case. */
export function matchesReference(item: ReferenceItem, query: string): boolean {
  const words = query.trim().toLowerCase().replace(/^#/u, "").split(/\s+/u).filter(Boolean);
  if (!words.length) return true;
  const text = `${item.label} ${item.title} ${item.words}`.toLowerCase();
  return words.every(word => text.includes(word));
}

/**
 * The rows the picker shows: those of the kinds turned on, closed ones only when asked, the local ones that match the
 * query (the issues are searched by the host), the most recent first.
 */
export function referenceRows(items: readonly ReferenceItem[], kinds: ReadonlySet<ReferenceKind>, includeClosed: boolean, query: string): readonly ReferenceItem[] {
  return items.filter(item => kinds.has(item.kind) && (includeClosed || !item.closed) && (item.kind === "issue" || matchesReference(item, query)))
    .map((item, index) => ({ item, index }))
    .sort((a, b) => b.item.time - a.item.time || a.index - b.index)
    .map(entry => entry.item);
}

/**
 * Replaces the `#token`, or inserts at the caret, the references chosen, separated by spaces. An insertion right
 * after a word starts with a space.
 */
export function insertReferences(text: string, start: number, end: number, inserts: readonly string[]) {
  if (inserts.length === 0) return null;
  const separated = start === 0 || /[\s(\[{]/u.test(text[start - 1]);
  const piece = (separated ? "" : " ") + inserts.join(" ");
  const value = text.slice(0, start) + piece + text.slice(end);
  return value.length <= 32768 ? { text: value, caret: start + piece.length } : null;
}

/** The kind a key toggles in the picker: Ctrl+1 to Ctrl+4 (with Shift, it shows that kind alone). */
export function referenceKindOfKey(event: Pick<KeyboardEvent, "code" | "ctrlKey" | "metaKey" | "altKey">): ReferenceKind | null {
  if (!(event.ctrlKey || event.metaKey) || event.altKey) return null;
  const match = /^(?:Digit|Numpad)([1-4])$/u.exec(event.code);
  return match ? referenceKinds[Number(match[1]) - 1] : null;
}

/** The kinds after a toggle: a kind turned on or off, or alone. The last kind on stays on. */
export function toggledKinds(kinds: ReadonlySet<ReferenceKind>, kind: ReferenceKind, alone: boolean): ReadonlySet<ReferenceKind> {
  if (alone) return new Set([kind]);
  const next = new Set(kinds);
  if (!next.delete(kind)) next.add(kind);
  return next.size ? next : kinds;
}
