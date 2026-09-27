import type { HistoryRequest, HistoryResponse } from "#neoastra";

export type HistoryState =
  | { kind: "loading"; request: HistoryRequest }
  | { kind: "ready"; request: HistoryRequest; page: HistoryResponse }
  | { kind: "error"; request: HistoryRequest; code: string };

export type HistoryTimeline = Readonly<{
  sessionId: string;
  entries: HistoryResponse["entries"];
  next: HistoryResponse["next"];
  tailOmitted: boolean;
  limitReached: boolean;
  newerOmitted: boolean;
}>;

const maximumTimelineEntries = 1000;

export function historySettled(state: HistoryState | undefined, timeline: HistoryTimeline | undefined): boolean {
  return state?.kind === "error" || (state?.kind === "ready" && timeline?.sessionId === state.request.sessionId
    && (timeline.next === null || timeline.entries.length === maximumTimelineEntries));
}

export function mergeHistoryPage(previous: HistoryTimeline | undefined, request: HistoryRequest, page: HistoryResponse,
  explicitOlder = false): HistoryTimeline {
  const current = previous?.next;
  const cursor = request.cursor;
  // A new tail read starts afresh; never combine a forward cursor, another session or another revision.
  const retained = cursor?.version === 2 && current?.version === 2 && previous?.sessionId === request.sessionId
    && cursor.sessionId === current.sessionId && cursor.length === current.length
    && cursor.lastWriteUtcTicks === current.lastWriteUtcTicks && cursor.offset === current.offset ? previous : undefined;
  const accumulated = retained?.entries ?? [];
  const offsets = new Set(accumulated.map(entry => entry.offset));
  const entries: HistoryResponse["entries"][number][] = [];
  for (const entry of page.entries) {
    if (!offsets.has(entry.offset)) {
      offsets.add(entry.offset);
      entries.push(entry);
    }
  }
  entries.push(...accumulated);
  let next = page.next;
  let newerOmitted = retained?.newerOmitted === true;
  if (entries.length > maximumTimelineEntries) {
    if (explicitOlder) {
      // An explicit older-page request slides the window; never imply that newer events remain visible.
      entries.length = maximumTimelineEntries;
      newerOmitted = true;
    } else {
      // An automatic initial read must *always* keep the latest turn. The first retained record
      // is a valid byte boundary for a truthful future older-page cursor, even if the page ended.
      entries.splice(0, entries.length - maximumTimelineEntries);
      const boundary = next ?? cursor;
      if (boundary?.version === 2) next = { ...boundary, offset: entries[0].offset };
    }
  }
  return {
    sessionId: request.sessionId,
    entries,
    next,
    tailOmitted: retained?.tailOmitted === true || page.tailOmitted,
    limitReached: entries.length === maximumTimelineEntries && next !== null,
    newerOmitted,
  };
}

export async function loadHistory(
  invoke: (request: HistoryRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<HistoryResponse>,
  request: HistoryRequest,
  signal: AbortSignal,
  publish: (state: HistoryState) => void,
): Promise<void> {
  if (signal.aborted) return;
  publish({ kind: "loading", request });
  try {
    const page = await invoke(request, { signal, timeoutMilliseconds: 30_000 });
    if (signal.aborted) return;
    publish(page.status === "ok" ? { kind: "ready", request, page } : { kind: "error", request, code: page.status });
  } catch {
    if (signal.aborted) return;
    publish({ kind: "error", request, code: "read_failed" });
  }
}

export function historyMessage(code: string): import("./localization").MessageKey {
  switch (code) {
    case "unconfigured": return "History requires an explicitly admitted trusted catalog COPY and cache-write opt-in.";
    case "missing_session": return "The persisted journal is no longer available. This does not refresh the catalog snapshot.";
    case "outside_root": return "The cached journal path is outside this copy's sessions root. No journal was opened through the history route and no cache repair was attempted.";
    case "invalid_cursor": return "The history cursor is invalid. Refresh history to start again from the latest journal events.";
    case "history_changed": return "The journal changed. This page was discarded; refresh history to read the latest revision.";
    case "unsupported_format": return "This bounded reader supports UTF-8 LF/CRLF journals only. Legacy encoding/framing or unsupported records cannot be displayed here.";
    case "record_too_large": return "A journal record exceeds the 128 KiB history limit. It was not skipped; older history beyond it is unavailable through this reader.";
    case "corrupt_record": return "An interior journal record is malformed. It was not skipped or repaired.";
    case "wire_limit": return "The page exceeds desktop display limits. No partial page or advancing cursor was published.";
    default: return "The history page could not be read. Check the trusted copy and cache access; no alternate history scan was used.";
  }
}
