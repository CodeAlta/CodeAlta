import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import { SessionNotesOverlay } from "./SessionNotesOverlay";
import { TimelineMessage } from "./TimelineMessage";
import { buildTimelineItems, type HistoryEntry } from "./timeline";
import { createNotesReader } from "./sessionNotes";
import { createMutationCapability } from "./sessionOperations";
import { createSessionOverlayMemory } from "./sessionOverlayMemory";
import { saveWindowGeometry, type WindowGeometry } from "./windowGeometry";
import { WorkItemCards } from "./workItems/WorkItemCards";
import { workItems } from "./workItems/workItems";
import type { WorkItemsHub } from "./workItems/workItemsHub";

const storageKey = "codealta.desktop.notes-window.v1";
const dockKey = "codealta.desktop.proposals-dock.v1";
const root = createRoot(document.getElementById("app")!);
const epoch = "11111111-1111-4111-8111-111111111111";
let generation = 0, width = 1080, height = 600, proposals = 0, markdown = "", reads = 0, hidden = false;
// What the window keeps for the session while its pane is mounted again (a switch of space).
let memory = createSessionOverlayMemory();
const forbidden = () => { throw Error("The layout fixture must not act on real state."); };
const reader = createNotesReader(async request => {
  reads++;
  return { status: "ok", hostEpoch: epoch, sessionId: request.sessionId, markdown };
}, forbidden);
const capability = createMutationCapability(epoch);
const hub = { act: forbidden, read: forbidden } as unknown as WorkItemsHub;
const task = (id: string, title: string) => ({
  id, kind: "task" as const, status: "pending", title,
  summary: "A proposal should stay beside the notes without hiding the checklist or its controls.",
  category: "problem", statusText: null, created: "2026-10-10", file: `.alta/tasks/${id}.md`,
  proposedBy: "fixture", runner: null, acknowledged: false, runsWith: null,
});
const items = workItems([{ projectId: "fixture", truncated: false, plans: [],
  tasks: [task("proposal", "Keep the session overlays readable"), task("later", "A proposal that arrives later")] }], new Set());
const busy = new Set<string>();
const entry: HistoryEntry = { offset: "1", eventType: "contentCompleted", kind: "User", phase: null,
  sessionId: "fixture", providerId: "fixture", runId: "run", contentId: "body", activityId: null,
  parentActivityId: null, interactionId: null, sourceSessionId: null, prompt: null, name: null, timestamp: "2026-10-10T12:00:00Z",
  tool: null, files: null, images: null, text: "Please keep both the notes and the proposed task readable.",
  details: null, textTruncated: false, detailsTruncated: false, bodyOmitted: false };
const message = buildTimelineItems([entry])[0];

function render() {
  // The production session composition: its timeline scrolls, while Notes and proposals are siblings.
  // Only the outer fixture's dimensions are supplied here; no overlay geometry is copied into the test.
  flushSync(() => root.render(<div className="ide-shell" style={{ display: "block", width, height }}>
    <div className="session-workspace" style={{ width: "100%" }} key={generation}>
      <div className="session-timeline-area">
        <div className="timeline-scroll"><div className="messages"><TimelineMessage item={message} onBranch={forbidden} /></div></div>
        <SessionNotesOverlay sessionId="fixture" epoch={epoch} capability={capability} reader={reader} fallbackMarkdown="" hidden={hidden} memory={memory.notes("fixture")} />
        <WorkItemCards hub={hub} items={items.slice(0, proposals)} preferredStart="worktree" busy={busy} memory={memory.proposals("fixture")} onStart={forbidden} onOpenList={forbidden} />
      </div>
    </div>
  </div>));
}
Object.assign(window, { overlays: {
  reset(options: { width: number; height?: number; stored?: WindowGeometry; notes?: string; proposals?: boolean }) {
    width = options.width; height = options.height ?? 600; markdown = options.notes ?? ""; proposals = options.proposals ? 1 : 0; hidden = false;
    memory = createSessionOverlayMemory();
    localStorage.removeItem(dockKey);
    saveWindowGeometry(storageKey, options.stored ?? null); generation++; render();
  },
  /** Mounts the pane again with the same memory, as a switch of space does. */
  remount() { generation++; render(); },
  resize(value: number, tall = height) { width = value; height = tall; render(); },
  /** Shows the first proposal (true), none (false) or that many. */
  proposals(value: boolean | number) { proposals = Number(value); render(); },
  hide(value: boolean) { hidden = value; render(); },
  reads: () => reads,
  stored: () => localStorage.getItem(storageKey),
  dock: () => localStorage.getItem(dockKey),
  unmount() { flushSync(() => root.unmount()); },
} });
render();
