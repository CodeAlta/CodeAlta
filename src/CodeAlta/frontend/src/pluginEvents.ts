import type { HistoryResponse, SessionPluginEvent, SessionPluginEventsRequest } from "#neoastra";
import type { TimelineItem } from "./timeline";

type Entry = HistoryResponse["entries"][number];
type Invoke = (request: SessionPluginEventsRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<unknown>;

/** Cards for the loaded window, "retry" when the journal changed under the read, or null when there are none to show. */
export type PluginEventsRead = (notBefore: string, signal: AbortSignal) => Promise<readonly SessionPluginEvent[] | "retry" | null>;

const maximumEvents = 32;
const turnEnded = (entry: Entry) => entry.eventType === "error"
  || entry.eventType === "sessionUpdate" && (entry.kind === "Idle" || entry.kind === "Shutdown")
  || entry.eventType === "activity" && entry.kind === "Turn" && ["Completed", "Failed", "Canceled"].includes(entry.phase ?? "");

/**
 * What the plugin cards of a loaded history window depend on: its oldest record and the last turn that ended
 * in it. Plugins only summarize finished turns, so a window without one has no cards, and records streaming
 * into a running turn do not change the key.
 */
export function pluginEventsWindow(entries: readonly Entry[]): { key: string; notBefore: string } | null {
  for (let index = entries.length - 1; index >= 0; index--) {
    if (turnEnded(entries[index])) return { key: `${entries[0].offset}:${entries[index].offset}`, notBefore: entries[0].timestamp };
  }
  return null;
}

/**
 * Shows a plugin card as a compact timeline row: a leading bold phrase of its Markdown is the title and the
 * rest the one-line summary; the detail sections open from the row's details.
 */
export function pluginEventItem(event: SessionPluginEvent): TimelineItem {
  const heading = /^\*\*([^*\n]{1,80})\*\*[ \t]*(?:·[ \t]*)?/u.exec(event.markdown);
  const summary = (heading ? event.markdown.slice(heading[0].length) : event.markdown).trim();
  const detailMarkdown = event.details.map(detail => event.details.length > 1 ? `### ${detail.header}\n\n${detail.markdown}` : detail.markdown).join("\n\n") || null;
  return {
    key: `plugin:${event.eventId}`, eventType: "plugin", category: "plugin", icon: "usage",
    title: heading ? heading[1] : event.pluginId, subtitle: null, timestamp: event.timestamp,
    markdown: null, summary: summary || null, summaryIsCode: false,
    detailMarkdown, details: null, detailsLabel: event.details.length === 1 ? event.details[0].header : "Details",
    metadata: [`Plugin: ${event.pluginId}`], truncated: false, bodyOmitted: false,
    copyMarkdown: [event.markdown, detailMarkdown].filter(value => !!value).join("\n\n") || null,
  };
}

/** The cards to show with a window whose oldest record has the given timestamp, as timeline items. */
export function pluginEventItems(events: readonly SessionPluginEvent[], oldest: string | undefined): TimelineItem[] {
  const from = Date.parse(oldest ?? "");
  return Number.isFinite(from) ? events.filter(event => Date.parse(event.timestamp) >= from).map(pluginEventItem) : [];
}

function text(value: unknown, maximum: number): value is string { return typeof value === "string" && value.length <= maximum; }
function card(value: unknown): value is SessionPluginEvent {
  if (!value || typeof value !== "object") return false;
  const event = value as Record<string, unknown>;
  return text(event.eventId, 512) && event.eventId.length > 0 && text(event.pluginId, 128) && text(event.markdown, 4096)
    && text(event.timestamp, 64) && Number.isFinite(Date.parse(event.timestamp))
    && Array.isArray(event.details) && event.details.length <= 4 && event.details.every(detail => !!detail && typeof detail === "object"
      && text((detail as Record<string, unknown>).header, 128) && text((detail as Record<string, unknown>).markdown, 16 * 1024));
}

/** Reads the plugin cards of one session from the host; anything but a well-formed answer for that session is dropped. */
export function createPluginEventsRead(invoke: Invoke, target: { epoch: string; sessionId: string; projectId: string | null }): PluginEventsRead {
  return async (notBefore, signal) => {
    const reply = await invoke({ expectedHostEpoch: target.epoch, sessionId: target.sessionId, projectId: target.projectId, notBefore },
      { signal, timeoutMilliseconds: 15000 }) as { status?: unknown; sessionId?: unknown; events?: unknown } | null;
    if (reply?.status === "history_changed") return "retry";
    if (reply?.status !== "ok" || reply.sessionId !== target.sessionId || !Array.isArray(reply.events)
      || reply.events.length > maximumEvents || !reply.events.every(card)) return null;
    return reply.events;
  };
}
