// Disposable page; the production window of a tool call, with a record reader and a live output the test feeds.
import { StrictMode, useState } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import { dismissDialogsOnOutsidePress } from "./modalDialogs";
import { ShellLanguageContext } from "./shellLanguage";
import { buildTimelineItems, type HistoryEntry } from "./timeline";
import { ToolCallDialog } from "./ToolCallDialog";
import { createToolCallCache } from "./toolCallReader";
import { createToolOutputStore } from "./toolOutput";

type OutputItem = { status: string; text: string; start: string; total: string; isReset: boolean; isComplete: boolean };
const state = { closed: 0, opened: [] as string[], reads: [] as string[], injected: 0 };
const records = new Map<string, unknown>();
// One channel per observed call: `push` delivers an item to the page.
const channels = new Map<string, { push(item: OutputItem): void }>();
const outputs = createToolOutputStore(async request => {
  const waiting: ((result: IteratorResult<OutputItem>) => void)[] = [], queued: OutputItem[] = [];
  channels.set(request.activityId, { push(item) { const next = waiting.shift(); if (next) next({ done: false, value: item }); else queued.push(item); } });
  state.opened.push(request.activityId);
  return { [Symbol.asyncIterator]: () => ({
    next: () => new Promise<IteratorResult<OutputItem>>(resolve => { const ready = queued.shift(); if (ready) resolve({ done: false, value: ready }); else waiting.push(resolve); }),
    return: async () => ({ done: true as const, value: undefined }),
  }) };
}).session("epoch", "session");
const reader = createToolCallCache(async request => {
  state.reads.push(`${request.offset}|${request.outputOffset ?? ""}|${request.part ?? ""}`);
  return records.get(`${request.offset}|${request.outputOffset ?? ""}`) ?? { status: "missing_record", call: null, text: null };
}).reader("epoch", "session");

const entry = (values: Partial<HistoryEntry>): HistoryEntry => ({ offset: "1", sessionId: "session", providerId: "provider", runId: "run", activityId: null,
  contentId: null, parentActivityId: null, interactionId: null, sourceSessionId: null, eventType: "activity", kind: "ToolCall", name: null, phase: null, text: null,
  timestamp: "2026-10-07T08:00:00Z", details: null, textTruncated: false, detailsTruncated: false, bodyOmitted: false, files: null, tool: null, images: null, ...values });

let show: ((entries: HistoryEntry[] | null) => void) | undefined;
function Fixture() {
  const [entries, setEntries] = useState<HistoryEntry[] | null>(null);
  show = setEntries;
  // The row of the call as the timeline builds it from its records.
  const item = entries && buildTimelineItems(entries)[0];
  return item ? <ToolCallDialog item={item} reader={reader} outputs={outputs} current={() => true} onClose={() => { state.closed++; setEntries(null); }} /> : null;
}

Object.defineProperty(navigator, "clipboard", { value: { writeText: async () => { } } });
// As the application does: a press outside a window dismisses it.
dismissDialogsOnOutsidePress(document);
Object.assign(window, { toolFixture: { state,
  /** Shows the call these records describe; given again with other records, the open window follows them. */
  show(entries: Partial<HistoryEntry>[]) { flushSync(() => show!(entries.map(entry))); },
  close() { flushSync(() => show!(null)); },
  record(offset: string, outputOffset: string | null, call: object) { records.set(`${offset}|${outputOffset ?? ""}`, { status: "ok", text: null, call }); },
  push(activityId: string, item: Partial<OutputItem>) {
    channels.get(activityId)!.push({ status: "ok", text: "", start: "0", total: "0", isReset: false, isComplete: false, ...item });
  },
} });
createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => { } }}><Fixture /></ShellLanguageContext.Provider>
</StrictMode>);
