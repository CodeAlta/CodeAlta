import { liveTextItem } from "../liveTimeline";
import { promptProblem } from "../systemPrompt";
import type { ReconciledRow } from "../reconcileTimeline";
import type { TimelineGroup } from "../toolGroups";
import type { BackgroundCallState } from "../backgroundTasks";

/**
 * The types of entries the timeline can dim, each on its own. A prompt starts a turn and the last answer of a turn
 * ends it; an answer the turn went on from is a step of it. What needs the user (an error, a warning, a permission
 * request, a question and its answer, a tool call or job that failed or was canceled) is `attention`: it is never dimmed and has no setting.
 */
export const timelineEntryTypes = Object.freeze(["prompt", "agentMessage", "answer", "step", "reasoning", "tool", "file", "plan", "systemPrompt", "status", "plugin"] as const);
export type TimelineEntryType = typeof timelineEntryTypes[number] | "attention";

/** The name of each type in Settings, an English source key of the localization. */
export const timelineEntryTypeLabel = Object.freeze({
  prompt: "Your prompts", agentMessage: "Agent messages and background jobs", answer: "Final answers", step: "Intermediate answers",
  reasoning: "Reasoning", tool: "Tool calls", file: "File changes", plan: "Plans", systemPrompt: "System prompt", status: "Model and status",
  plugin: "Plugin cards",
} as const satisfies Readonly<Record<typeof timelineEntryTypes[number], string>>);

const needsAttention = (phase: string | null | undefined) => phase === "failed" || phase === "canceled";
const toolGroupBackground = "color-mix(in srgb, var(--panel-2) 60%, transparent)";

function backgroundNeedsAttention(phase: string | null | undefined, activityId: string | undefined, calls?: ReadonlyMap<string, BackgroundCallState>): boolean {
  if (!activityId || phase !== "completed" && phase !== "deselected") return false;
  const state = calls?.get(activityId);
  return state === "failed" || state === "stopped";
}

/** What a row is, before the place of an answer in its turn is known: every answer is `answer` here. */
export function rowEntryType(row: ReconciledRow, backgroundCalls?: ReadonlyMap<string, BackgroundCallState>): TimelineEntryType {
  if (row.source === "liveTool") return needsAttention(row.row.phase.toLowerCase())
    || backgroundNeedsAttention(row.row.phase.toLowerCase(), row.row.activityId, backgroundCalls) ? "attention" : "tool";
  if (row.source === "liveText") {
    const kind = row.row.kind.toLowerCase();
    // A prompt that arrives while it is written can be the message of another agent: its envelope says so, as in history.
    if (kind === "user") {
      const item = liveTextItem(row.row);
      return needsAttention(item.jobResult?.phase) ? "attention" : item.delegated || item.jobResult ? "agentMessage" : "prompt";
    }
    return kind === "assistant" ? "answer" : kind.startsWith("reasoning") ? "reasoning" : kind === "plan" ? "plan" : "status";
  }
  const item = row.item;
  if (item.category === "error" || item.eventType.startsWith("permission") || item.eventType === "userInputRequest"
    || item.eventType === "interaction" || needsAttention(item.toolPhase) || needsAttention(item.jobResult?.phase)
    || item.prompt && promptProblem(item.prompt)
    || item.category === "tool" && backgroundNeedsAttention(item.toolPhase, item.toolCall?.activityId, backgroundCalls)) return "attention";
  switch (item.category) {
    case "user": return item.delegated || item.jobResult ? "agentMessage" : "prompt";
    case "assistant": return "answer";
    case "reasoning": return "reasoning";
    case "tool": case "image": return "tool";
    case "file": return "file";
    case "plan": return "plan";
    case "prompt": return "systemPrompt";
    case "plugin": return "plugin";
    default: return "status";
  }
}

/**
 * The type of each group, by its key. A prompt or an agent message starts a turn; an answer is `step` when a later
 * one of the same turn follows it, so the newest answer of a turn that still runs stays an answer until another comes.
 */
export function groupEntryTypes(groups: readonly TimelineGroup[], backgroundCalls?: ReadonlyMap<string, BackgroundCallState>): ReadonlyMap<string, TimelineEntryType> {
  const result = new Map<string, TimelineEntryType>();
  let answer: string | null = null;
  for (const group of groups) {
    // A run of tool calls with one that failed or was canceled stays prominent as a whole.
    const type = group.tools ? group.rows.some(row => rowEntryType(row, backgroundCalls) === "attention") ? "attention" : "tool" : groupEntryType(group.rows, backgroundCalls);
    // A failed or canceled job is attention, but its user-message envelope still starts a turn.
    const startsTurn = group.rows.some(row => row.source === "history" ? row.item.category === "user" : row.source === "liveText" && row.row.kind.toLowerCase() === "user");
    if (startsTurn) answer = null;
    else if (type === "answer") { if (answer !== null) result.set(answer, "step"); answer = group.key; }
    result.set(group.key, type);
  }
  return result;
}

// A group that is not a run of tool calls holds one row; should it hold more, the most prominent one counts.
function groupEntryType(rows: readonly ReconciledRow[], backgroundCalls?: ReadonlyMap<string, BackgroundCallState>): TimelineEntryType {
  const types = rows.map(row => rowEntryType(row, backgroundCalls));
  return (["attention", "prompt", "agentMessage", "answer"] as const).find(type => types.includes(type)) ?? types[0] ?? "status";
}

/** The paint of a card in one state; its existing semantic color is never replaced. */
export type EntryAppearanceState = Readonly<{ backgroundTint: number; outerBorder: boolean; elevation: "none" | "subtle" }>;
/** Card paint in both states and whether its always-present left accent follows content opacity. */
export type EntryAppearance = Readonly<{ normal: EntryAppearanceState; hover: EntryAppearanceState; dimAccent: boolean }>;
/** One type's content opacity and optional appearance extension. Older saved dim preferences remain valid. */
export type EntryEmphasis = Readonly<{ dim: boolean; opacity: number; hoverOpacity: number; appearance?: EntryAppearance }>;
/**
 * The emphasis of the timeline: a switch for all of it, and the setting of each type. `focused` folds the work between
 * two messages into one line that opens on its rows; otherwise those lines are open. `foldedImages` shows the images
 * the model read during that work under its folded line, in a strip.
 */
export type TimelineEmphasis = Readonly<{ enabled: boolean; focused: boolean; foldedImages: boolean;
  types: Readonly<Record<typeof timelineEntryTypes[number], EntryEmphasis>> }>;

export const minimumOpacity = 10, opacityStep = 5;
/** The opacities Settings offers, in percent. */
export const opacityChoices: readonly number[] = Object.freeze(Array.from({ length: (100 - minimumOpacity) / opacityStep + 1 }, (_, index) => minimumOpacity + index * opacityStep));
/** Background-color percentages, independent of content opacity. */
export const backgroundTintChoices: readonly number[] = Object.freeze(Array.from({ length: 16 }, (_, index) => index * 2));
const baselineState: EntryAppearanceState = Object.freeze({ backgroundTint: 8, outerBorder: true, elevation: "none" });
const baselineAppearance: EntryAppearance = Object.freeze({ normal: baselineState, hover: baselineState, dimAccent: true });
/** Resolves optional paint settings without mutating or migrating a legacy preference. */
export function entryAppearance(value: EntryEmphasis): EntryAppearance { return value.appearance ?? baselineAppearance; }
const borderlessState: EntryAppearanceState = Object.freeze({ ...baselineState, outerBorder: false });
const progressAppearance: EntryAppearance = Object.freeze({ normal: borderlessState, hover: baselineState, dimAccent: true });
const quietAppearance: EntryAppearance = Object.freeze({ normal: borderlessState, hover: borderlessState, dimAccent: true });
const shown: EntryEmphasis = Object.freeze({ dim: false, opacity: 100, hoverOpacity: 100, appearance: baselineAppearance });
const progress: EntryEmphasis = Object.freeze({ dim: true, opacity: 70, hoverOpacity: 100, appearance: progressAppearance });
const quiet: EntryEmphasis = Object.freeze({ dim: true, opacity: 50, hoverOpacity: 100, appearance: quietAppearance });
export const defaultTimelineEmphasis: TimelineEmphasis = Object.freeze({ enabled: false, focused: true, foldedImages: true, types: Object.freeze({
  prompt: shown, agentMessage: shown, answer: shown, step: progress, reasoning: quiet, tool: progress, file: shown, plan: shown,
  systemPrompt: quiet, status: quiet, plugin: quiet }) });

/** Kept in the WebView's storage: one setting for the window. */
export const timelineEmphasisKey = "codealta.desktop.timeline.emphasis.v1";

const opacityOf = (value: unknown, fallback: number) =>
  typeof value === "number" && Number.isFinite(value) ? Math.min(100, Math.max(minimumOpacity, Math.round(value / opacityStep) * opacityStep)) : fallback;

function restoreAppearance(value: unknown): EntryAppearance | undefined {
  if (!value || typeof value !== "object") return undefined;
  const kept = value as Record<string, unknown>;
  const state = (value: unknown): EntryAppearanceState => {
    const kept = value && typeof value === "object" ? value as Record<string, unknown> : {};
    return { backgroundTint: typeof kept.backgroundTint === "number" && Number.isFinite(kept.backgroundTint)
      ? Math.min(30, Math.max(0, Math.round(kept.backgroundTint / 2) * 2)) : baselineState.backgroundTint,
    outerBorder: typeof kept.outerBorder === "boolean" ? kept.outerBorder : baselineState.outerBorder,
    elevation: kept.elevation === "subtle" ? "subtle" : "none" };
  };
  return { normal: state(kept.normal), hover: state(kept.hover), dimAccent: typeof kept.dimAccent === "boolean" ? kept.dimAccent : baselineAppearance.dimAccent };
}

/** Reads the kept setting; what is missing or invalid takes its default. */
export function restoreTimelineEmphasis(read: () => string | null): TimelineEmphasis {
  let stored: unknown;
  try { stored = JSON.parse(read() ?? "null"); } catch { return defaultTimelineEmphasis; }
  if (!stored || typeof stored !== "object") return defaultTimelineEmphasis;
  const value = stored as { enabled?: unknown; focused?: unknown; foldedImages?: unknown; types?: Record<string, unknown> };
  const types = Object.fromEntries(timelineEntryTypes.map(type => {
    const fallback = defaultTimelineEmphasis.types[type];
    const kept = value.types && typeof value.types[type] === "object" && value.types[type] !== null ? value.types[type] as Record<string, unknown> : {};
    if (!["dim", "opacity", "hoverOpacity", "appearance"].some(key => key in kept)) return [type, fallback];
    const appearance = restoreAppearance(kept.appearance);
    return [type, { dim: typeof kept.dim === "boolean" ? kept.dim : fallback.dim, opacity: opacityOf(kept.opacity, fallback.opacity),
      hoverOpacity: opacityOf(kept.hoverOpacity, fallback.hoverOpacity), ...(appearance ? { appearance } : {}) }];
  })) as TimelineEmphasis["types"];
  return { enabled: typeof value.enabled === "boolean" ? value.enabled : defaultTimelineEmphasis.enabled,
    focused: typeof value.focused === "boolean" ? value.focused : defaultTimelineEmphasis.focused,
    foldedImages: typeof value.foldedImages === "boolean" ? value.foldedImages : defaultTimelineEmphasis.foldedImages, types };
}

/** Keeps the setting; returns whether it could be kept. */
export function persistTimelineEmphasis(write: (value: string) => void, value: TimelineEmphasis): boolean {
  try { write(JSON.stringify(value)); return true; } catch { return false; }
}

/**
 * Window-wide custom properties for content opacity and independent card paint. Hover/focus never lowers opacity.
 * Colors remain scheme-owned. The stored hover choice and legacy dim fields are preserved.
 */
export function emphasisProperties(value: TimelineEmphasis): Record<string, string> {
  const properties: Record<string, string> = {};
  if (!value.enabled) return properties;
  for (const type of timelineEntryTypes) {
    const emphasis = value.types[type];
    const opacity = emphasis.dim ? emphasis.opacity / 100 : 1;
    const hoverOpacity = emphasis.dim ? Math.max(emphasis.opacity, emphasis.hoverOpacity) / 100 : 1;
    if (emphasis.dim) {
      properties[`--timeline-opacity-${type}`] = String(opacity);
      properties[`--timeline-hover-opacity-${type}`] = String(hoverOpacity);
    }
    const appearance = entryAppearance(emphasis);
    for (const [prefix, state] of [["", appearance.normal], ["hover-", appearance.hover]] as const) {
      properties[`--timeline-${prefix}tint-${type}`] = `${state.backgroundTint}%`;
      properties[`--timeline-${prefix}border-${type}`] = state.outerBorder ? "var(--line)" : "transparent";
      // Blueprint's surface shadow includes an inset contour; elevation must not add another border.
      properties[`--timeline-${prefix}elevation-${type}`] = state.elevation === "subtle" ? "var(--timeline-subtle-shadow)" : "none";
      properties[`--timeline-${prefix}layer-${type}`] = state.elevation === "subtle" ? prefix ? "2" : "1" : "auto";
      properties[`--timeline-${prefix}accent-opacity-${type}`] = String(appearance.dimAccent ? prefix ? hoverOpacity : opacity : 1);
      if (type === "tool") properties[`--timeline-${prefix}group-background-${type}`] = state.backgroundTint === baselineState.backgroundTint
        ? toolGroupBackground : `color-mix(in srgb, var(--muted) ${state.backgroundTint}%, var(--panel))`;
    }
  }
  return properties;
}

/** Binds one classified group to window-wide appearance variables; attention has no configurable values. */
export function entryEmphasisProperties(type: TimelineEntryType): Record<string, string> {
  const properties: Record<string, string> = {};
  for (const name of ["opacity", "tint", "border", "elevation", "layer", "accent-opacity", "group-background"] as const) {
    const fallback = name === "opacity" || name === "accent-opacity" ? "1" : name === "tint" ? "8%" : name === "border" ? "var(--line)" : name === "group-background" ? toolGroupBackground : name === "layer" ? "auto" : "none";
    properties[`--entry-${name}`] = `var(--timeline-${name}-${type}, ${fallback})`;
    properties[`--entry-hover-${name}`] = `var(--timeline-hover-${name}-${type}, ${fallback})`;
  }
  return properties;
}
