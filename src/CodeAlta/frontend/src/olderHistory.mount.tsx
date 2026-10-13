import { StrictMode, useCallback, useRef, useState } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import type { HistoryCursor, HistoryEntry, HistoryRequest } from "#neoastra";
import { History } from "./HistoryPanel";
import type { TimelinePage } from "./history";
import { createTimelineScrollMemory, useTimelinePosition, type MessageNavigation } from "./timelineScroll";

// The timeline of a session wired as its pane wires it: the history over a journal read a page at a time, in the
// element that scrolls it. Three journals: one whose latest turn is longer than the view, one whose latest turn
// is two messages, one whose latest turn is a little longer than the view. The test holds a read back, makes one fail and puts the timeline behind another tab.
const pageSize = 100;
function journalOf(sessionId: string, sizes: readonly number[]): HistoryEntry[] {
  const journal: HistoryEntry[] = [];
  sizes.forEach((size, at) => {
    const turn = at + 1;
    for (let index = 0; index < size; index++) {
      const user = index === 0;
      journal.push({ offset: String(1000 + journal.length * 100), eventType: "contentCompleted", providerId: "provider", sessionId, runId: `run-${turn}`,
        timestamp: new Date(Date.UTC(2026, 9, 1, 8, turn, index % 60)).toISOString(), kind: user ? "User" : "Assistant", phase: null,
        contentId: user ? `user:${turn}` : `msg:${turn}:${index}`, activityId: null, parentActivityId: null, interactionId: null, sourceSessionId: null, prompt: null, name: null,
        text: user ? `Prompt of turn ${turn}` : `**Answer ${index}** of turn ${turn}.\n\nIt says something about step ${index}.`, details: null, textTruncated: false,
        tool: null, files: null, images: null, detailsTruncated: false, bodyOmitted: false });
    }
  });
  return journal;
}
const journals: Record<string, HistoryEntry[]> = {
  long: journalOf("long", Array.from({ length: 9 }, () => 120)),
  short: journalOf("short", [...Array.from({ length: 6 }, () => 120), 2]),
  medium: journalOf("medium", [...Array.from({ length: 6 }, () => 120), 14]),
};
const state = { reads: [] as (string | null)[], failures: [] as string[], held: null as { wait: Promise<void>; release: () => void } | null };

/** Reverse pages of a journal: the newest records first, then those before the cursor. A read of older records can be held back or made to fail. */
async function read(request: HistoryRequest): Promise<TimelinePage> {
  const journal = journals[request.sessionId];
  const revision = { sessionId: request.sessionId, length: String(journal.length * 100), lastWriteUtcTicks: "7" };
  state.reads.push(request.cursor?.offset ?? null);
  if (request.cursor) {
    await state.held?.wait;
    const code = state.failures.shift();
    if (code) return { status: code, entries: [], next: null, tailOmitted: false };
  }
  const end = request.cursor ? journal.findIndex(entry => entry.offset === request.cursor!.offset) : journal.length;
  const start = Math.max(0, end - pageSize);
  const next: HistoryCursor | null = start > 0 ? { version: 2, ...revision, offset: journal[start].offset } : null;
  return { status: "ok", entries: journal.slice(start, end), next, tailOmitted: false, revision, sources: [] };
}

const memory = createTimelineScrollMemory();
const fixture = {
  state,
  /** Holds the reads of older records back until `release`. */
  hold() { let release = () => { }; const wait = new Promise<void>(resolve => { release = resolve; }); state.held = { wait, release }; },
  release() { state.held?.release(); state.held = null; },
  show: (_view: { sessionId?: string; observing?: boolean }) => { },
  navigate: (_action: MessageNavigation): unknown => null,
  /** Shows the first message of a turn at the top of the view, as going to a bookmark does. */
  showTurn: (_turn: number): boolean => false,
};
Object.assign(window, { olderFixture: fixture });

const noop = () => { };
function Pane({ sessionId, observing }: { sessionId: string; observing: boolean }) {
  const timeline = useTimelinePosition(sessionId, memory);
  const settled = useRef(() => { }); settled.current = () => timeline.settled();
  const onSettled = useCallback(() => settled.current(), []);
  fixture.navigate = timeline.navigateMessage;
  fixture.showTurn = turn => timeline.showRow(journals[sessionId].find(entry => entry.contentId === `user:${turn}`)!.offset);
  return <div className="ide-shell" style={{ height: "100%" }}><div className="session-workspace" style={{ height: "100%", display: "flex", flexDirection: "column" }}>
    <div className="session-timeline-area" style={{ position: "relative", flex: "1 1 auto", minHeight: 0 }}>
      <div className="timeline-scroll" data-following={timeline.following} ref={timeline.elementRef} style={{ height: "100%", overflowY: "auto" }}
        onScroll={event => timeline.scroll(event.currentTarget)} onWheel={timeline.wheel} onKeyDown={timeline.keyDown} onPointerDown={timeline.pointerDown}
        onPointerMove={timeline.pointerMove} onPointerUp={timeline.pointerEnd} onPointerCancel={timeline.pointerEnd}>
        <History sessionId={sessionId} observing={observing} read={read as never} live={null} onNotesChange={noop} onSettled={onSettled}
          onBeforeOlder={timeline.beforeOlderPage} onAfterOlder={timeline.afterOlderPage} olderRequest={timeline.olderRequestRef} />
      </div>
    </div>
  </div></div>;
}

function Fixture() {
  const [view, setView] = useState({ sessionId: "long", observing: true });
  fixture.show = next => flushSync(() => setView(current => ({ ...current, ...next })));
  return <Pane key={view.sessionId} sessionId={view.sessionId} observing={view.observing} />;
}

createRoot(document.getElementById("root")!).render(<StrictMode><Fixture /></StrictMode>);
