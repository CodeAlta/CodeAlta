import { liveTextItem, liveToolItem } from "../liveTimeline";
import type { ReconciledRow } from "../reconcileTimeline";
import type { ScriptRun, TimelineItem } from "../timeline";
import { hasWorkflowBlock } from "../workflows/runFlow";
import type { TimelineEntryType } from "../timelineEmphasis/timelineEmphasis";
import type { TimelineGroup } from "../toolGroups";
import type { BackgroundCallState } from "../backgroundTasks";
import { thoughtHeadline } from "./activityText";

export { activityDuration, thoughtHeadline } from "./activityText";

/**
 * What the timeline shows, in order: a message on its own line, or the activity between two messages folded into
 * one line that opens on the rows it holds. The prompts, the agent messages and the answers stay on their own line,
 * with what needs the user (an error, a warning, a request); the work in between (reasoning, tool calls, background
 * jobs, status rows, plugin cards) is the activity.
 */
export type TimelineBlock =
  | Readonly<{ kind: "row"; key: string; group: TimelineGroup }>
  | Readonly<{ kind: "activity"; key: string; groups: readonly TimelineGroup[] }>;

/** Whether a group is a message the timeline keeps on its own line. */
export function standsAlone(group: TimelineGroup, type: TimelineEntryType): boolean {
  if (group.tools) return false;
  // A background job ends with a prompt of the host: it is part of the work, whatever its outcome.
  if (group.rows.some(row => itemOf(row).jobResult)) return false;
  return type === "prompt" || type === "agentMessage" || type === "answer" || type === "step" || type === "attention";
}

// A row that is one line by itself (a model change, a recorded system prompt, the statistics of a turn) is not
// folded when it is the only work between two messages: its fold would be a line in place of a line.
function oneLine(group: TimelineGroup): boolean {
  if (group.tools || group.rows.length !== 1) return false;
  const row = group.rows[0];
  return row.source === "history" && !row.item.jobResult && ["status", "prompt", "plugin"].includes(row.item.category);
}

/**
 * Folds the groups of the timeline into blocks: each run of groups between two messages is one activity block,
 * keyed by its first group, so that it keeps its key while it grows.
 */
export function foldTimeline(groups: readonly TimelineGroup[], types: ReadonlyMap<string, TimelineEntryType>): TimelineBlock[] {
  const blocks: TimelineBlock[] = [];
  let run: TimelineGroup[] = [];
  const close = () => {
    if (run.length === 1 && oneLine(run[0])) blocks.push({ kind: "row", key: run[0].key, group: run[0] });
    else if (run.length) blocks.push({ kind: "activity", key: `activity:${run[0].key}`, groups: run });
    run = [];
  };
  for (const group of groups) {
    if (standsAlone(group, types.get(group.key) ?? "status")) { close(); blocks.push({ kind: "row", key: group.key, group }); }
    else run.push(group);
  }
  close();
  return blocks;
}

/** The state of one step of an activity, as its line shows it. */
export type ActivityStepState = "running" | "completed" | "failed" | "canceled" | "pending" | null;

/** One step of an activity, as its progress line says it: a tool call, a background job, a status. */
export type ActivityStep = Readonly<{
  /** The key of its row: its window opens by it. */
  rowKey: string;
  item: TimelineItem;
  title: string;
  /** What it works on: a command, a path, a query. */
  detail: string | null;
  detailIsCode: boolean;
  state: ActivityStepState;
}>;

/** What the line of an activity says of the rows it holds. */
export type ActivitySummary = Readonly<{
  /** The first line of its newest reasoning, null when the model said nothing of what it thinks. */
  headline: string | null;
  /** Its tool calls and background jobs, then how many of them ended well, failed or still run. */
  tools: number;
  succeeded: number;
  failed: number;
  running: number;
  thoughts: number;
  jobs: number;
  /** The lines its calls added to and removed from files. */
  added: number;
  removed: number;
  /** The time of its first and of its last record, in milliseconds, NaN when unknown. */
  startedAt: number;
  endedAt: number;
  /** Its newest steps, oldest first: the progress lines of an activity that goes on. */
  latest: readonly ActivityStep[];
  /** The short names of the first different steps it took, such as "write_file" or "Bash wc". */
  names: readonly string[];
}>;

const latestSteps = 3, latestNames = 3;

function itemOf(row: ReconciledRow): TimelineItem {
  return row.source === "history" ? row.item : row.source === "liveText" ? liveTextItem(row.row) : liveToolItem(row.row);
}

function stepState(item: TimelineItem, background?: BackgroundCallState): ActivityStepState {
  if (item.jobResult) return item.jobResult.phase ?? (item.jobResult.succeeded ? "completed" : "failed");
  if (item.category !== "tool" || !item.toolPhase) return null;
  if (background === "running") return "running";
  if (background === "failed") return "failed";
  if (background === "stopped") return "canceled";
  switch (item.toolPhase) {
    case "completed": case "deselected": return "completed";
    case "failed": return "failed";
    case "canceled": return "canceled";
    case "requested": return "pending";
    default: return "running";
  }
}

/** Sums up the rows of an activity block for its line. */
export function activitySummary(groups: readonly TimelineGroup[], backgroundCalls?: ReadonlyMap<string, BackgroundCallState>): ActivitySummary {
  let headline: string | null = null;
  let tools = 0, succeeded = 0, failed = 0, running = 0, thoughts = 0, jobs = 0, added = 0, removed = 0;
  let startedAt = NaN, endedAt = NaN;
  const steps: ActivityStep[] = [];
  for (const group of groups) for (const row of group.rows) {
    const item = itemOf(row);
    const time = Date.parse(item.timestamp);
    if (Number.isFinite(time)) {
      if (!(time >= startedAt)) startedAt = time;
      if (!(time <= endedAt)) endedAt = time;
    }
    const ended = Date.parse(item.toolCall?.endedAt ?? "");
    if (Number.isFinite(ended) && !(ended <= endedAt)) endedAt = ended;
    if (item.category === "reasoning") {
      thoughts++;
      const line = thoughtHeadline(item.markdown);
      if (line) headline = line;
      continue;
    }
    const state = stepState(item, item.toolCall ? backgroundCalls?.get(item.toolCall.activityId) : undefined);
    if (item.jobResult) jobs++;
    else if (item.category === "tool") tools++;
    if (state === "completed") succeeded++;
    if (state === "failed") failed++;
    if (state === "running" || state === "pending") running++;
    if (item.toolChanges) { added += item.toolChanges.added; removed += item.toolChanges.removed; }
    // A status row, a plan or a plugin card is part of the work, not a step of it.
    if (!item.jobResult && item.category !== "tool") continue;
    const title = item.jobResult ? item.jobResult.title ?? item.title : item.title;
    const detail = item.jobResult ? item.jobResult.command ?? item.jobResult.result : item.summary;
    steps.push({ rowKey: row.key, item, title, detail: detail ?? null, detailIsCode: item.jobResult ? !!item.jobResult.command : item.summaryIsCode, state });
  }
  const names: string[] = [];
  for (const step of steps) {
    if (names.length === latestNames) break;
    const name = stepName(step);
    if (!names.includes(name)) names.push(name);
  }
  return { headline, tools, succeeded, failed, running, thoughts, jobs, added, removed, startedAt, endedAt, latest: steps.slice(-latestSteps), names };
}

/**
 * The short name of a step: the name of its tool, with the program a command runs ("Bash wc") or the file it
 * works on ("Read main.tsx"). A tool whose name says it all stays alone ("write_file"), and a command that starts
 * with the name of its tool is named by what follows it ("alta skill").
 */
export function stepName(step: Pick<ActivityStep, "title" | "detail" | "detailIsCode">): string {
  const detail = step.detail?.trim();
  if (!detail || !step.detailIsCode) return step.title;
  // A program whose path has spaces is quoted: the quotes hold its name.
  const quoted = /^(["'])(.+?)\1/u.exec(detail);
  const words = detail.split(/\s+/u);
  const repeats = !quoted && words[0].toLowerCase() === step.title.toLowerCase();
  if (repeats && words.length === 1) return step.title;
  // A command that starts with a group or a call operator ("(Get-Content …).Count", "& git") is named by its program.
  let at = repeats ? 1 : 0;
  if (!quoted && /^[(&]+$/u.test(words[at]) && at + 1 < words.length) at++;
  // A script that starts with a variable ("$f = …") names no program: the tool says it.
  if (!quoted && /^[(&]*[$@]/u.test(words[at])) return step.title;
  const first = (quoted ? quoted[2] : words[at]).replace(/^[(&]+/u, "") || words[at];
  const word = /[\\/]/u.test(first) ? first.split(/[\\/]/u).filter(Boolean).at(-1) ?? first : first;
  return word.length > 32 ? step.title : `${step.title} ${word}`;
}

/** The view of an MCP tool call of an activity (an MCP App), as its block shows it under its line. */
export type ActivityView = Readonly<{
  /** The key of the row of its call: the window of the call opens by it. */
  rowKey: string;
  /** The journal offset of the record of its call, which the view is opened from. */
  offset: string;
  server: string;
  /** When its call ended, else when it started. */
  timestamp: string;
}>;

/**
 * The views of the calls of an activity that ended, in order: shown under the block, folded or not, since a view is
 * often the result of its call.
 */
export function activityViews(groups: readonly TimelineGroup[]): ActivityView[] {
  const views: ActivityView[] = [];
  for (const group of groups) for (const row of group.rows) {
    if (row.source !== "history") continue;
    const { item } = row;
    if (!item.toolApp || !item.toolCall?.offset || (item.toolPhase !== "completed" && item.toolPhase !== "failed")) continue;
    views.push({ rowKey: row.key, offset: item.toolCall.offset, server: item.toolApp.server, timestamp: item.toolCall.endedAt ?? item.timestamp });
  }
  return views;
}

/** The tool calls of an activity, by the identity a background task names its call with, separated by spaces. */
export function activityCalls(groups: readonly TimelineGroup[]): string {
  const calls: string[] = [];
  for (const group of groups) for (const row of group.rows) {
    const id = row.source === "history" ? row.item.toolCall?.activityId : undefined;
    if (id && !/\s/u.test(id)) calls.push(id);
  }
  return calls.join(" ");
}

/** A run of a script of an activity that has its workflow block: the call that runs it, and the run. */
export type ActivityWorkflow = Readonly<{ rowKey: string; run: ScriptRun; title: string | null }>;

/**
 * The runs of scripts of an activity that ran steps or started sessions, in order: each is a block of its own under the
 * line, folded or not, as the views are.
 */
export function activityWorkflows(groups: readonly TimelineGroup[]): ActivityWorkflow[] {
  const runs: ActivityWorkflow[] = [];
  for (const group of groups) for (const row of group.rows) {
    if (row.source !== "history" || !row.item.scriptRun || !hasWorkflowBlock(row.item.scriptRun)) continue;
    runs.push({ rowKey: row.key, run: row.item.scriptRun, title: row.item.scriptTitle ?? null });
  }
  return runs;
}

/**
 * Whether a block is open: the reader's own choice when they made one for one of its rows, open in a timeline
 * that is not focused, and open when it holds a row the timeline is asked to show.
 */
export function activityExpanded(groups: readonly TimelineGroup[], choices: ReadonlyMap<string, boolean>, focused: boolean, shown?: string | null): boolean {
  for (const group of groups) for (const row of group.rows) {
    const choice = choices.get(row.key);
    if (choice !== undefined) return choice;
  }
  if (shown && groups.some(group => group.rows.some(row => row.source === "history" && row.item.key === shown))) return true;
  return !focused;
}
