// Disposable page: the real setting and two histories share one window-owned preference.
import { StrictMode, useState } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import type { HistoryResponse } from "#neoastra";
import { History } from "../HistoryPanel";
import { TimelineBookmarksContext, type TimelineBookmarks } from "../bookmarks/timelineBookmarks";
import { SystemPromptReaderContext } from "../SystemPromptDialog";
import type { SystemPromptReader } from "../systemPromptReader";
import { colorSchemeOf, defaultColorScheme, schemePalette, showAppearance } from "../colorSchemes";
import { dismissDialogsOnOutsidePress } from "../modalDialogs";
import { ShellLanguageContext } from "../shellLanguage";
import type { Locale } from "../localization";
import { TimelineEmphasisSetting } from "./TimelineEmphasisSetting";
import { TimelineEmphasisContext } from "./useTimelineEmphasis";
import { createTimelineEmphasisStore } from "./timelineEmphasisStore";
import { timelineEmphasisKey } from "./timelineEmphasis";

type Entry = HistoryResponse["entries"][number];
const entry = (offset: string, kind: string, values: Partial<Entry> = {}): Entry => ({ offset, eventType: "contentCompleted", providerId: "provider",
  sessionId: "session", runId: "run", timestamp: `2026-10-12T00:00:${offset.padStart(2, "0")}Z`, kind, phase: null,
  contentId: `content-${offset}`, activityId: null, parentActivityId: null, interactionId: null, sourceSessionId: null, prompt: null, name: null,
  tool: null, files: null, images: null, text: `${kind} text`, details: null, textTruncated: false, detailsTruncated: false, bodyOmitted: false, ...values });
const promptFacts = { changed: false, hash: "appearance-fixture", sourcePath: null, totalTokens: 1200, systemTokens: 800, developerTokens: 400,
  applied: true, lossy: false, added: null, removed: null };
const entries = [entry("1", "User"), entry("2", "Assistant"), entry("3", "Reasoning"),
  entry("4", "ToolCall", { eventType: "activity", contentId: null, activityId: "call", name: "shell_command", phase: "Completed" }),
  entry("5", "ToolCall", { eventType: "activity", contentId: null, activityId: "call-two", name: "read_file", phase: "Completed" }),
  entry("6", "Plan"), entry("7", "Assistant"), entry("8", "Warning", { eventType: "sessionUpdate", contentId: null }),
  entry("9", "CommandExecution", { eventType: "permissionRequest", contentId: null }),
  entry("10", "ToolCall", { eventType: "activity", contentId: null, activityId: "failed", name: "shell_command", phase: "Failed" }),
  entry("11", "ToolCall", { eventType: "activity", contentId: null, activityId: "completed-with-failure", name: "read_file", phase: "Completed" }),
  entry("12", "User", { text: "[CodeAlta background job]\nJob: job-canceled\nTitle: Verify\nCommand: dotnet test\nResult: stopped before it ended after 1 s\n\nOutput:\nstopped" }),
  entry("13", "ToolCall", { eventType: "activity", contentId: null, activityId: "canceled", name: "shell_command", phase: "Canceled" }),
  entry("14", "ToolCall", { eventType: "activity", contentId: null, activityId: "completed-with-cancellation", name: "read_file", phase: "Completed" }),
  entry("15", "session_start", { eventType: "system_prompt", contentId: null, text: null, name: "Default", prompt: promptFacts }),
  entry("16", "changed", { eventType: "system_prompt", contentId: null, text: null, prompt: { ...promptFacts, changed: true, applied: false, added: 2, removed: 0 } }),
  entry("17", "changed", { eventType: "system_prompt", contentId: null, text: null, prompt: { ...promptFacts, changed: true, lossy: true, added: 2, removed: 0 } })];
const noop = () => {};
const theme = (dark: boolean) => showAppearance(document.documentElement, "bp6-dark", { theme: dark ? "dark" : "light", scheme: defaultColorScheme,
  palette: schemePalette(colorSchemeOf(defaultColorScheme), dark ? "dark" : "light") });

function Fixture() {
  const [isolation] = useState(() => {
    const never = () => { throw new Error("Appearance previews must not use inherited bookmark or system-prompt services"); };
    return { bookmarks: { subscribe: never, bookmarkOf: never } as unknown as TimelineBookmarks,
      prompt: { read: never } as unknown as SystemPromptReader, branches: [] as { offset: string; mode: string }[] };
  });
  const [reader] = useState(() => {
    let count = 0;
    return { read: async (): Promise<HistoryResponse> => { count++; return { status: "ok", entries, next: null, tailOmitted: false }; }, count: () => count };
  });
  const [settings, setSettings] = useState(true);
  const [histories, setHistories] = useState(1);
  const [locale, setLocale] = useState<Locale>("en");
  Object.assign(window, { emphasisFixture: { settings: (shown: boolean) => flushSync(() => setSettings(shown)),
    histories: (count: number) => flushSync(() => setHistories(count)), language: (next: Locale) => flushSync(() => setLocale(next)), theme, readCalls: reader.count,
    branches: isolation.branches } });
  return <ShellLanguageContext.Provider value={{ locale, choice: locale, setLanguage: noop }}><div className="ide-shell" style={{ display: "block", padding: 16 }}>
    {settings && <TimelineBookmarksContext.Provider value={isolation.bookmarks}><SystemPromptReaderContext.Provider value={isolation.prompt}>
      <div className="appearance-settings"><TimelineEmphasisSetting /></div>
    </SystemPromptReaderContext.Provider></TimelineBookmarksContext.Provider>}
    {Array.from({ length: histories }, (_, index) => <section key={index} data-history={index}>
      <History sessionId={`session-${index}`} live={null} read={reader.read} onNotesChange={noop} onSettled={noop} onBeforeOlder={noop} onAfterOlder={noop}
        onBranch={(offset, mode) => isolation.branches.push({ offset, mode })} />
    </section>)}
  </div></ShellLanguageContext.Provider>;
}

dismissDialogsOnOutsidePress(document);
theme(true);
// Every row is painted where it is seen: the activity lines of these timelines are open, without writing a preference.
const store = createTimelineEmphasisStore(() => localStorage.getItem(timelineEmphasisKey) ?? JSON.stringify({ focused: false }),
  value => localStorage.setItem(timelineEmphasisKey, value));
createRoot(document.getElementById("root")!).render(<StrictMode><TimelineEmphasisContext.Provider value={store}>
  <Fixture />
</TimelineEmphasisContext.Provider></StrictMode>);
