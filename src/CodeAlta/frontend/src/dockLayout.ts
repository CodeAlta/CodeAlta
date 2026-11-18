import type { IJsonTabNode, Model } from "flexlayout-react";
import { createSessionTabModel, sessionDraftNodeId } from "./sessionTabLayout";

/** Where the main window keeps the arrangement of the dock of a space (a secondary window keeps it in its page). */
export const dockLayoutKey = "codealta.desktop.dockLayout.v1";
/** What a window keeps of its page is bounded: an arrangement that does not fit is not kept. */
const textLimit = 262_144;

type Row = { type: "row"; weight?: number; children: (Row | Pane)[] };
type Pane = { type: "tabset"; weight?: number; selected: number; active?: true; children: IJsonTabNode[] };

function record(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== "object" || Array.isArray(value)) throw new Error("Invalid dock node");
  return value as Record<string, unknown>;
}

// FlexLayout draws the content of a tab only when the tab names a component; the dock chooses that content by the id.
const tabComponent = "tab";

// What the dock reads back of the `config` of a tab: the transient tab and the preview of a Claude Code conversation,
// whose places the next one takes.
function flags(config: unknown): IJsonTabNode["config"] {
  if (!config || typeof config !== "object") return undefined;
  const { preview, claudeCodePreview } = config as Record<string, unknown>;
  return claudeCodePreview === true ? { claudeCodePreview: true } : typeof preview === "boolean" ? { preview } : undefined;
}

// Keep only arrangement data. FlexLayout accepts node attributes without validating them; a stored pane must
// not override the dock's behavior or even claim a tab's id. Labels and tab behavior come from the window.
function keep(value: unknown, valid: ReadonlySet<string> | undefined, seen = new Set<string>()): Row | Pane | null {
  const { type, weight, selected, active, children } = record(value);
  if (!Array.isArray(children) || weight !== undefined && (typeof weight !== "number" || !(weight >= 0 && weight <= Number.MAX_SAFE_INTEGER)))
    throw new Error("Invalid dock children or weight");
  if (type === "tabset") {
    const tabs: IJsonTabNode[] = [];
    const front = typeof selected === "number" && Number.isInteger(selected) ? selected : 0;
    let nextSelected = 0;
    for (const [index, child] of children.entries()) {
      const { type, id, config } = record(child);
      if (type !== "tab" || typeof id !== "string" || !id || seen.has(id)) throw new Error("Invalid dock tab");
      seen.add(id);
      if (valid && !valid.has(id)) continue;
      if (index === front) nextSelected = tabs.length;
      tabs.push(id === sessionDraftNodeId ? { type, id, component: tabComponent, enableDrag: false, config: { preview: true } }
        : { type, id, component: tabComponent, config: flags(config) });
    }
    return tabs.length ? { type, weight, children: tabs, selected: nextSelected, active: active === true ? true : undefined } : null;
  }
  if (type !== "row") throw new Error("Invalid dock row");
  const panes = children.map(child => keep(child, valid, seen)).filter(child => child !== null);
  return panes.length ? { type, weight, children: panes } : null;
}

/**
 * The arrangement of a dock as text: its panes, how they are split and their sizes, the tabs of each by identity and
 * the one in front of each. Null when the dock holds no tab, which is not an arrangement worth keeping, or when the text
 * would be too long for a page to keep.
 */
export function serializeDockLayout(model: Model): string | null {
  try {
    const layout = keep(model.toJson().layout, undefined);
    if (!layout) return null;
    const text = JSON.stringify({ version: 1, layout });
    return text.length <= textLimit ? text : null;
  } catch { return null; }
}

/**
 * A dock arranged as kept in `text`, with the tabs that are `valid` (the tabs the window restores) in the panes they
 * were in. Null when nothing was kept, the text is not understood, or none of its tabs is valid: the dock then has
 * its single pane, and the tabs of the window the places they take there.
 */
export function restoreDockLayout(text: string | null | undefined, valid: ReadonlySet<string>): Model | null {
  if (!text || text.length > textLimit) return null;
  try {
    const { version, layout } = record(JSON.parse(text));
    if (version !== 1) return null;
    const kept = keep(layout, valid);
    return kept?.type === "row" ? createSessionTabModel(kept) : null;
  } catch { return null; }
}
