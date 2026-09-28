import type { HistoryResponse } from "#neoastra";
import type { IconName } from "./AppIcon";
import { projectFileChanges, type FileChanges } from "./fileChanges";
import { projectToolRecord, type ToolRecord } from "./toolRecords";

export type HistoryEntry = HistoryResponse["entries"][number];

export type TimelineItem = Readonly<{
  key: string;
  eventType: string;
  category: "user" | "assistant" | "reasoning" | "tool" | "file" | "status" | "prompt" | "plan" | "notes" | "error";
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
  fileChanges?: FileChanges;
  toolRecord?: ToolRecord;
  toolPhase?: string;
}>;

type JsonObject = Record<string, unknown>;

export function buildTimelineItems(entries: HistoryResponse["entries"]): TimelineItem[] {
  const completed = new Set(entries
    .filter(entry => entry.eventType === "contentCompleted" && entry.contentId)
    .map(entry => contentKey(entry)));
  const terminalActivities = new Set(entries
    .filter(entry => entry.eventType === "activity" && entry.activityId && isTerminalPhase(entry.phase))
    .map(entry => activityKey(entry)));
  const representedActivities = new Set(entries
    .filter(entry => entry.eventType === "activity" && entry.activityId)
    .map(entry => activityKey(entry)));
  const deltas = new Map<string, TimelineItem>();
  const result: TimelineItem[] = [];

  for (const entry of entries) {
    // Raw provider records are persistence/runtime plumbing duplicated by typed content and
    // activity events. The TUI intentionally keeps them out of its visual timeline too.
    if (entry.eventType === "raw" || entry.eventType === "notes") continue;
    // These update the TUI's status/usage surfaces, not its conversation timeline.
    if (entry.eventType === "sessionUpdate" && !["warning", "reconnecting", "modelchanged", "compactionstarted", "compactioncompleted", "diffupdated"].includes(entry.kind?.toLowerCase() ?? "")) continue;
    if (entry.eventType === "activity" && entry.activityId && !isTerminalPhase(entry.phase) && terminalActivities.has(activityKey(entry))) continue;
    if ((entry.eventType === "contentCompleted" || entry.eventType === "contentDelta") &&
        isToolOutput(entry.kind) && entry.parentActivityId && representedActivities.has(parentActivityKey(entry))) continue;

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
    result.push(toTimelineItem(entry, false));
  }
  return result;
}

function contentKey(entry: HistoryEntry): string {
  return JSON.stringify([entry.providerId, entry.runId, entry.kind, entry.contentId]);
}

function activityKey(entry: HistoryEntry): string {
  return JSON.stringify([entry.providerId, entry.runId, entry.activityId]);
}

function parentActivityKey(entry: HistoryEntry): string {
  return JSON.stringify([entry.providerId, entry.runId, entry.parentActivityId]);
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

  if (entry.eventType === "contentCompleted" || entry.eventType === "contentDelta") {
    if (normalizedKind === "user") { category = "user"; icon = "user"; title = "You"; }
    else if (normalizedKind === "assistant") { category = "assistant"; icon = "assistant"; title = "CodeAlta"; }
    else if (normalizedKind.startsWith("reasoning")) { category = "reasoning"; icon = "brain"; title = normalizedKind === "reasoningsummary" ? "Reasoning summary" : "Reasoning"; }
    else if (normalizedKind === "plan") { category = "plan"; icon = "plan"; title = "Plan"; }
    else if (normalizedKind === "filechangeoutput") { category = "file"; icon = "file"; title = "File changes"; }
    else if (normalizedKind.endsWith("output")) { category = "tool"; icon = "tool"; title = friendly(kind); }
    else { title = friendly(kind || entry.eventType); }
    subtitle = streaming ? "Streaming" : null;
  } else if (entry.eventType === "activity") {
    category = normalizedKind === "filechange" ? "file" : "tool";
    icon = category === "file" ? "file" : "tool";
    const tool = toolPresentation(entry, parsedDetails);
    title = tool.name;
    subtitle = [friendly(entry.phase ?? ""), tool.kindLabel].filter(Boolean).join(" · ") || null;
    summary = tool.primary;
    summaryIsCode = tool.primaryIsCode;
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
    category = "notes"; icon = "notes"; title = "Alta notes"; subtitle = friendly(kind);
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
    }
  } else if (entry.eventType === "interaction") {
    category = "status"; icon = "check"; title = friendly(kind || "Interaction");
  }

  const metadata = [
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
    toolRecord: projectToolRecord(entry),
    toolPhase: entry.eventType === "activity" ? entry.phase?.toLowerCase() : undefined,
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
  const cost = capture(text, /\*\*Cost:\*\*\s*([^\n]+)/i);
  const tokens = [input ? `${input} in` : null, output ? `${output} out` : null].filter(Boolean).join(" · ");
  return [model, tokens || null, cost ? `${cost} cost` : null].filter(Boolean).join(" · ") || null;
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
