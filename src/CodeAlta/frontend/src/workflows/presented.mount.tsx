// Disposable page: what a script presented (`present()`) on the cards of its run: its block in the timeline as the parts
// come, the card of its result (a run of an agent, a run of the user), and the tab of the run, over a host the test
// answers. Driven by presented.browser.test.ts through `window.presentedFixture`.
import { StrictMode, createElement, type ReactNode } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import type { HistoryResponse, WorkflowRunDetailResponse, WorkflowRecordItem } from "#neoastra";
import { buildTimelineItems } from "../timeline";
import { TimelineMessage } from "../TimelineMessage";
import { MessageReaderContext } from "../wholeMessage";
import { OpenRunContext } from "./openRun";
import { RunTab } from "./RunTab";
import { presentedHeading } from "./scriptResult";
import { WorkflowBlock } from "./WorkflowBlock";
import type { WorkflowsApi } from "./workflowsHub";

type Entry = HistoryResponse["entries"][number];
const root = createRoot(document.getElementById("root")!);
const at = (seconds: number) => new Date(Date.UTC(2026, 9, 29, 10, 0, seconds)).toISOString();
const entry = (offset: string, overrides: Partial<Entry>): Entry => ({ offset, eventType: "workflow", providerId: "codealta", sessionId: "session", runId: null,
  timestamp: at(Number(offset)), kind: null, phase: null, contentId: null, activityId: null, parentActivityId: "call-1", interactionId: null, sourceSessionId: null,
  prompt: null, name: "run-1", tool: null, files: null, images: null, text: null, details: null, textTruncated: false, detailsTruncated: false, bodyOmitted: false, ...overrides });

const findings = "## Findings\n\n| File | Issue |\n| --- | --- |\n| `src/Parser.cs` | A quoted key loses its quotes. |\n| `src/Lexer.cs` | A tab ends a token. |\n\n"
  + "```cs\nvar key = reader.ReadKey(); // not unquoted\n```";
// The whole of the second part: the page gave its start only, and the card reads the rest.
const whole = findings + "\n\n" + Array.from({ length: 12 }, (_, index) => `- note ${index + 1}`).join("\n");

const records = (forUser: boolean): Entry[] => [
  entry("2", { kind: "RunStarted", text: "Review PR 231", details: JSON.stringify(forUser ? { forUser: true } : {}) }),
  entry("3", { kind: "Presented", text: "## Reading\nRead **3** files of the pull request." }),
  entry("4", { kind: "StepStarted", activityId: "review" }),
  entry("5", { kind: "Presented", activityId: "review", text: findings, textTruncated: true }),
  entry("6", { kind: "StepEnded", activityId: "review", phase: "completed", details: JSON.stringify({ durationMs: 3000 }) }),
  entry("7", { kind: "RunEnded", phase: "completed", details: JSON.stringify({ durationMs: 4000, calls: 2 }) }),
];
const value = '{"issues":2,"files":["src/Parser.cs","src/Lexer.cs"],"source":"public sealed class Parser\\n{\\n    // <summary> *not* Markdown\\n}"}';
const resultText = (forUser: boolean) => `Script run-1 (Review PR 231) ended:\nScript completed · 4.0 s · 2 calls\nProfile: step review 3.0 s · script 2 ms\n`
  // The parts are in a fence longer than the fences they hold, as the host writes them.
  + (forUser ? "" : `${presentedHeading}\n\`\`\`\`markdown\n## Reading\nRead **3** files of the pull request.\n\n${findings}\n\`\`\`\`\n`)
  + (forUser ? "Result:\n## Review of PR 231\n\n- **2** issues\n" : `Result:\n\`\`\`json\n${value}\n\`\`\`\n`);
const prompt = (forUser: boolean) => entry("9", { eventType: "contentCompleted", kind: "User", providerId: "provider", runId: "turn-2", parentActivityId: null, name: null,
  contentId: "prompt-9", text: resultText(forUser), provenance: "workflow" } as Partial<Entry>);

const state = { runs: [] as string[], reads: 0 };
const reader = async () => { state.reads++; return { status: "ok" as const, text: whole, complete: true }; };
const detail = (forUser: boolean): WorkflowRunDetailResponse => ({
  status: "ok", run: { id: "run-1", sessionId: "session", title: "Review PR 231", state: "completed", startedAt: at(2), endedAt: at(7), callId: "call-1", calls: 2 },
  records: records(forUser).map((record): WorkflowRecordItem => ({ kind: record.kind!, activityId: record.activityId, phase: record.phase,
    text: record.kind === "Presented" && record.activityId === "review" ? whole : record.text, details: record.details ?? JSON.stringify({}), timestamp: record.timestamp })),
  source: "present(\"## Reading\");\nreturn data;", result: resultText(forUser).slice(resultText(forUser).indexOf("\n") + 1), mermaid: null,
});
let tabForUser = false;
const api = { runDetail: async () => detail(tabForUser), stop: async () => ({ status: "ok", message: null }) } as unknown as WorkflowsApi;

function wrap(child: ReactNode, width: number | null) {
  return createElement(StrictMode, null, createElement(MessageReaderContext.Provider, { value: reader },
    createElement(OpenRunContext.Provider, { value: run => { state.runs.push(run.runId); } },
      createElement("div", { className: "fixture-frame", style: { width: width === null ? "100%" : `${width}px`, height: "100%", overflowY: "auto" } },
        createElement("div", { className: "messages" }, child)))));
}

const fixture = {
  state,
  theme(light: boolean) {
    document.documentElement.classList.toggle("bp6-dark", !light);
    document.body.classList.toggle("bp6-dark", !light);
    if (light) document.documentElement.dataset.theme = "light"; else delete document.documentElement.dataset.theme;
  },
  /** The block of the run as the timeline shows it, with its first `count` records: the parts come as the run goes. */
  renderBlock(width: number, count: number) {
    const entries = [entry("1", { eventType: "activity", kind: "ToolCall", providerId: "provider", runId: "turn-1", activityId: "call-1", parentActivityId: null, name: "exec",
      phase: "Started", details: JSON.stringify({ arguments: { code: '// @exec: {"title": "Review PR 231"}\nreturn data;' } }) }), ...records(false).slice(0, count)];
    const run = buildTimelineItems(entries)[0].scriptRun!;
    flushSync(() => root.render(wrap(createElement(WorkflowBlock, { run, title: "Review PR 231", rowKey: "row-1" }), width)));
  },
  /** The card of the result of the run, which went on in the background: a run of an agent, or one the user ran from the library. */
  renderResult(width: number, forUser: boolean) {
    const items = buildTimelineItems([...records(forUser), prompt(forUser)]);
    flushSync(() => root.render(wrap(items.map(item => createElement(TimelineMessage, { key: item.key, item })), width)));
  },
  /** The tab of the run. */
  renderTab(forUser: boolean) {
    tabForUser = forUser;
    flushSync(() => root.render(wrap(createElement(RunTab, { api, epoch: "epoch", runId: "run-1", visible: true, onActivate: () => { }, onGone: () => { } }), null)));
  },
  clear() { flushSync(() => root.render(null)); },
};
Object.assign(window, { presentedFixture: fixture });
