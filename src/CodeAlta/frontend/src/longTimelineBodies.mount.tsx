import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import { TimelineMessage } from "./TimelineMessage";
import { buildTimelineItems, type HistoryEntry, type TimelineItem } from "./timeline";
import { ShellLanguageContext } from "./shellLanguage";
import type { Locale } from "./localization";

const root = createRoot(document.getElementById("app")!);
const source = "x".repeat(239) + "😀\n\n**supplied** [link](https://example.invalid)\n\n" + "prose\n\n".repeat(220)
  + '<img src=x onerror="window.injected=true"><script>window.injected=true</script> END';
const original: HistoryEntry = { offset: "1", eventType: "contentCompleted", kind: "Reasoning", phase: null,
  sessionId: "one", providerId: "provider", runId: "run", contentId: "body", activityId: null,
  parentActivityId: null, interactionId: null, name: null, timestamp: "2026-09-27T00:00:00Z",
  files: null, text: source, details: '{"literal":"<img src=x>"}', textTruncated: true, detailsTruncated: false, bodyOmitted: true };
let entry = original, patch: Partial<TimelineItem> = {}, locale: Locale = "en", generation = 0;
const copies: string[] = [];
Object.defineProperty(navigator, "clipboard", { value: { writeText: async (value: string) => { copies.push(value); } } });
function render() {
  const captured = generation;
  flushSync(() => root.render(<ShellLanguageContext.Provider value={{ locale, choice: locale, setLanguage: () => {} }}>
    <TimelineMessage item={{ ...buildTimelineItems([entry])[0], ...patch }} canInspect={() => generation === captured} />
  </ShellLanguageContext.Provider>));
}
Object.assign(window, { bodyFixture: { source, copies,
  render, replace(value: Partial<HistoryEntry>) { entry = { ...original, ...value }; patch = {}; render(); },
  item(value: Partial<TimelineItem>) { patch = value; render(); },
  language(value: Locale) { locale = value; render(); },
  scope() { generation++; render(); },
  expectedCopy() { return buildTimelineItems([entry])[0].copyMarkdown; },
  unmount() { flushSync(() => root.unmount()); },
} });
render();
