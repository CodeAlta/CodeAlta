import type { HistoryResponse } from "#neoastra";
import { parseDelegatedMessage } from "./delegatedMessage";
import type { IconName } from "./AppIcon";
import { compactionDetailsMarkdown, splitCheckpointSummary } from "./compactionDetails";
import { projectFileChanges, type FileChanges } from "./fileChanges";
import { projectTimelineImages, type TimelineImage } from "./timelineImages";

export type HistoryEntry = HistoryResponse["entries"][number];

export type TimelineItem = Readonly<{
  key: string;
  eventType: string;
  category: "user" | "assistant" | "reasoning" | "tool" | "file" | "image" | "status" | "prompt" | "plan" | "notes" | "error" | "plugin";
  icon: IconName;
  title: string;
  subtitle: string | null;
  timestamp: string;
  markdown: string | null;
  summary: string | null;
  summaryIsCode: boolean;
  detailMarkdown: string | null;
  details: string | null;
  detailsLabel: string;
  metadata: ReadonlyArray<string>;
  truncated: boolean;
  bodyOmitted: boolean;
  copyMarkdown: string | null;
  /** An HTML fragment a plugin gave for its card: shown instead of the summary, after sanitizing. */
  html?: string | null;
  /** The detail sections of a card that has an HTML section, in order: each is an HTML fragment or Markdown. */
  detailSections?: ReadonlyArray<Readonly<{ header: string; html: string | null; markdown: string | null }>>;
  /** The plugin a card comes from, for the commands its fragments name. */
  pluginKey?: string;
  fileChanges?: FileChanges;
  /** The images attached to a user message, or the ones a tool gave the model; their content is read by index. */
  images?: ReadonlyArray<TimelineImage>;
  /** What names a tool call: its row, its details and its live output are found by it. */
  toolCall?: ToolCallIdentity;
  toolPhase?: string;
  toolOutput?: string | null;
  toolOutputLines?: number;
  toolOutputBytes?: number | null;
  toolFields?: NonNullable<HistoryEntry["tool"]>["fields"];
  /** The lines an edit added to and removed from files, when the record of the call has its diff. */
  toolChanges?: Readonly<{ added: number; removed: number }>;
  /** The exit code of a command, when its record has one. */
  toolExitCode?: number | null;
  /** True for a prompt another agent session delivered, shown without its routing envelope. */
  delegated?: boolean;
  /** The session a delegated prompt comes from, when it is known: the row names it by its title. */
  sourceSessionId?: string;
}>;

/**
 * The identity of a tool call and where its records are. `offset` is the journal offset of its newest
 * activity record and `outputOffset` the one of its output record; both are null for a call the journal has
 * not shown yet. `startedAt` and `endedAt` are the times of its first record and of the record that ended it,
 * when the loaded window has them.
 */
export type ToolCallIdentity = Readonly<{ providerId: string; runId: string | null; activityId: string; kind: string; name: string | null;
  offset: string | null; outputOffset: string | null; startedAt: string | null; endedAt: string | null;
  /** The images of the output record: they are read at `outputOffset`. */
  images?: ReadonlyArray<TimelineImage> }>;

type JsonObject = Record<string, unknown>;

// A journal record always presents the same way, and a window keeps its record objects when it grows or is
// refreshed. Remembering the item of a record keeps a long timeline cheap to rebuild and lets its rows
// see that nothing changed.
const presented = new WeakMap<HistoryEntry, TimelineItem>();
const folded = new WeakMap<HistoryEntry, { first: HistoryEntry; output: HistoryEntry | undefined; item: TimelineItem }>();
function presentedItem(entry: HistoryEntry): TimelineItem {
  let item = presented.get(entry);
  if (!item) presented.set(entry, item = toTimelineItem(entry, false));
  return item;
}

export function buildTimelineItems(entries: HistoryResponse["entries"]): TimelineItem[] {
  const completed = new Set(entries
    .filter(entry => entry.eventType === "contentCompleted" && entry.contentId)
    .map(entry => contentKey(entry)));
  // The records of one call (requested, started, ended) make one row: its newest record, where its first one is.
  const calls = new Map<string, { first: HistoryEntry; last: HistoryEntry }>();
  for (const entry of entries) {
    if (entry.eventType !== "activity" || !entry.activityId) continue;
    const key = activityKey(entry), call = calls.get(key);
    if (!call) calls.set(key, { first: entry, last: entry });
    // A record that ended the call is not replaced by a late report of its start.
    else if (!isTerminalPhase(call.last.phase) || isTerminalPhase(entry.phase)) call.last = entry;
  }
  const deltas = new Map<string, TimelineItem>();
  const outputs = new Map<string, HistoryEntry>();
  for (const entry of entries) {
    if (entry.eventType === "contentCompleted" && isToolOutput(entry.kind) && entry.parentActivityId && entry.tool)
      outputs.set(parentActivityKey(entry), entry);
  }
  const result: TimelineItem[] = [];

  for (const entry of entries) {
    // Raw provider records are persistence/runtime plumbing duplicated by typed content and
    // activity events. The TUI intentionally keeps them out of its visual timeline too.
    if (entry.eventType === "raw" || entry.eventType === "notes") continue;
    // These update the TUI's status/usage surfaces, not its conversation timeline.
    if (entry.eventType === "sessionUpdate" && !["warning", "reconnecting", "modelchanged", "compactionstarted", "compactioncompleted", "diffupdated"].includes(entry.kind?.toLowerCase() ?? "")) continue;
    if ((entry.eventType === "contentCompleted" || entry.eventType === "contentDelta") &&
        isToolOutput(entry.kind) && entry.parentActivityId && calls.has(parentActivityKey(entry))) {
      // The text of an output is shown on the tile of its call; the images the model was given get a card.
      if (entry.eventType === "contentCompleted" && entry.images) { const card = presentedItem(entry); if (card.category === "image") result.push(card); }
      continue;
    }

    if (entry.eventType === "contentDelta" && entry.contentId) {
      const key = contentKey(entry);
      if (completed.has(key)) continue;
      const existing = deltas.get(key);
      if (existing) {
        const markdown = `${existing.markdown ?? ""}${entry.text ?? ""}`;
        const updated = { ...existing, markdown, copyMarkdown: markdown, timestamp: entry.timestamp,
          truncated: existing.truncated || entry.textTruncated || entry.detailsTruncated };
        deltas.set(key, updated);
        const index = result.indexOf(existing);
        if (index >= 0) result[index] = updated;
      } else {
        const item = toTimelineItem(entry, true);
        deltas.set(key, item);
        result.push(item);
      }
      continue;
    }
    const call = entry.eventType === "activity" && entry.activityId ? calls.get(activityKey(entry)) : undefined;
    if (!call) { result.push(presentedItem(entry)); continue; }
    if (entry === call.first) result.push(callItem(call.first, call.last, outputs.get(activityKey(entry))));
  }
  return result.filter(item => item.category !== "reasoning" || !!item.markdown?.trim());
}

// The row of a call: what its newest record says, at the time of its first one, with the output record of the
// call when there is one. A typed completed output belongs to this exact session/provider/run/activity; a
// bounded preview or retained streaming deltas are never counted as a total.
function callItem(first: HistoryEntry, last: HistoryEntry, output: HistoryEntry | undefined): TimelineItem {
  const known = folded.get(last);
  if (known && known.first === first && known.output === output) return known.item;
  const base = presentedItem(last);
  const attached = output?.tool ? output : undefined;
  const item: TimelineItem = { ...base, timestamp: first.timestamp,
    toolCall: base.toolCall && { ...base.toolCall, outputOffset: attached?.offset ?? null, images: projectTimelineImages(attached?.images),
      startedAt: isTerminalPhase(first.phase) ? null : first.timestamp, endedAt: isTerminalPhase(last.phase) ? last.timestamp : null },
    ...attached ? { toolOutput: attached.tool!.output, toolOutputLines: attached.tool!.outputLines, toolOutputBytes: attached.tool!.outputBytes,
      toolExitCode: attached.tool!.exitCode ?? base.toolExitCode,
      toolFields: [...(base.toolFields ?? []), { path: "content", text: attached.text ?? "", truncated: attached.textTruncated || attached.bodyOmitted }] } : {} };
  folded.set(last, { first, output, item });
  return item;
}

function contentKey(entry: HistoryEntry): string {
  return JSON.stringify([entry.sessionId, entry.providerId, entry.runId, entry.kind, entry.contentId]);
}

function activityKey(entry: HistoryEntry): string {
  return JSON.stringify([entry.sessionId, entry.providerId, entry.runId, entry.activityId]);
}

function parentActivityKey(entry: HistoryEntry): string {
  return JSON.stringify([entry.sessionId, entry.providerId, entry.runId, entry.parentActivityId]);
}

function isTerminalPhase(phase: string | null): boolean {
  return ["completed", "failed", "canceled"].includes(phase?.toLowerCase() ?? "");
}

function isToolOutput(kind: string | null): boolean {
  return ["commandoutput", "filechangeoutput", "tooloutput"].includes(kind?.toLowerCase() ?? "");
}

function toTimelineItem(entry: HistoryEntry, streaming: boolean): TimelineItem {
  const kind = entry.kind ?? "";
  const normalizedKind = kind.toLowerCase();
  const parsedDetails = parseDetails(entry.details);
  let category: TimelineItem["category"] = "status";
  let icon: IconName = "info";
  let title = friendly(entry.eventType);
  let subtitle: string | null = entry.phase ? friendly(entry.phase) : null;
  let markdown = entry.text;
  let summary: string | null = null;
  let summaryIsCode = false;
  let detailMarkdown: string | null = null;
  let details = formatDetails(entry.details);
  let detailsLabel = "Details";
  // A prompt of another session is one that has the envelope of a message between agents, or one the host
  // recorded with the session that sent it: the prompt a parent gives its sub-agent is sent as it is written.
  const fromAgent = !streaming && normalizedKind === "user" && entry.eventType === "contentCompleted";
  const delegated = !fromAgent ? null : parseDelegatedMessage(entry.text)
    ?? (entry.sourceSessionId ? { sourceSessionId: entry.sourceSessionId, kind: "prompt", body: entry.text ?? "" } : null);
  const toolImages = !streaming && normalizedKind === "tooloutput" && entry.eventType === "contentCompleted" ? projectTimelineImages(entry.images) : undefined;

  if (entry.eventType === "contentCompleted" || entry.eventType === "contentDelta") {
    if (normalizedKind === "user") {
      category = "user"; icon = delegated ? "branch" : "user"; title = delegated ? "Agent message" : "You";
      if (delegated) markdown = delegated.body;
    }
    else if (normalizedKind === "assistant") { category = "assistant"; icon = "assistant"; title = "Assistant"; }
    else if (normalizedKind.startsWith("reasoning")) { category = "reasoning"; icon = "brain"; title = normalizedKind === "reasoningsummary" ? "Reasoning summary" : "Reasoning"; }
    else if (normalizedKind === "plan") { category = "plan"; icon = "plan"; title = "Plan"; }
    else if (normalizedKind === "filechangeoutput") { category = "file"; icon = "file"; title = "File changes"; }
    else if (toolImages) { category = "image"; icon = "fileImage"; title = "Image"; markdown = null; details = null; }
    else if (normalizedKind.endsWith("output")) { category = "tool"; icon = "tool"; title = friendly(kind); }
    else { title = friendly(kind || entry.eventType); }
    subtitle = streaming ? "Streaming" : delegated
      ? friendly(delegated.kind)
      : category === "image" ? toolImages!.map(image => image.title).join(", ") : null;
  } else if (entry.eventType === "activity") {
    category = normalizedKind === "filechange" ? "file" : "tool";
    icon = category === "file" ? "file" : "tool";
    const tool = toolPresentation(entry, parsedDetails);
    title = tool.name;
    subtitle = [friendly(entry.phase ?? ""), tool.kindLabel].filter(Boolean).join(" · ") || null;
    summary = entry.tool?.primary ?? tool.primary;
    summaryIsCode = entry.tool?.isCommand ?? tool.primaryIsCode;
    markdown = tool.status;
    // Retain the bounded supplied message without treating it as output or an outcome.
    if (normalizedKind === "toolcall" && !markdown && entry.text) detailMarkdown = entry.text;
    detailsLabel = normalizedKind === "filechange" ? "File change record details" : tool.detailsLabel;
  } else if (entry.eventType === "system_prompt") {
    category = "prompt"; icon = "prompt";
    title = promptTitle(entry.text);
    subtitle = promptSubtitle(entry.text, entry.name, kind);
    detailMarkdown = entry.text;
    markdown = null;
    summary = promptSummary(entry.text);
    detailsLabel = "Prompt details";
  } else if (entry.eventType === "planSnapshot") {
    category = "plan"; icon = "plan"; title = "Plan"; subtitle = friendly(kind);
  } else if (entry.eventType === "notes") {
    category = "notes"; icon = "notes"; title = "Notes"; subtitle = friendly(kind);
  } else if (entry.eventType === "error") {
    category = "error"; icon = "error"; title = "Error";
  } else if (entry.eventType.startsWith("permission")) {
    category = "status"; icon = "question"; title = entry.name || "Permission request"; subtitle = friendly(kind);
  } else if (entry.eventType === "userInputRequest") {
    category = "status"; icon = "question"; title = entry.name || "User input requested";
  } else if (entry.eventType === "sessionUpdate") {
    category = normalizedKind === "warning" ? "error" : "status";
    icon = normalizedKind === "usageupdated" ? "usage" : normalizedKind === "modelchanged" ? "model" : normalizedKind.includes("completed") ? "check" : "info";
    if (normalizedKind === "diffupdated") {
      category = "file"; icon = "file"; title = "File changes";
    } else if (normalizedKind === "usageupdated") {
      title = usageHeadline(entry.text);
      subtitle = "Usage";
      summary = usageSummary(entry.text);
      detailMarkdown = entry.text;
      markdown = null;
      detailsLabel = "Usage details";
    } else if (normalizedKind === "modelchanged") {
      const model = modelPresentation(entry.text, parsedDetails);
      title = model.title;
      subtitle = model.subtitle;
      summary = model.summary;
      detailMarkdown = model.keepMessage ? entry.text : null;
      markdown = null;
      detailsLabel = "Model details";
    } else {
      title = sessionUpdateTitle(kind);
      // A local compaction is detailed like in the terminal, in place of its raw record.
      const checkpoint = normalizedKind === "compactioncompleted" ? splitCheckpointSummary(entry.text) : null;
      const compaction = checkpoint ? compactionDetailsMarkdown(parsedDetails, checkpoint.summary) : null;
      if (compaction) { markdown = checkpoint!.message; detailMarkdown = compaction; details = null; detailsLabel = "Compaction details"; }
    }
  } else if (entry.eventType === "interaction") {
    category = "status"; icon = "check"; title = friendly(kind || "Interaction");
  }

  const metadata = [
    delegated?.sourceSessionId ? `From session: ${delegated.sourceSessionId}` : null,
    entry.providerId ? `Provider: ${entry.providerId}` : null,
    entry.runId ? `Run: ${entry.runId}` : null,
    entry.activityId ? `Activity: ${entry.activityId}` : null,
    entry.parentActivityId ? `Parent: ${entry.parentActivityId}` : null,
    entry.interactionId ? `Interaction: ${entry.interactionId}` : null,
    `Event: ${entry.eventType} · byte ${entry.offset}`,
  ].filter((value): value is string => value !== null);
  const copyMarkdown = [summaryIsCode && summary ? `\`\`\`\n${summary}\n\`\`\`` : summary, markdown, detailMarkdown, details]
    .filter((value): value is string => !!value).join("\n\n") || null;

  return {
    key: entry.offset,
    eventType: entry.eventType,
    category,
    icon,
    title,
    subtitle,
    timestamp: entry.timestamp,
    markdown,
    summary,
    summaryIsCode,
    detailMarkdown,
    details,
    detailsLabel,
    metadata,
    truncated: entry.textTruncated || entry.detailsTruncated,
    bodyOmitted: entry.bodyOmitted,
    copyMarkdown,
    fileChanges: projectFileChanges(entry),
    images: category === "user" ? projectTimelineImages(entry.images) : category === "image" ? toolImages : undefined,
    toolCall: entry.eventType === "activity" && entry.activityId && category === "tool" ? { providerId: entry.providerId, runId: entry.runId,
      activityId: entry.activityId, kind, name: entry.name, offset: /^\d+$/.test(entry.offset) ? entry.offset : null, outputOffset: null,
      startedAt: null, endedAt: null } : undefined,
    toolPhase: entry.eventType === "activity" ? entry.phase?.toLowerCase() : undefined,
    toolOutput: entry.tool?.output,
    toolOutputLines: entry.tool?.outputLines,
    toolOutputBytes: entry.tool?.outputBytes,
    toolFields: entry.tool?.fields,
    toolChanges: entry.tool?.added != null && entry.tool.removed != null ? { added: entry.tool.added, removed: entry.tool.removed } : undefined,
    toolExitCode: entry.tool?.exitCode,
    delegated: delegated ? true : undefined,
    sourceSessionId: delegated?.sourceSessionId ?? undefined,
  };
}

function toolPresentation(entry: HistoryEntry, details: JsonObject | null) {
  const detailsName = stringAt(details, "toolName") ?? stringAt(details, "mcpToolName") ?? stringAt(details, "tool") ?? stringAt(details, "name");
  const name = entry.name?.trim() || detailsName || friendly(entry.kind || "Tool");
  const command = stringAt(details, "command") ?? stringAt(details, "arguments", "command") ?? stringAt(details, "input", "command");
  const query = stringAt(details, "query") ?? stringAt(details, "arguments", "query") ?? stringAt(details, "input", "query");
  const path = stringAt(details, "path") ?? stringAt(details, "arguments", "path") ?? stringAt(details, "input", "path");
  const prompt = stringAt(details, "prompt") ?? stringAt(details, "arguments", "prompt") ?? stringAt(details, "input", "prompt");
  const primary = firstUsefulLine(command ?? query ?? path ?? prompt);
  const phase = entry.phase?.toLowerCase();
  const status = entry.text && (!primary || phase === "failed") ? entry.text : null;
  return {
    name,
    kindLabel: entry.name || detailsName ? friendly(entry.kind || "Tool call") : "",
    primary,
    primaryIsCode: !!(command || path),
    status,
    detailsLabel: command ? "Command and result" : "Tool details",
  };
}

function firstUsefulLine(value: string | null): string | null {
  return value?.replaceAll("\r\n", "\n").split("\n").map(line => line.trim()).find(Boolean) ?? null;
}

function promptTitle(text: string | null): string {
  const initial = /\*\*Change:\*\*\s*initial/i.test(text ?? "") || /\*\*Reason:\*\*\s*session[_ ]start/i.test(text ?? "");
  return initial ? "System prompt recorded" : "System prompt changed";
}

function promptSubtitle(text: string | null, name: string | null, kind: string): string | null {
  const total = capture(text, /\*\*Approximate tokens:\*\*\s*([\d,]+)\s+total/i);
  return [name, total ? `${total} tokens` : null, friendly(kind)].filter(Boolean).join(" · ") || null;
}

function promptSummary(text: string | null): string | null {
  const mapping = capture(text, /\*\*Provider mapping:\*\*\s*([^\n]+)/i);
  return mapping ? `Provider mapping: ${mapping}` : null;
}

function usageHeadline(text: string | null): string {
  const context = capture(text, /\*\*Context:\*\*\s*([^\n]+)/i);
  return context ? `Context ${context}` : "Usage updated";
}

function usageSummary(text: string | null): string | null {
  const model = capture(text, /\*\*Model:\*\*\s*([^\n]+)/i);
  const input = capture(text, /\*\*Input tokens:\*\*\s*([^\n]+)/i);
  const output = capture(text, /\*\*Output tokens:\*\*\s*([^\n]+)/i);
  const cached = capture(text, /\*\*Cached input tokens:\*\*\s*([^\n]+)/i);
  const cost = capture(text, /\*\*Cost:\*\*\s*([^\n]+)/i);
  const tokens = [input ? `${input} in${cached && cached !== "0" ? ` (${cached} cached)` : ""}` : null, output ? `${output} out` : null].filter(Boolean).join(" · ");
  // A cost that comes with its unit reads by itself: "0.0614 AI credits".
  return [model, tokens || null, cost ? /[a-z]/i.test(cost) ? cost : `${cost} cost` : null].filter(Boolean).join(" · ") || null;
}

function modelPresentation(text: string | null, details: JsonObject | null) {
  const provider = stringAt(details, "providerKey") ?? stringAt(details, "providerId");
  const model = stringAt(details, "modelId") ?? capture(text, /model\s+`([^`]+)`/i);
  const reasoning = stringAt(details, "reasoningEffort") ?? capture(text, /reasoning:\s*`([^`]+)`/i);
  return {
    title: model ? `Model · ${model}` : "Model changed",
    subtitle: provider ? `Provider ${provider}` : null,
    summary: reasoning ? `Reasoning: ${friendly(reasoning)}` : null,
    keepMessage: !provider && !model && !reasoning,
  };
}

function sessionUpdateTitle(kind: string): string {
  const normalized = kind.toLowerCase();
  if (normalized === "reconnecting") return "Reconnecting";
  if (normalized === "compactionstarted") return "Compaction started";
  if (normalized === "compactioncompleted") return "Compaction completed";
  if (normalized === "warning") return "Warning";
  return friendly(kind || "Session update");
}

function capture(value: string | null, pattern: RegExp): string | null {
  return value?.match(pattern)?.[1]?.trim() || null;
}

function parseDetails(details: string | null): JsonObject | null {
  if (!details) return null;
  try {
    const value: unknown = JSON.parse(details);
    return isObject(value) ? value : null;
  } catch { return null; }
}

function isObject(value: unknown): value is JsonObject {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function stringAt(root: JsonObject | null, ...path: string[]): string | null {
  let value: unknown = root;
  for (const segment of path) {
    if (!isObject(value)) return null;
    value = value[segment];
  }
  return typeof value === "string" && value.trim() ? value.trim() : null;
}

/** Text of the newest persisted usage record in the loaded window, or null. */
export function latestUsageText(entries: HistoryResponse["entries"]): string | null {
  for (let index = entries.length - 1; index >= 0; index--) {
    const entry = entries[index];
    if (entry.eventType === "sessionUpdate" && entry.text?.includes("**Context:**")) return entry.text;
  }
  return null;
}

export function latestNotes(entries: HistoryResponse["entries"]): string {
  let markdown = "";
  for (const entry of entries) {
    if (entry.eventType === "notes") markdown = entry.kind?.toLowerCase() === "cleared" ? "" : entry.text ?? "";
  }
  return markdown;
}

export function formatDetails(details: string | null): string | null {
  if (!details) return null;
  try { return JSON.stringify(JSON.parse(details), null, 2); }
  catch { return details; }
}

export function friendly(value: string): string {
  if (!value) return "";
  return value.replaceAll("_", " ").replace(/([a-z0-9])([A-Z])/g, "$1 $2").replace(/^./, character => character.toUpperCase());
}

export async function writeMarkdown(writeText: (text: string) => Promise<void>, markdown: string): Promise<"copied" | "failed"> {
  try { await writeText(markdown); return "copied"; }
  catch { return "failed"; }
}
