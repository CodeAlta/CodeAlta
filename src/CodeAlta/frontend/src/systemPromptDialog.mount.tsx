// Disposable page; the production rows of system prompts and their window, over a reader the test feeds.
import { StrictMode, useState } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import { dismissDialogsOnOutsidePress } from "./modalDialogs";
import { ShellLanguageContext } from "./shellLanguage";
import { SystemPromptDiffContext, SystemPromptReaderContext } from "./SystemPromptDialog";
import { SystemPromptDiff } from "./SystemPromptDiff";
import { createSystemPromptCache } from "./systemPromptReader";
import { buildTimelineItems, type HistoryEntry } from "./timeline";
import { TimelineMessage } from "./TimelineMessage";

const state = { reads: [] as string[], copied: "" };
const replies = new Map<string, unknown>();
// A read that the test holds: the window is looked at while its prompt is still on the way.
let held: Promise<void> | null = null, release: (() => void) | null = null;
const reader = createSystemPromptCache(async request => {
  state.reads.push(`${request.offset}|${request.part ?? ""}`);
  if (held && request.part === null) await held;
  const reply = replies.get(request.offset) as { prompt?: { change?: unknown } } | undefined;
  if (!reply) return { status: "missing_record", prompt: null, text: null, change: null };
  return request.part === "change" ? { status: "ok", prompt: null, text: null, change: reply.prompt?.change ?? null } : reply;
}).reader("epoch", "session");

const entry = (values: Partial<HistoryEntry>): HistoryEntry => ({ offset: "1", sessionId: "session", providerId: "provider", runId: "run", activityId: null,
  contentId: null, parentActivityId: null, interactionId: null, sourceSessionId: null, prompt: null, eventType: "system_prompt", kind: "changed", name: null, phase: null, text: null,
  timestamp: "2026-10-13T09:50:36Z", details: null, textTruncated: false, detailsTruncated: false, bodyOmitted: false, files: null, tool: null, images: null, ...values });

let show: ((entries: HistoryEntry[]) => void) | undefined;
function Fixture() {
  const [entries, setEntries] = useState<HistoryEntry[]>([]);
  show = setEntries;
  // The rows as the timeline builds them from the records of a page.
  return <SystemPromptDiffContext.Provider value={SystemPromptDiff}><SystemPromptReaderContext.Provider value={reader}>
    <div className="ide-shell" style={{ display: "block", width: "100%" }}><div className="messages">
      {buildTimelineItems(entries).map(item => <TimelineMessage key={item.key} item={item} />)}
    </div></div>
  </SystemPromptReaderContext.Provider></SystemPromptDiffContext.Provider>;
}

Object.defineProperty(navigator, "clipboard", { value: { writeText: async (text: string) => { state.copied = text; } } });
// As the application does: a press outside a window dismisses it.
dismissDialogsOnOutsidePress(document);
Object.assign(window, { promptFixture: { state,
  show(entries: Partial<HistoryEntry>[]) { flushSync(() => show!(entries.map(entry))); },
  /** What the host answers for the prompt record at an offset. */
  record(offset: string, prompt: object) { replies.set(offset, { status: "ok", text: null, change: null, prompt }); },
  hold() { held = new Promise(resolve => { release = resolve; }); },
  release() { release?.(); held = null; release = null; },
} });
createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => { } }}><Fixture /></ShellLanguageContext.Provider>
</StrictMode>);
