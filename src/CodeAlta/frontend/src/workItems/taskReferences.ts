// The tasks a text names in code, by their id or their short id (`p53b`): the text shows them as chips that
// say what the task is and open it in the Work items page.
import { createContext } from "react";
import type { MarkdownLinkScope } from "../markdownLinks";
import { taskCategoryTone, type WorkItem } from "./workItems";

/** What finds the tasks the texts of the window name, and shows one. */
export type TaskReferences = Readonly<{
  /** The task of the project of a text that a code span of it names; null when it names none. */
  find(scope: MarkdownLinkScope | null, text: string): WorkItem | null;
  /** Shows the task in the Work items page. */
  open(item: WorkItem): void;
}>;

/** The task references of the window; null where texts name no task (a window without a host, the preview of a folded row). */
export const TaskReferencesContext = createContext<TaskReferences | null>(null);

/** The attribute of a code span that names a task: the id of that task. */
export const taskReferenceAttribute = "data-task-ref";

// No id is longer: a longer code span is not read.
const maximumReference = 160;

/**
 * The tasks of each project by their id and by their short id. A short id that two tasks of a project share (two
 * checkouts each wrote one) names neither: the chip would open one of them by chance.
 */
export function taskReferenceIndex(items: readonly WorkItem[]): ReadonlyMap<string, ReadonlyMap<string, WorkItem>> {
  const projects = new Map<string, Map<string, WorkItem | null>>();
  for (const item of items) {
    if (item.kind !== "task") continue;
    let names = projects.get(item.projectId);
    if (!names) projects.set(item.projectId, names = new Map());
    names.set(item.id, item);
    if (item.shortId) names.set(item.shortId, names.has(item.shortId) ? null : item);
  }
  const index = new Map<string, ReadonlyMap<string, WorkItem>>();
  for (const [projectId, names] of projects) index.set(projectId, new Map([...names].filter((entry): entry is [string, WorkItem] => entry[1] !== null)));
  return index;
}

/** The task of a project that the text of a code span names, as an agent writes it: its short id or its id, alone. */
export function findTaskReference(index: ReadonlyMap<string, ReadonlyMap<string, WorkItem>>, projectId: string | null | undefined, text: string): WorkItem | null {
  if (!projectId || text.length > maximumReference) return null;
  return index.get(projectId)?.get(text.trim()) ?? null;
}

/** Whether a task is closed: done or dismissed, it shows struck through. */
export const closedTask = (item: Pick<WorkItem, "status">) => item.status === "done" || item.status === "dismissed";

/**
 * Marks the code spans of a rendered text that name a task, and unmarks the ones that no longer do (a task that was
 * removed). A code block, and a code span inside a link or a button, keep what they are.
 */
export function dressTaskReferences(root: HTMLElement, find: ((text: string) => WorkItem | null) | null, label: (item: WorkItem) => string) {
  for (const code of Array.from(root.querySelectorAll<HTMLElement>("code"))) {
    const marked = code.hasAttribute(taskReferenceAttribute);
    if (!find && !marked) continue;
    const item = find && !code.closest("pre, a, button") ? find(code.textContent ?? "") : null;
    if (!item) {
      if (marked) for (const name of [taskReferenceAttribute, "data-task-tone", "data-task-closed", "tabindex", "role", "aria-label"]) code.removeAttribute(name);
      continue;
    }
    code.setAttribute(taskReferenceAttribute, item.id);
    code.setAttribute("data-task-tone", taskCategoryTone(item.category));
    code.toggleAttribute("data-task-closed", closedTask(item));
    code.setAttribute("tabindex", "0");
    code.setAttribute("role", "link");
    code.setAttribute("aria-label", label(item));
  }
}

/** The code span of a task that an event happened on, in a text. */
export function taskReferenceAt(target: EventTarget | null, root: HTMLElement | null): HTMLElement | null {
  const code = target instanceof Element ? target.closest<HTMLElement>(`code[${taskReferenceAttribute}]`) : null;
  return code && root?.contains(code) ? code : null;
}
