import type { WorkspaceSession } from "#neoastra";
import type { Label } from "../labels/labels";
import type { SessionTreeEntry } from "./sessionTree";

/** What the folding of a group of sessions of a scope is kept under, beside the labels of projects: no label id has a colon. */
export const sessionLabelGroupKey = (scope: string, labelId: string | null): string => `sessions:${scope}:${labelId ?? ""}`;

/**
 * The list of the sessions of a scope under their labels: a title for each label one of its sessions has, in the order of
 * the labels, followed by its sessions, then a title for the sessions of no label. A session is listed once, under the
 * first of its labels, with its sub-agents; a folded title hides them. A list where no session has a label is left as it
 * is: it needs no title.
 * @param entries The list of the scope, in tree order.
 * @param labels The labels for sessions of the space, in the order of their tree.
 * @param scope The scope (`scopeKey`).
 * @param folded Whether the group kept under a key is folded.
 */
export function groupSessionsByLabels(entries: readonly SessionTreeEntry[], labels: readonly Label[], scope: string,
  folded: (key: string) => boolean): SessionTreeEntry[] {
  // The blocks of the list: a session at the top, then what is under it (its sub-agents, the links to those listed elsewhere).
  const blocks: { top: WorkspaceSession | null; entries: SessionTreeEntry[] }[] = [];
  for (const entry of entries) {
    if (entry.kind === "label") continue;
    if (entry.kind === "session" && entry.row.depth === 0 || !blocks.length) blocks.push({ top: entry.kind === "session" ? entry.row.session : null, entries: [] });
    blocks[blocks.length - 1].entries.push(entry);
  }
  const byLabel = new Map<string, typeof blocks>();
  const other: typeof blocks = [];
  for (const block of blocks) {
    const named = block.top?.labels;
    const first = named?.length ? labels.find(label => named.includes(label.id)) : undefined;
    if (!first) { other.push(block); continue; }
    byLabel.set(first.id, [...byLabel.get(first.id) ?? [], block]);
  }
  if (!byLabel.size) return [...entries];
  const grouped: SessionTreeEntry[] = [];
  const add = (label: Label | null, members: typeof blocks) => {
    const key = sessionLabelGroupKey(scope, label?.id ?? null);
    const hidden = folded(key);
    grouped.push({ kind: "label", label, key, folded: hidden, sessions: members.flatMap(block => block.top ? [block.top] : []) });
    if (!hidden) for (const block of members) grouped.push(...block.entries);
  };
  for (const label of labels) if (byLabel.has(label.id)) add(label, byLabel.get(label.id)!);
  if (other.length) add(null, other);
  return grouped;
}
