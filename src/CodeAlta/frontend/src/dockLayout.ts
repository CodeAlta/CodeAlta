import type { IJsonRowNode, Model } from "flexlayout-react";
import { createSessionTabModel } from "./sessionTabLayout";

/** Where the main window keeps the arrangement of the dock of a space (a secondary window keeps it in its page). */
export const dockLayoutKey = "codealta.desktop.dockLayout.v1";
/** What a window keeps of its page is bounded: an arrangement that does not fit is not kept. */
const textLimit = 262_144;

type Json = { type?: unknown; id?: unknown; children?: unknown; selected?: unknown; [key: string]: unknown };
const nodes = (value: unknown): Json[] => Array.isArray(value) ? value.filter((node): node is Json => !!node && typeof node === "object") : [];

const collectTabs = (node: Json): string[] => node.type === "tab" ? (typeof node.id === "string" ? [node.id] : []) : nodes(node.children).flatMap(collectTabs);

/**
 * The tabs of a pane that are still `valid`, the one in front kept in front. Rows keep what is left in them, with the
 * sizes they had: a size is a weight among its neighbors, so it comes back as the share of the window it was.
 */
function keep(node: Json, valid: ReadonlySet<string>): Json | null {
  if (node.type === "tabset") {
    const all = nodes(node.children);
    const tabs = all.filter(tab => tab.type === "tab" && typeof tab.id === "string" && valid.has(tab.id));
    if (!tabs.length) return null;
    const front = typeof node.selected === "number" ? all[node.selected]?.id : undefined;
    return { ...node, children: tabs, selected: Math.max(0, tabs.findIndex(tab => tab.id === front)) };
  }
  if (node.type !== "row") return null;
  const children = nodes(node.children).map(child => keep(child, valid)).filter((child): child is Json => child !== null);
  return children.length ? { ...node, children } : null;
}

/**
 * The arrangement of a dock as text: its panes, how they are split and their sizes, the tabs of each by identity and
 * the one in front of each. Null when the dock holds no tab, which is not an arrangement worth keeping, or when the text
 * would be too long for a page to keep.
 */
export function serializeDockLayout(model: Model): string | null {
  const layout = model.toJson().layout;
  if (!collectTabs(layout as Json).length) return null;
  const text = JSON.stringify({ version: 1, layout });
  return text.length <= textLimit ? text : null;
}


/**
 * A dock arranged as kept in `text`, with the tabs that are `valid` (the tabs the window restores) in the panes they
 * were in. Null when nothing was kept, the text is not understood, or none of its tabs is valid: the dock then has
 * its single pane, and the tabs of the window the places they take there.
 */
export function restoreDockLayout(text: string | null | undefined, valid: ReadonlySet<string>): Model | null {
  try {
    const value: unknown = text ? JSON.parse(text) : null;
    if (!value || typeof value !== "object" || (value as { version?: unknown }).version !== 1 || text!.length > textLimit) return null;
    const layout = keep((value as { layout?: Json }).layout ?? {}, valid);
    return layout?.type === "row" ? createSessionTabModel(layout as unknown as IJsonRowNode) : null;
  } catch { return null; }
}
