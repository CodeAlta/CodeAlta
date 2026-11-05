// Disposable page; the timeline of a session wired as its pane wires it, with its search: older turns read a chunk at a
// time, work folded between the messages, and a prompt under it that has the keyboard.
import { StrictMode, useCallback, useRef } from "react";
import { createRoot } from "react-dom/client";
import type { HistoryCursor, HistoryEntry, HistoryRequest } from "#neoastra";
import { History } from "../HistoryPanel";
import type { TimelinePage } from "../history";
import { createTimelineScrollMemory, useTimelinePosition } from "../timelineScroll";
import { findKeyKept } from "./findInPage";
import { useTimelineFind } from "./useTimelineFind";

const sessionId = "session";
const pageSize = 100;
const journal: HistoryEntry[] = [];
const add = (runId: string, values: Partial<HistoryEntry>) => journal.push({ offset: String(1000 + journal.length * 100), eventType: "contentCompleted", providerId: "provider", sessionId, runId,
  timestamp: new Date(Date.UTC(2026, 9, 1, 8, journal.length)).toISOString(), kind: "Assistant", phase: null, contentId: `content:${journal.length}`, activityId: null, parentActivityId: null, interactionId: null,
  sourceSessionId: null, prompt: null, name: null, text: null, details: null, textTruncated: false, tool: null, files: null, images: null, detailsTruncated: false, bodyOmitted: false, ...values });
// The older turns are read as the reader goes up: each says the word once, in its prompt.
for (let turn = 1; turn <= 4; turn++) {
  add(`run-${turn}`, { kind: "User", text: `Older prompt ${turn} with the needle` });
  for (let index = 0; index < 110; index++) add(`run-${turn}`, { text: `Older answer ${index}.` });
}
// The latest turn is much longer than the view: the word is at its start, in its middle and in folded work.
add("run-5", { kind: "User", text: "First **needle** of the latest turn" });
for (let index = 0; index < 60; index++) add("run-5", { text: `Answer ${index} of the turn.\n\nA paragraph long enough to take some room in the view.${index === 30 ? " The second NEEDLE is here." : ""}` });
add("run-5", { eventType: "activity", kind: "ToolCall", contentId: null, activityId: "call", name: "Bash", phase: "Completed",
  tool: { primary: "dotnet build foldedword", isCommand: true, output: null, outputLines: 0, outputBytes: null, exitCode: null, added: null, removed: null, fields: [] } as unknown as HistoryEntry["tool"] });
add("run-5", { text: "The last answer." });

async function read(request: HistoryRequest): Promise<TimelinePage> {
  const revision = { sessionId, length: String(journal.length * 100), lastWriteUtcTicks: "7" };
  const end = request.cursor ? journal.findIndex(entry => entry.offset === request.cursor!.offset) : journal.length;
  const start = Math.max(0, end - pageSize);
  const next: HistoryCursor | null = start > 0 ? { version: 2, ...revision, offset: journal[start].offset } : null;
  return { status: "ok", entries: journal.slice(start, end), next, tailOmitted: false, revision, sources: [] };
}

const state = { searchedSessions: 0, promptSearched: 0, moved: 0 };
// The window searches the sessions with Ctrl+F, except where a page keeps the key.
window.addEventListener("keydown", event => {
  if (event.key.toLowerCase() !== "f" || !event.ctrlKey || findKeyKept(event, event.target instanceof Element ? event.target : null)) return;
  event.preventDefault(); event.stopPropagation(); state.searchedSessions++;
}, true);
Object.assign(window, { timelineFindFixture: { state } });

const memory = createTimelineScrollMemory();
const noop = () => { };
function Pane() {
  const timeline = useTimelinePosition(sessionId, memory);
  const settled = useRef(() => { }); settled.current = () => timeline.settled();
  const onSettled = useCallback(() => settled.current(), []);
  const { finder, history, keys } = useTimelineFind(timeline, true, () => { state.moved++; });
  // The text editor of the prompt has a search of its own: it takes Ctrl+F for itself where it has the keyboard.
  const prompt = useCallback((element: HTMLTextAreaElement | null) => {
    element?.addEventListener("keydown", event => {
      if (event.key.toLowerCase() !== "f" || !(event.ctrlKey || event.metaKey)) return;
      event.preventDefault(); event.stopPropagation(); state.promptSearched++;
    });
  }, []);
  return <div className="ide-shell" style={{ height: "100%" }}>
    <div className="session-workspace" {...keys} style={{ height: "100%", display: "flex", flexDirection: "column" }}>
      <div className="session-timeline-area" style={{ position: "relative", flex: "1 1 auto", minHeight: 0 }}>
        {finder.bar}
        <div className="timeline-scroll" tabIndex={-1} data-following={timeline.following} ref={timeline.elementRef} style={{ height: "100%", overflowY: "auto" }}
          onScroll={event => timeline.scroll(event.currentTarget)} onWheel={timeline.wheel} onKeyDown={timeline.keyDown} onPointerDown={timeline.pointerDown}
          onPointerMove={timeline.pointerMove} onPointerUp={timeline.pointerEnd} onPointerCancel={timeline.pointerEnd}>
          <History {...history} sessionId={sessionId} read={read as never} live={null} onNotesChange={noop} onSettled={onSettled}
            onBeforeOlder={timeline.beforeOlderPage} onAfterOlder={timeline.afterOlderPage} olderRequest={timeline.olderRequestRef} />
        </div>
      </div>
      <textarea id="session-prompt" aria-label="Prompt" ref={prompt} />
    </div>
  </div>;
}

createRoot(document.getElementById("root")!).render(<StrictMode><Pane /></StrictMode>);
