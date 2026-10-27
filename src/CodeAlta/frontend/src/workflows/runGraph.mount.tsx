// Disposable page: the workflow block of a run (the research sample, and a fan-out of 50 sessions), the timeline that holds
// it folded, a session of 30 runs, and the tab of a run, over a host the test answers. Driven by runGraph.browser.test.ts
// through `window.runFixture`.
import { StrictMode, createElement, type ReactNode } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import type { HistoryResponse, WorkflowRunDetailResponse, WorkflowRecordItem } from "#neoastra";
import { reconcileTimeline } from "../reconcileTimeline";
import { ScriptRunActionsContext, type ScriptRunActions } from "../scriptCard/SaveAsScript";
import { SessionLinksContext } from "../SessionReference";
import { scriptRunOf } from "../timeline";
import { groupEntryTypes } from "../timelineEmphasis/timelineEmphasis";
import { foldTimeline } from "../timelineActivity/activityFold";
import { TimelineActivity } from "../timelineActivity/TimelineActivity";
import { groupTimelineTools } from "../toolGroups";
import { OpenRunContext } from "./openRun";
import { RunTab } from "./RunTab";
import { WorkflowBlock } from "./WorkflowBlock";
import type { WorkflowsApi } from "./workflowsHub";

type Entry = HistoryResponse["entries"][number];
const root = createRoot(document.getElementById("root")!);
const start = Date.UTC(2026, 9, 27, 10, 0, 0);
const at = (seconds: number) => new Date(start + seconds * 1000).toISOString();
const item = (kind: string, seconds: number, fields: Partial<WorkflowRecordItem> & { extra?: object } = {}): WorkflowRecordItem =>
  ({ kind, activityId: fields.activityId ?? null, phase: fields.phase ?? null, text: fields.text ?? null, timestamp: at(seconds),
    details: JSON.stringify({ kind, ...fields.extra }) });

// A run of `fix-failing-tests`: find, 50 fixes (41 done, 6 running, 3 failed), then a report not reached yet.
function fanOut(): WorkflowRecordItem[] {
  const list: WorkflowRecordItem[] = [
    item("RunStarted", 0, { text: "Fix failing tests" }),
    item("StepsDeclared", 0, { text: "Fix failing tests", extra: { steps: [{ id: "find", title: "Find the failing tests" },
      { id: "fix", title: "Fix each test", after: ["find"], each: true }, { id: "report", title: "Report", after: ["fix"] }] } }),
    item("StepStarted", 1, { activityId: "find" }),
    item("StepEnded", 8, { activityId: "find", phase: "completed", extra: { durationMs: 7000, stepKey: "find#0" } }),
    item("StepStarted", 8, { activityId: "fix" }),
  ];
  for (let index = 0; index < 50; index++) {
    list.push(item("SessionStarted", 9 + index % 5, { activityId: `child-${index}`, text: `Fix test_${index}`, extra: { stepId: "fix", providerKey: "codex", modelId: "gpt-6.1-sol" } }));
    const status = index < 41 ? "completed" : index < 47 ? null : "failed";
    if (status) list.push(item("SessionEnded", 30 + index, { activityId: `child-${index}`, phase: status, extra: { durationMs: 20_000 + index * 100, inputTokens: 1200, outputTokens: 300 } }));
  }
  return list;
}

// The research sample with two angles: split, research each sub-question, write; it ended.
function research(): WorkflowRecordItem[] {
  return [
    item("RunStarted", 0, { text: "Research a question" }),
    item("StepsDeclared", 0, { extra: { steps: [{ id: "split", title: "Split the question" }, { id: "research", title: "Research each sub-question", each: true },
      { id: "write", title: "Write the answer" }] } }),
    item("StepStarted", 1, { activityId: "split" }),
    item("StepEnded", 6, { activityId: "split", phase: "completed", extra: { durationMs: 5000, stepKey: "split#0" } }),
    item("StepStarted", 6, { activityId: "research" }),
    item("SessionStarted", 7, { activityId: "child-a", text: "History of the question", extra: { stepId: "research", providerKey: "codex", modelId: "gpt-6.1-sol" } }),
    item("SessionStarted", 7, { activityId: "child-b", text: "Current practice", extra: { stepId: "research", providerKey: "claude-code", modelId: "claude-sonnet-5-5" } }),
    item("SessionEnded", 61, { activityId: "child-a", phase: "completed", extra: { durationMs: 54_000, inputTokens: 18_000, outputTokens: 2400 } }),
    item("SessionEnded", 75, { activityId: "child-b", phase: "completed", extra: { durationMs: 68_000, inputTokens: 21_000, outputTokens: 3100 } }),
    item("StepEnded", 75, { activityId: "research", phase: "completed", extra: { durationMs: 69_000, stepKey: "research#0" } }),
    item("StepStarted", 75, { activityId: "write" }),
    item("StepEnded", 92, { activityId: "write", phase: "completed", extra: { durationMs: 17_000, stepKey: "write#0" } }),
    item("RunEnded", 92, { phase: "completed" }),
  ];
}

const runOf = (records: WorkflowRecordItem[], runId: string) => scriptRunOf(records.map(record => ({ ...record, name: runId })), runId);

const state = { opened: [] as string[], runs: [] as string[], copied: [] as string[], reruns: [] as string[], stops: [] as string[], calls: [] as string[], reads: 0, gone: 0,
  mermaid: "flowchart LR\n  s0[\"Find the failing tests · completed\"]:::completed\n" };
Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText: async (text: string) => { state.copied.push(text); } } });
const detail = (): WorkflowRunDetailResponse => ({
  status: "ok", run: { id: "run-7f3c2a", sessionId: "session-1", title: "Fix failing tests", state: "running", startedAt: at(0), endedAt: null, callId: "call-1", calls: 60 },
  records: fanOut(), source: "export const meta = { name: \"fix-failing-tests\" };\nreturn 1;", result: null, mermaid: state.mermaid,
});
const api = { runDetail: async () => { state.reads++; return detail(); },
  stop: async ({ runId }: { runId: string }) => { state.stops.push(runId); return { status: "ok", message: null }; } } as unknown as WorkflowsApi;
const links = { title: (id: string) => id.startsWith("child-") ? `Fix ${id}` : id === "session-1" ? "Fix the tests" : null, open: (id: string) => { state.opened.push(id); } };
const actions: ScriptRunActions = {
  saveAsScript: async () => null,
  resume: async (runId, how) => { state.reruns.push(`${runId}:${how.from ?? (how.all ? "all" : "resume")}`); return null; },
  stop: async runId => { state.stops.push(runId); return null; },
};

function wrap(child: ReactNode, width: number | null, scroll = false) {
  return createElement(StrictMode, null, createElement(SessionLinksContext.Provider, { value: links },
    createElement(ScriptRunActionsContext.Provider, { value: actions },
      createElement(OpenRunContext.Provider, { value: run => { state.runs.push(run.runId); } },
        createElement("div", { className: "fixture-frame", style: { width: width === null ? "100%" : `${width}px`, height: "100%", overflowY: scroll ? "auto" : undefined } }, child)))));
}

// A session whose turn ran the research script: its prompt, then the exec call and the records of its run.
function timelineEntries(): Entry[] {
  const entry = (offset: string, overrides: Partial<Entry>): Entry => ({ offset, eventType: "contentCompleted", providerId: "provider", sessionId: "session", runId: "run",
    timestamp: at(Number(offset)), kind: "User", phase: null, contentId: `content-${offset}`, activityId: null, parentActivityId: null, interactionId: null, sourceSessionId: null,
    prompt: null, name: null, tool: null, files: null, images: null, text: "Research the question.", details: null, textTruncated: false, detailsTruncated: false, bodyOmitted: false, ...overrides });
  const code = JSON.stringify({ arguments: { code: '// @exec: {"title": "Research a question"}\nreturn await run("research");' } });
  const exec = (offset: string, phase: string) => entry(offset, { eventType: "activity", kind: "ToolCall", contentId: null, activityId: "call-1", name: "exec", phase, text: null, details: code });
  return [entry("1", {}), exec("2", "Started"),
    ...research().map((record, index) => entry(String(3 + index), { eventType: "workflow", kind: record.kind, contentId: null, activityId: record.activityId, phase: record.phase,
      text: record.text, details: record.details, parentActivityId: "call-1", name: "run-5b81e0", timestamp: record.timestamp })),
    exec("93", "Completed")];
}

const fixture = {
  state,
  theme(light: boolean) {
    document.documentElement.classList.toggle("bp6-dark", !light);
    document.body.classList.toggle("bp6-dark", !light);
    if (light) document.documentElement.dataset.theme = "light"; else delete document.documentElement.dataset.theme;
  },
  /** The block of a run, as the timeline shows it, at a width: the research sample, or the fan-out of 50 sessions. */
  renderBlock(width: number, which: "research" | "fanOut") {
    const run = which === "research" ? runOf(research(), "run-5b81e0") : runOf(fanOut(), "run-7f3c2a");
    flushSync(() => root.render(wrap(createElement(WorkflowBlock, { run, title: which === "research" ? "Research a question" : "Fix failing tests",
      rowKey: "row-1", onOpenCall: (rowKey: string) => { state.calls.push(rowKey); } }), width)));
  },
  /** The activity of a turn that ran the research script, folded or open. */
  renderTimeline(expanded: boolean) {
    const entries = timelineEntries();
    const groups = groupTimelineTools(reconcileTimeline(entries, null), entries);
    const types = groupEntryTypes(groups);
    const block = foldTimeline(groups, types).find(candidate => candidate.kind === "activity");
    if (block?.kind !== "activity") throw new Error("an activity");
    flushSync(() => root.render(wrap(createElement("div", { className: "messages" }, createElement(TimelineActivity, { sessionId: "session", groups: block.groups, entryTypes: types,
      expanded, live: false, onToggle: () => { }, revision: null, sources: new Map(), echoImages: new Map(), canInspect: () => true, onOpenSource: () => { },
      onOpenTool: (key: string) => { state.calls.push(key); } })), null)));
  },
  /** A session of many runs, in a pane that scrolls: a graph is drawn only near the view. */
  renderMany(count: number) {
    const run = runOf(research(), "run-5b81e0");
    flushSync(() => root.render(wrap(createElement("div", { className: "many-runs", style: { display: "grid", gap: "12px" } },
      Array.from({ length: count }, (_, index) => createElement(WorkflowBlock, { key: index, run, title: `Research ${index}`, rowKey: `row-${index}` }))), null, true)));
  },
  /** The tab of the run. */
  renderTab() {
    flushSync(() => root.render(wrap(createElement(RunTab, { api, epoch: "epoch", runId: "run-7f3c2a", visible: true, onActivate: () => { },
      onGone: () => { state.gone++; }, write: async (text: string) => { state.copied.push(text); } }), null)));
  },
  clear() { flushSync(() => root.render(null)); },
};
Object.assign(window, { runFixture: fixture });
