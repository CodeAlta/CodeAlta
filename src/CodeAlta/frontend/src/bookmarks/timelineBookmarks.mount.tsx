import { StrictMode, useCallback, useRef, useState, useSyncExternalStore } from "react";
import { createRoot } from "react-dom/client";
import type { BookmarkRow, HistoryCursor, HistoryEntry, HistoryRequest } from "#neoastra";
import { History } from "../HistoryPanel";
import type { TimelinePage } from "../history";
import { createTimelineScrollMemory, useTimelinePosition } from "../timelineScroll";
import { BookmarkRail } from "./BookmarkRail";
import { createBookmarksHub, type BookmarksApi } from "./bookmarksHub";
import { createRevealRequests, type RevealOutcome, type RevealRequest } from "./revealRequests";
import { createTimelineBookmarks, TimelineBookmarksContext } from "./timelineBookmarks";

// The timeline of a session with its bookmarks, wired as the pane of a session wires it: the history over a journal of
// forty turns read a page at a time, the marks of its rows, the rail beside it and the requests that show a message.
const sessionId = "session", turns = 40, perTurn = 20, pageSize = 100;
const journal: HistoryEntry[] = [];
for (let turn = 1; turn <= turns; turn++) for (let index = 0; index < perTurn; index++) {
  const user = index === 0;
  journal.push({ offset: String(1000 + journal.length * 100), eventType: "contentCompleted", providerId: "provider", sessionId, runId: `run-${turn}`,
    timestamp: new Date(Date.UTC(2026, 9, 1, 8, turn, index)).toISOString(), kind: user ? "User" : "Assistant", phase: null,
    contentId: user ? `user:${turn}` : `msg:${turn}:${index}`, activityId: null, parentActivityId: null, interactionId: null, sourceSessionId: null, name: null,
    text: user ? `Prompt of turn ${turn}` : `**Answer ${index}** of turn ${turn}.\n\nIt says something about step ${index}.`, details: null, textTruncated: false,
    tool: null, files: null, images: null, detailsTruncated: false, bodyOmitted: false });
}
const revision = { sessionId, length: "900000", lastWriteUtcTicks: "7" };
const state = { reads: [] as (string | null)[], outcomes: [] as RevealOutcome[], rows: [] as BookmarkRow[], notify: [] as (() => void)[], serial: 0, calls: [] as string[] };

/** Reverse pages of the journal: the newest records first, then those before the cursor. */
async function read(request: HistoryRequest): Promise<TimelinePage> {
  state.reads.push(request.cursor?.offset ?? null);
  const end = request.cursor ? journal.findIndex(entry => entry.offset === request.cursor!.offset) : journal.length;
  const start = Math.max(0, end - pageSize);
  const next: HistoryCursor | null = start > 0 ? { version: 2, ...revision, offset: journal[start].offset } : null;
  return { status: "ok", entries: journal.slice(start, end), next, tailOmitted: false, revision, sources: [] };
}

const changed = () => { for (const tell of state.notify) tell(); };
function bookmarkOf(entry: HistoryEntry, label: string | null = null): BookmarkRow {
  return { id: `b${++state.serial}`, kind: "message", sessionId, projectId: null, sessionTitle: "The session", sessionMissing: false, role: entry.kind === "User" ? "user" : "assistant",
    label, excerpt: entry.text!.replace(/\s+/g, " "), runId: entry.runId, contentId: entry.contentId, offset: entry.offset, messageAt: entry.timestamp,
    createdAt: new Date(Date.UTC(2026, 9, 12, 9, 0, state.serial)).toISOString(), createdBy: null };
}
// A bookmark kept from before: an answer of the third turn, which the first window does not hold. The offset it kept is no longer right.
const old = journal.find(entry => entry.contentId === "msg:3:5")!;
state.rows.push({ ...bookmarkOf(old, "The old decision"), offset: "1" });

const api = {
  list: async () => ({ status: "ok", bookmarks: [...state.rows], truncated: false }),
  add: async (request: { sessionId: string; offset?: string | null }) => {
    state.calls.push(`add:${request.offset ?? ""}`);
    const entry = journal.find(candidate => candidate.offset === request.offset);
    if (!entry) return { status: "not_bookmarkable", bookmark: null };
    const held = state.rows.find(row => row.runId === entry.runId && row.contentId === entry.contentId);
    if (held) return { status: "ok", bookmark: held };
    const bookmark = bookmarkOf(entry);
    state.rows.unshift(bookmark); changed();
    return { status: "ok", bookmark };
  },
  rename: async () => ({ status: "not_found", bookmark: null }),
  remove: async (request: { id: string }) => {
    state.calls.push(`remove:${request.id}`);
    const found = state.rows.find(row => row.id === request.id);
    if (!found) return { status: "not_found", bookmark: null };
    state.rows = state.rows.filter(row => row !== found); changed();
    return { status: "ok", bookmark: found };
  },
  // Where the message is now: found by its turn and its id, whatever offset the bookmark kept.
  resolve: async (request: { id: string }) => {
    const found = state.rows.find(row => row.id === request.id);
    const entry = found && journal.find(candidate => candidate.runId === found.runId && candidate.contentId === found.contentId);
    return entry ? { status: "ok", sessionId, projectId: null, offset: entry.offset } : { status: found ? "gone" : "not_found", sessionId: null, projectId: null, offset: null };
  },
  watch: async (_request: unknown, options?: { signal?: AbortSignal }) => {
    const pending: number[] = [0];
    let wake: (() => void) | null = null, ended = false, count = 0;
    const tell = () => { pending.push(++count); wake?.(); };
    state.notify.push(tell);
    options?.signal?.addEventListener("abort", () => { ended = true; wake?.(); });
    return (async function* () {
      for (;;) {
        while (pending.length) yield { revision: pending.shift()! };
        if (ended) return;
        await new Promise<void>(resolve => { wake = resolve; });
      }
    })();
  },
} as unknown as BookmarksApi;

const hub = createBookmarksHub(api), reveals = createRevealRequests(), memory = createTimelineScrollMemory();
hub.connect("epoch");
const go = (bookmark: BookmarkRow) => void hub.resolve(bookmark.id).then(target => { if (target.status === "ok" && target.offset) reveals.open(sessionId, target.offset); else state.outcomes.push("missing"); });
const step: { current: ((direction: -1 | 1) => boolean) | null } = { current: null };
Object.assign(window, { timelineFixture: { state, hub, reveals, journal, step: (direction: -1 | 1) => step.current?.(direction) ?? false } });

const noop = () => { };
function Pane() {
  const timeline = useTimelinePosition(sessionId, memory);
  const [marks] = useState(() => createTimelineBookmarks((rowKey, bookmarkId) => void (bookmarkId ? hub.remove(bookmarkId) : hub.add(sessionId, rowKey))));
  const request = useSyncExternalStore(reveals.subscribe, () => reveals.forSession(sessionId));
  const settled = useRef(() => { }); settled.current = () => timeline.settled();
  const onSettled = useCallback(() => settled.current(), []);
  const onReveal = useCallback((asked: RevealRequest, outcome: RevealOutcome) => {
    reveals.done(asked);
    state.outcomes.push(outcome === "shown" && !timeline.showRow(asked.offset, true) ? "missing" : outcome);
  }, [timeline.showRow]);
  return <div className="ide-shell" style={{ height: "100%" }}><div className="session-workspace" style={{ height: "100%", display: "flex", flexDirection: "column" }}>
    <div className="session-timeline-area" style={{ position: "relative", flex: "1 1 auto", minHeight: 0 }}>
      <div className="timeline-scroll" ref={timeline.elementRef} style={{ height: "100%", overflowY: "auto" }} onScroll={event => timeline.scroll(event.currentTarget)}
        onWheel={timeline.wheel} onKeyDown={timeline.keyDown} onPointerDown={timeline.pointerDown} onPointerMove={timeline.pointerMove} onPointerUp={timeline.pointerEnd}>
        <TimelineBookmarksContext.Provider value={marks}>
          <History sessionId={sessionId} read={read as never} live={null} onNotesChange={noop} onSettled={onSettled}
            onBeforeOlder={timeline.beforeOlderPage} onAfterOlder={timeline.afterOlderPage} reveal={request} onReveal={onReveal} />
        </TimelineBookmarksContext.Provider>
      </div>
      <BookmarkRail sessionId={sessionId} hub={hub} marks={marks} scroll={timeline.elementRef} onGo={go} step={step} />
    </div>
  </div></div>;
}

createRoot(document.getElementById("root")!).render(<StrictMode><Pane /></StrictMode>);
