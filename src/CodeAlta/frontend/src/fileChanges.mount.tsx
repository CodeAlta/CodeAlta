import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import { TimelineMessage } from "./TimelineMessage";
import { buildTimelineItems, type HistoryEntry } from "./timeline";
import { ShellLanguageContext } from "./shellLanguage";
import type { Locale } from "./localization";

// Literal history only. Mount the production projection and renderer, not a facsimile.
const root = createRoot(document.getElementById("app")!);
const diff = "@@ -1 +1 @@\n-<img src=x onerror=alert(1)>\n+<script>literal</script>\n";
const details = JSON.stringify({ changes: [
  { path: "<img src=x onerror=alert(1)>.ts", kind: { type: "unknown <b>" }, diff },
  { path: "same/path", operation: "delete", diff: "unsupported binary" },
  { path: "same/path" },
] });
let entry: HistoryEntry = { offset: "1", eventType: "activity", providerId: "provider", sessionId: "one", runId: "run",
  timestamp: "2026-09-22T10:00:00Z", kind: "FileChange", phase: "Failed", contentId: null, activityId: "activity",
  parentActivityId: null, interactionId: null, name: null, text: null, details,
  tool: null, files: null, textTruncated: false, detailsTruncated: false, bodyOmitted: false };
let locale: Locale = "en", generation = 0;
const copies: string[] = [];
Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText: async (text: string) => { copies.push(text); } } });
function render() {
  const captured = generation;
  flushSync(() => root.render(<ShellLanguageContext.Provider value={{ locale, choice: locale, setLanguage: () => {} }}>
    <TimelineMessage item={buildTimelineItems([entry])[0]} canInspect={() => captured === generation} />
  </ShellLanguageContext.Provider>));
}
Object.assign(window, { fileFixture: { diff, details, copies,
  render, language(value: Locale) { locale = value; render(); },
  replace(patch: Partial<HistoryEntry>) { entry = { ...entry, ...patch }; render(); },
  scope() { generation++; render(); },
  unmount() { flushSync(() => root.unmount()); },
} });
render();
