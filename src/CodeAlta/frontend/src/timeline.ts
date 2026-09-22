import type { HistoryResponse } from "#neoastra";

export type HistoryEntry = HistoryResponse["entries"][number];

export type TimelineItem = Readonly<{
  key: string;
  eventType: string;
  category: "user" | "assistant" | "reasoning" | "tool" | "file" | "status" | "prompt" | "plan" | "notes" | "error";
  icon: string;
  title: string;
  subtitle: string | null;
  timestamp: string;
  markdown: string | null;
  details: string | null;
  metadata: ReadonlyArray<string>;
  truncated: boolean;
  bodyOmitted: boolean;
}>;

export function buildTimelineItems(entries: HistoryResponse["entries"]): TimelineItem[] {
  const completed = new Set(entries
    .filter(entry => entry.eventType === "contentCompleted" && entry.contentId)
    .map(entry => contentKey(entry)));
  const deltas = new Map<string, TimelineItem>();
  const result: TimelineItem[] = [];

  for (const entry of entries) {
    if (entry.eventType === "contentDelta" && entry.contentId) {
      const key = contentKey(entry);
      if (completed.has(key)) continue;
      const existing = deltas.get(key);
      if (existing) {
        const updated = { ...existing, markdown: `${existing.markdown ?? ""}${entry.text ?? ""}`, timestamp: entry.timestamp,
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
  return `${entry.providerId}\u0000${entry.runId ?? ""}\u0000${entry.kind ?? ""}\u0000${entry.contentId ?? ""}`;
}

function toTimelineItem(entry: HistoryEntry, streaming: boolean): TimelineItem {
  const kind = entry.kind ?? "";
  const normalizedKind = kind.toLowerCase();
  let category: TimelineItem["category"] = "status";
  let icon = "i";
  let title = friendly(entry.eventType);
  let subtitle: string | null = entry.phase ? friendly(entry.phase) : null;

  if (entry.eventType === "contentCompleted" || entry.eventType === "contentDelta") {
    if (normalizedKind === "user") { category = "user"; icon = "You"; title = "You"; }
    else if (normalizedKind === "assistant") { category = "assistant"; icon = "A"; title = "CodeAlta"; }
    else if (normalizedKind.startsWith("reasoning")) { category = "reasoning"; icon = "◇"; title = normalizedKind === "reasoningsummary" ? "Reasoning summary" : "Reasoning"; }
    else if (normalizedKind === "plan") { category = "plan"; icon = "✓"; title = "Plan"; }
    else if (normalizedKind === "filechangeoutput") { category = "file"; icon = "Δ"; title = "File changes"; }
    else if (normalizedKind.endsWith("output")) { category = "tool"; icon = ">_"; title = friendly(kind); }
    else { title = friendly(kind || entry.eventType); }
    subtitle = streaming ? "Streaming" : null;
  } else if (entry.eventType === "activity") {
    category = normalizedKind === "filechange" ? "file" : "tool";
    icon = category === "file" ? "Δ" : "⌘";
    title = entry.name || friendly(kind || "Activity");
    subtitle = [friendly(entry.phase ?? ""), entry.name ? friendly(kind) : ""].filter(Boolean).join(" · ") || null;
  } else if (entry.eventType === "system_prompt") {
    category = "prompt"; icon = "Aa"; title = "Prompt information"; subtitle = entry.name || friendly(kind);
  } else if (entry.eventType === "planSnapshot") {
    category = "plan"; icon = "✓"; title = "Plan"; subtitle = friendly(kind);
  } else if (entry.eventType === "notes") {
    category = "notes"; icon = "▤"; title = "Alta notes"; subtitle = friendly(kind);
  } else if (entry.eventType === "error") {
    category = "error"; icon = "!"; title = "Error";
  } else if (entry.eventType.startsWith("permission")) {
    category = "status"; icon = "?"; title = entry.name || "Permission request"; subtitle = friendly(kind);
  } else if (entry.eventType === "userInputRequest") {
    category = "status"; icon = "?"; title = entry.name || "User input requested";
  } else if (entry.eventType === "sessionUpdate") {
    category = normalizedKind === "warning" ? "error" : "status";
    icon = normalizedKind === "usageupdated" ? "%" : normalizedKind === "modelchanged" ? "M" : "i";
    title = friendly(kind || "Session update");
  } else if (entry.eventType === "interaction") {
    category = "status"; icon = "✓"; title = friendly(kind || "Interaction");
  } else if (entry.eventType === "raw") {
    title = "Provider event";
  }

  const metadata = [
    entry.phase ? `Phase: ${friendly(entry.phase)}` : null,
    entry.providerId ? `Provider: ${entry.providerId}` : null,
    entry.runId ? `Run: ${entry.runId}` : null,
    entry.activityId ? `Activity: ${entry.activityId}` : null,
    entry.parentActivityId ? `Parent: ${entry.parentActivityId}` : null,
    entry.interactionId ? `Interaction: ${entry.interactionId}` : null,
  ].filter((value): value is string => value !== null);

  return {
    key: entry.offset,
    eventType: entry.eventType,
    category,
    icon,
    title,
    subtitle,
    timestamp: entry.timestamp,
    markdown: entry.text,
    details: formatDetails(entry.details),
    metadata,
    truncated: entry.textTruncated || entry.detailsTruncated,
    bodyOmitted: entry.bodyOmitted,
  };
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
