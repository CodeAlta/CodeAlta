import { askWaiting } from "./ask";
import { isBusy } from "./contract";
import { lastActive, type SessionView } from "./sessionStore";

// The list of sessions as a tree, without the DOM: a sub-agent (a session another one started) is shown under the session
// that started it, never above it, and the sub-agents of a session are folded under one summary row until it is opened.

/** What the folded sub-agents of a session are doing, as the summary row of its card says it. */
export type SubAgents = Readonly<{
  /** The sessions it started itself. */
  count: number;
  /** Those of its whole branch that work now. */
  running: number;
  /** Those of its whole branch that wait for an answer of the user. */
  waiting: number;
  /** The sessions it started, in the order of the list: the dots of the summary row. */
  children: readonly SessionView[];
  /** The asks its branch waits on: one that was not there when the user folded it opens it again. */
  asks: string;
}>;

/** A row of the list: a session, how deep it is, and the lines that join it to its parent. */
export type TreeRow = Readonly<{
  view: SessionView;
  /** 0 for a session of its own, 1 for a sub-agent, 2 for a sub-agent of a sub-agent… */
  depth: number;
  /**
   * The lines of the tree on the left of the row, one per level above 1: whether the branch of that level goes on below
   * this row (the ancestor at that depth has a sibling after it).
   */
  rails: readonly boolean[];
  /** Whether a sibling follows this sub-agent: its line goes on down past it. */
  more: boolean;
  /** Its sub-agents, or null when it started none that is listed. */
  subAgents: SubAgents | null;
  /** Whether its sub-agents are shown under it. */
  open: boolean;
  /** A sub-agent whose parent is not in the list: shown on its own, with the parent's title (null when the phone does not know it). */
  parentElsewhere?: Readonly<{ title: string | null }>;
}>;

/** Whether the sub-agents of a session are shown, from the asks its branch waits on now. */
export type FoldQuery = (id: string, asks: string) => boolean;

/** The sessions it starts, its parent, and the activity of its whole branch: the order of the list. */
type Node = { view: SessionView; parent: Node | null; children: Node[]; activity: number };

/**
 * Orders sessions as the list shows them, the latest active first, but an order already shown is kept: those of `held` stay in
 * their order, and one that is not there yet goes right after the session it follows in the latest-first order, so that nothing
 * already shown moves for it.
 */
export function keepOrder<T>(latestFirst: readonly T[], held: ReadonlyMap<T, number> | null): T[] {
  if (!held) return [...latestFirst];
  const kept = latestFirst.filter(item => held.has(item)).sort((a, b) => held.get(a)! - held.get(b)!);
  let previous: T | null = null;
  for (const item of latestFirst) {
    if (!held.has(item)) kept.splice(previous === null ? 0 : kept.indexOf(previous) + 1, 0, item);
    previous = item;
  }
  return kept;
}

/**
 * The rows of the list. A session whose parent is listed is under it, the parent being as recent as the latest of its branch;
 * a session whose parent is not listed (another project, another space, archived) is a row of its own that names it. Siblings
 * are the latest active first, unless `held` gives the order the rows were last shown in (see `keepOrder`). The sub-agents of
 * a session are under it only when `open` says so.
 */
export function sessionTree(views: readonly SessionView[], options: Readonly<{ open: FoldQuery; held?: ReadonlyMap<string, number> | null }>): TreeRow[] {
  const nodes = new Map<string, Node>(views.map(view => [view.id, { view, parent: null, children: [], activity: lastActive(view) }]));
  for (const node of nodes.values()) {
    const parent = node.view.parentId ? nodes.get(node.view.parentId) : undefined;
    if (!parent || parent === node) continue;
    // A lineage that loops is cut where it would close: the session stays a row of its own.
    let ancestor: Node | null = parent;
    while (ancestor && ancestor !== node) ancestor = ancestor.parent;
    if (ancestor === node) continue;
    node.parent = parent;
    parent.children.push(node);
  }
  for (const node of nodes.values()) for (let up = node.parent; up; up = up.parent) up.activity = Math.max(up.activity, node.activity);

  const latestFirst = (list: readonly Node[]) => [...list].sort((a, b) => b.activity - a.activity
    || (a.view.title ?? "").localeCompare(b.view.title ?? "", undefined, { sensitivity: "base" }) || a.view.id.localeCompare(b.view.id));
  const ordered = (list: readonly Node[]) => {
    const byId = keepOrder(latestFirst(list).map(node => node.view.id), options.held ?? null);
    return byId.map(id => nodes.get(id)!);
  };

  const rows: TreeRow[] = [];
  const visit = (node: Node, depth: number, rails: readonly boolean[], more: boolean) => {
    const children = ordered(node.children);
    const subAgents = children.length ? summarize(node, children) : null;
    const open = !!subAgents && options.open(node.view.id, subAgents.asks);
    const orphan = !node.parent && node.view.parentId && node.view.parentId !== node.view.id;
    rows.push({ view: node.view, depth, rails, more, subAgents, open, ...(orphan ? { parentElsewhere: { title: node.view.parentTitle ?? null } } : {}) });
    if (!open) return;
    const below = depth === 0 ? rails : [...rails, more];
    children.forEach((child, index) => visit(child, depth + 1, below, index < children.length - 1));
  };
  for (const root of ordered([...nodes.values()].filter(node => !node.parent))) visit(root, 0, [], false);
  return rows;
}

function summarize(node: Node, children: readonly Node[]): SubAgents {
  let running = 0, waiting = 0;
  const asks: string[] = [];
  const walk = (list: readonly Node[]) => {
    for (const child of list) {
      const live = child.view.live;
      if (askWaiting(live)) { waiting++; asks.push(`${child.view.id}:${live!.asking!.id}`); }
      else if (isBusy(live?.live)) running++;
      walk(child.children);
    }
  };
  walk(children);
  return { count: children.length, running, waiting, children: children.map(child => child.view), asks: asks.sort().join(" ") };
}

/** How many cards of their own sessions take: those whose parent is not among them. */
export function topLevelCount(views: readonly SessionView[]): number {
  const ids = new Set(views.map(view => view.id));
  return views.filter(view => !view.parentId || view.parentId === view.id || !ids.has(view.parentId)).length;
}

/** The summary row of folded sub-agents: "3 sub-agents", then what they do ("1 running", "1 waits for you"). */
export function subAgentWords(subAgents: SubAgents): { count: string; states: string[] } {
  const states: string[] = [];
  if (subAgents.waiting) states.push(subAgents.waiting === 1 ? "1 waits for you" : `${subAgents.waiting} wait for you`);
  if (subAgents.running) states.push(`${subAgents.running} running`);
  return { count: subAgents.count === 1 ? "1 sub-agent" : `${subAgents.count} sub-agents`, states };
}

/** Where the choices of the user are kept: the page's storage. */
export type FoldStore = Readonly<{ get(key: string): string | null; set(key: string, value: string): void }>;

/** The key of the folds in the storage of the page. */
export const foldKey = "alta.subagents";
const maximumFolds = 200;

/**
 * Whether the sub-agents of each session are shown: folded until the user opens them, as the user left them (kept on the phone,
 * the latest 200), and opened by themselves when one of them waits for the user on an ask that was not there when the user
 * folded them, so that a question is not missed under a fold.
 */
export function createFolds(store: FoldStore) {
  const read = (): Record<string, readonly [boolean, string]> => {
    try {
      const value: unknown = JSON.parse(store.get(foldKey) ?? "{}");
      return value && typeof value === "object" && !Array.isArray(value) ? value as Record<string, readonly [boolean, string]> : {};
    } catch { return {}; }
  };
  let choices = read();
  return {
    /** Whether the sub-agents of a session are shown, with the asks its branch waits on now. */
    open(id: string, asks: string): boolean {
      const choice = choices[id];
      if (!Array.isArray(choice)) return asks !== "";
      return asks !== "" && asks !== choice[1] ? true : !!choice[0];
    },
    /** The user opened or folded the sub-agents of a session, while its branch waited on these asks. */
    set(id: string, open: boolean, asks: string) {
      const { [id]: _, ...others } = read();
      const kept = Object.entries(others).slice(-(maximumFolds - 1));
      choices = Object.fromEntries([...kept, [id, [open, asks] as const]]);
      store.set(foldKey, JSON.stringify(choices));
    },
  };
}
