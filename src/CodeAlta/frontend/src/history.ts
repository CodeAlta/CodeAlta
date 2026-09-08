import type { HistoryRequest, HistoryResponse } from "#neoastra";

export type HistoryState =
  | { kind: "loading"; request: HistoryRequest }
  | { kind: "ready"; request: HistoryRequest; page: HistoryResponse }
  | { kind: "error"; request: HistoryRequest; code: string };

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

export function historyMessage(code: string): string {
  switch (code) {
    case "unconfigured": return "History requires an explicitly admitted trusted catalog COPY and cache-write opt-in.";
    case "missing_session": return "The persisted journal is no longer available. This does not refresh the catalog snapshot.";
    case "outside_root": return "The cached journal path is outside this copy's sessions root. No journal was opened through the history route and no cache repair was attempted.";
    case "invalid_cursor": return "The history cursor is invalid. Restart history from the beginning.";
    case "history_changed": return "The journal changed. This page was discarded; restart history to read a new revision.";
    case "unsupported_format": return "This bounded reader supports UTF-8 LF/CRLF journals only. Legacy encoding/framing or unsupported records cannot be displayed here.";
    case "record_too_large": return "A journal record exceeds the 128 KiB history limit. It was not skipped; later history is unavailable through this reader.";
    case "corrupt_record": return "An interior journal record is malformed. It was not skipped or repaired.";
    case "wire_limit": return "The page exceeds desktop display limits. No partial page or advancing cursor was published.";
    default: return "The history page could not be read. Check the trusted copy and cache access; no alternate history scan was used.";
  }
}
