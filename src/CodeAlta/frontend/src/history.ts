import type { HistoryRequest, HistoryResponse, HistoryRevision, HistorySourceRange } from "#neoastra";

export type TimelinePage = HistoryResponse & { revision?: HistoryRevision | null; sources?: readonly HistorySourceRange[] };

export type HistoryState =
  | { kind: "loading"; request: HistoryRequest }
  | { kind: "ready"; request: HistoryRequest; page: TimelinePage }
  | { kind: "error"; request: HistoryRequest; code: string };

export type HistoryTimeline = Readonly<{
  sessionId: string;
  entries: HistoryResponse["entries"];
  next: HistoryResponse["next"];
  tailOmitted: boolean;
  limitReached: boolean;
  newerOmitted: boolean;
  revision?: HistoryRevision | null;
  sources?: readonly HistorySourceRange[];
  pages?: number;
  turnReached?: boolean;
}>;

const maximumTimelineEntries = 1000;
const maximumTimelineCharacters = 2 * 1024 * 1024;
export function historyEntryCharacters(entry: HistoryResponse["entries"][number]): number {
  return Object.values(entry).reduce<number>((sum, value) => sum + (typeof value === "string" ? value.length : 0), 0)
    + (entry.files?.rows.reduce((sum, row) => sum + row.path.length + (row.kind?.length ?? 0) + (row.diff?.length ?? 0), 0) ?? 0)
    + (entry.tool?.primary?.length ?? 0) + (entry.tool?.output?.length ?? 0)
    + (entry.tool?.fields.reduce((sum, field) => sum + field.path.length + field.text.length, 0) ?? 0);
}

export function historySettled(state: HistoryState | undefined, timeline: HistoryTimeline | undefined): boolean {
  return state?.kind === "error" || (state?.kind === "ready" && timeline?.sessionId === state.request.sessionId
    && (timeline.next === null || timeline.limitReached || timeline.turnReached === true));
}

// Only transient reads/revision changes can resume automatic latest-window reads.
// Missing/invalid/corrupt journals need manual action; callers still fence scope/older views.
export function historyCanRetry(state: HistoryState | undefined): boolean {
  return state?.kind === "error" && (state.code === "history_changed" || state.code === "read_failed");
}

const userPrompt = (entry: HistoryResponse["entries"][number]) => entry.kind?.toLowerCase() === "user"
  && (entry.eventType === "contentCompleted" || entry.eventType === "contentDelta");
// Offsets are canonical decimal strings: a longer one, or a greater one of equal length, is higher.
const offsetAfter = (left: string, right: string) => left.length > right.length || left.length === right.length && left > right;

/**
 * Merges one reverse page into the window being read.
 *
 * `known` is the settled window a refresh of the newest history started from: the journal only grows, so as
 * soon as the new pages reach a record that window already has, the window is kept and only the newer records
 * are appended. `olderFrom` is the first record of the window when the reader asked for older history: that
 * read continues page after page until it has brought in one more whole turn.
 */
export function mergeHistoryPage(previous: HistoryTimeline | undefined, request: HistoryRequest, page: TimelinePage,
  explicitOlder = false, retainedStart?: string, options: { known?: HistoryTimeline; olderFrom?: string } = {}): HistoryTimeline {
  const current = previous?.next;
  const cursor = request.cursor;
  // A new tail read starts afresh; never combine a forward cursor, another session or another revision.
  const retained = cursor?.version === 2 && current?.version === 2 && previous?.sessionId === request.sessionId
    && cursor.sessionId === current.sessionId && cursor.length === current.length
    && cursor.lastWriteUtcTicks === current.lastWriteUtcTicks && cursor.offset === current.offset ? previous : undefined;
  // A reverse cursor of the same journal revision that lies above the window's older boundary (or any
  // such cursor once the journal start is loaded) replays a merged page: it must not replace newer rows.
  const loaded = current?.version === 2 ? current : current === null ? previous?.revision : undefined;
  if (!retained && previous && cursor?.version === 2 && loaded && previous.sessionId === request.sessionId
    && cursor.sessionId === loaded.sessionId && cursor.length === loaded.length && cursor.lastWriteUtcTicks === loaded.lastWriteUtcTicks
    // Offsets are canonical decimal strings: a longer one, or a greater one of equal length, is higher.
    && (current === null || cursor.offset.length > current!.offset.length
      || cursor.offset.length === current!.offset.length && cursor.offset > current!.offset)) return previous;
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
  // Initial/latest navigation stops at the last user prompt, not an arbitrary
  // thousand-event window. The omitted prefix remains reachable by a byte cursor.
  let turnReached = false;
  const known = !explicitOlder && options.known?.sessionId === request.sessionId && !options.known.newerOmitted
    && options.known.entries.length > 0 ? options.known : undefined;
  let sourcesBefore = retained?.sources ?? [];
  if (known) {
    const have = new Set(known.entries.map(entry => entry.offset));
    if (entries.some(entry => have.has(entry.offset))) {
      const last = known.entries[known.entries.length - 1].offset;
      const appended = entries.filter(entry => offsetAfter(entry.offset, last));
      entries.splice(0, entries.length, ...known.entries, ...appended);
      // The older cursor keeps its position but must name the journal revision just read.
      const boundary = page.next ?? request.cursor ?? (page.revision ? { version: 2, ...page.revision, offset: "0" } : null);
      next = known.next === null ? null : boundary?.version === 2 ? { ...boundary, offset: known.next.offset } : known.next;
      sourcesBefore = [...sourcesBefore, ...(known.sources ?? [])];
      turnReached = true;
    }
  } else if (explicitOlder && options.olderFrom !== undefined) {
    // An older read brings in whole turns: it stops at the newest user prompt above the previous window start.
    const boundary = entries.findIndex(entry => entry.offset === options.olderFrom);
    let lastUser = -1;
    for (let index = (boundary < 0 ? entries.length : boundary) - 1; index >= 0; index--) {
      if (userPrompt(entries[index])) { lastUser = index; break; }
    }
    if (lastUser >= 0) {
      let start = lastUser;
      while (start > 0 && entries[start - 1].contentId === entries[lastUser].contentId
        && entries[start - 1].runId === entries[lastUser].runId && entries[start - 1].kind?.toLowerCase() === "user") start--;
      const cursor = page.next ?? request.cursor;
      if (start > 0 && cursor?.version === 2) {
        next = { ...cursor, offset: entries[start].offset };
        entries.splice(0, start);
      }
      turnReached = true;
    }
  } else if (!explicitOlder) {
    let lastUser = retainedStart === undefined ? -1 : entries.findIndex(entry => entry.offset === retainedStart);
    for (let index = entries.length - 1; index >= 0; index--) {
      if (retainedStart !== undefined) break;
      if (userPrompt(entries[index])) { lastUser = index; break; }
    }
    if (lastUser >= 0) {
      let start = lastUser;
      while (start > 0 && entries[start - 1].contentId === entries[lastUser].contentId
        && entries[start - 1].runId === entries[lastUser].runId && entries[start - 1].kind?.toLowerCase() === "user") start--;
      const boundary = page.next ?? request.cursor ?? (page.revision ? { version: 2, ...page.revision, offset: "0" } : null);
      if (start > 0 && boundary?.version === 2) {
        next = { ...boundary, offset: entries[start].offset };
        entries.splice(0, start);
      }
      turnReached = true;
    }
  }
  let newerOmitted = retained?.newerOmitted === true;
  let characters = entries.reduce((sum, entry) => sum + historyEntryCharacters(entry), 0);
  let trimmed = false;
  while (entries.length > maximumTimelineEntries || characters > maximumTimelineCharacters) {
    trimmed = true;
    if (explicitOlder) {
      // An explicit older-page request slides the window; never imply that newer events remain visible.
      characters -= historyEntryCharacters(entries.pop()!);
      newerOmitted = true;
    } else {
      // An automatic initial read must *always* keep the latest turn. The first retained record
      // is a valid byte boundary for a truthful future older-page cursor, even if the page ended.
      characters -= historyEntryCharacters(entries.shift()!);
      const boundary = next ?? cursor;
      if (boundary?.version === 2) next = { ...boundary, offset: entries[0].offset };
    }
  }
  // Each read of older history gets its own page budget; the first page of one starts at the window's first record.
  const pages = explicitOlder && options.olderFrom !== undefined && retained?.entries[0]?.offset === options.olderFrom ? 1 : (retained?.pages ?? 0) + 1;
  const visible = new Set(entries.map(entry => entry.offset));
  const listed = new Set<string>();
  const sources = [...(page.sources ?? []), ...sourcesBefore].filter(source => visible.has(source.start) && !listed.has(source.start) && !!listed.add(source.start));
  return {
    sessionId: request.sessionId,
    entries,
    next,
    tailOmitted: retained?.tailOmitted === true || page.tailOmitted,
    limitReached: next !== null && (trimmed || entries.length === maximumTimelineEntries || pages >= 32),
    newerOmitted,
    revision: page.revision ?? retained?.revision, sources, pages, turnReached,
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
    case "timeline_record_too_large": return "A journal record exceeds the 8 MiB timeline limit. It was not skipped; previously loaded rows remain available.";
    case "corrupt_record": return "An interior journal record is malformed. It was not skipped or repaired.";
    case "wire_limit": return "The page exceeds desktop display limits. No partial page or advancing cursor was published.";
    default: return "The history page could not be read. Check the trusted copy and cache access; no alternate history scan was used.";
  }
}
