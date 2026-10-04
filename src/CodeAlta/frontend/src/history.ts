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

/** What the first read of a session keeps: its latest turn, up to this many records and characters. */
const maximumInitialEntries = 1000;
const maximumInitialCharacters = 2 * 1024 * 1024;
/**
 * What a window may grow to once the reader asks for older history: enough for the whole journal of a long
 * session. Beyond it the window slides instead of growing.
 */
export const maximumTimelineEntries = 200_000;
export const maximumTimelineCharacters = 128 * 1024 * 1024;
/** A read of older history brings in whole turns until it has added at least this many records. */
export const olderChunkEntries = 300;
/** Pages one chunk of older history may read; reading everything has no such budget. */
const olderChunkPages = 32;
// A record's size never changes, and a long window is measured again with every page that joins it.
const measured = new WeakMap<HistoryResponse["entries"][number], number>();
export function historyEntryCharacters(entry: HistoryResponse["entries"][number]): number {
  let size = measured.get(entry);
  if (size === undefined) measured.set(entry, size = Object.values(entry).reduce<number>((sum, value) => sum + (typeof value === "string" ? value.length : 0), 0)
    + (entry.files?.rows.reduce((sum, row) => sum + row.path.length + (row.kind?.length ?? 0) + (row.diff?.length ?? 0), 0) ?? 0)
    + (entry.tool?.primary?.length ?? 0) + (entry.tool?.output?.length ?? 0)
    + (entry.tool?.fields.reduce((sum, field) => sum + field.path.length + field.text.length, 0) ?? 0));
  return size;
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
 * read continues page after page until it has brought in a chunk of whole turns (at least
 * `olderChunkEntries` records), or, with `all`, until it reaches the start of the journal.
 */
/**
 * A turn's preparation records (the model in use, the system prompt) are written just before the user's
 * message and belong to its run. Returns the index where the turn of the message at `index` really starts,
 * so a window cut at a turn keeps them and does not leave them behind as "previous" history.
 */
function turnStart(entries: readonly HistoryResponse["entries"][number][], index: number): number {
  const run = entries[index].runId;
  let start = index;
  for (let at = index - 1; at >= 0; at--) {
    const entry = entries[at];
    if (entry.eventType === "raw") continue;
    const setup = entry.eventType === "system_prompt" || entry.eventType === "sessionUpdate" && entry.kind?.toLowerCase() === "modelchanged";
    if (!setup || entry.runId !== run) break;
    start = at;
  }
  return start;
}

export function mergeHistoryPage(previous: HistoryTimeline | undefined, request: HistoryRequest, page: TimelinePage,
  explicitOlder = false, retainedStart?: string, options: { known?: HistoryTimeline; olderFrom?: string; all?: boolean } = {}): HistoryTimeline {
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
  // A window may hold hundreds of thousands of records: never spread it into an argument list.
  for (const entry of accumulated) entries.push(entry);
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
      entries.length = 0;
      for (const entry of known.entries) entries.push(entry);
      for (const entry of appended) entries.push(entry);
      // The older cursor keeps its position but must name the journal revision just read.
      const boundary = page.next ?? request.cursor ?? (page.revision ? { version: 2, ...page.revision, offset: "0" } : null);
      next = known.next === null ? null : boundary?.version === 2 ? { ...boundary, offset: known.next.offset } : known.next;
      sourcesBefore = [...sourcesBefore, ...(known.sources ?? [])];
      turnReached = true;
    }
  } else if (explicitOlder && options.olderFrom !== undefined && !options.all) {
    // An older read brings in whole turns, a chunk at a time: once enough records lie above the previous
    // window start, the window is cut at the oldest turn that was read completely. A turn whose start is
    // the first record read may continue in the page before it, so it is not a place to cut yet.
    const boundary = entries.findIndex(entry => entry.offset === options.olderFrom);
    const fresh = boundary < 0 ? entries.length : boundary;
    if (fresh >= olderChunkEntries || page.next === null) {
      for (let index = 0; index < fresh; index++) {
        if (!userPrompt(entries[index])) continue;
        let start = index;
        while (start > 0 && entries[start - 1].contentId === entries[index].contentId
          && entries[start - 1].runId === entries[index].runId && entries[start - 1].kind?.toLowerCase() === "user") start--;
        start = turnStart(entries, start);
        const cursor = page.next ?? request.cursor;
        if (start > 0 && page.next !== null && cursor?.version === 2) {
          next = { ...cursor, offset: entries[start].offset };
          entries.splice(0, start);
          turnReached = true;
          break;
        }
        if (page.next === null) { turnReached = true; break; }
        // Skip the rest of this prompt's records: they are the same, incomplete, turn start.
        while (index + 1 < fresh && userPrompt(entries[index + 1]) && entries[index + 1].contentId === entries[index].contentId) index++;
      }
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
      start = turnStart(entries, start);
      const boundary = page.next ?? request.cursor ?? (page.revision ? { version: 2, ...page.revision, offset: "0" } : null);
      if (start > 0 && boundary?.version === 2) {
        next = { ...boundary, offset: entries[start].offset };
        entries.splice(0, start);
      }
      turnReached = true;
    }
  }
  let newerOmitted = retained?.newerOmitted === true;
  // The first read of a session is small. A window the reader extended with older history, and the refreshes
  // that keep it, may hold the whole journal.
  const extended = explicitOlder || !!known;
  const maximumEntries = extended ? maximumTimelineEntries : maximumInitialEntries;
  const maximumCharacters = extended ? maximumTimelineCharacters : maximumInitialCharacters;
  let characters = 0;
  // Counted from the newest record: an automatic read drops what is over the limit at the old end.
  let keepFrom = entries.length;
  while (keepFrom > 0 && entries.length - keepFrom < maximumEntries) {
    const size = historyEntryCharacters(entries[keepFrom - 1]);
    if (characters + size > maximumCharacters && keepFrom < entries.length) break;
    characters += size;
    keepFrom--;
  }
  let trimmed = keepFrom > 0;
  if (trimmed && explicitOlder) {
    // An explicit older-page request slides the window; never imply that newer events remain visible.
    characters = 0;
    let keepTo = 0;
    while (keepTo < entries.length && keepTo < maximumEntries) {
      const size = historyEntryCharacters(entries[keepTo]);
      if (characters + size > maximumCharacters && keepTo > 0) break;
      characters += size;
      keepTo++;
    }
    trimmed = keepTo < entries.length;
    if (trimmed) { entries.length = keepTo; newerOmitted = true; }
  } else if (trimmed) {
    // An automatic read must *always* keep the latest turn. The first retained record is a valid byte
    // boundary for a truthful future older-page cursor, even if the page ended.
    entries.splice(0, keepFrom);
    const boundary = next ?? cursor;
    if (boundary?.version === 2) next = { ...boundary, offset: entries[0].offset };
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
    limitReached: next !== null && (trimmed || !extended && entries.length === maximumInitialEntries
      || !(explicitOlder && options.all) && pages >= olderChunkPages),
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
