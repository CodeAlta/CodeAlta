// Disposable page: the card of a run whose script declares its steps (a fan-out of 50 sessions), and the tab of that run,
// over a host the test answers. Driven by runGraph.browser.test.ts through `window.runFixture`.
import { StrictMode, createElement } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import type { WorkflowRunDetailResponse, WorkflowRecordItem } from "#neoastra";
import { ScriptRunView } from "../scriptCard/ScriptRunView";
import { SessionLinksContext } from "../SessionReference";
import { scriptRunOf } from "../timeline";
import { OpenRunContext } from "./openRun";
import { RunTab } from "./RunTab";
import type { WorkflowsApi } from "./workflowsHub";

const root = createRoot(document.getElementById("root")!);
const start = Date.UTC(2026, 9, 27, 10, 0, 0);
const at = (seconds: number) => new Date(start + seconds * 1000).toISOString();
const item = (kind: string, seconds: number, fields: Partial<WorkflowRecordItem> & { extra?: object } = {}): WorkflowRecordItem =>
  ({ kind, activityId: fields.activityId ?? null, phase: fields.phase ?? null, text: fields.text ?? null, timestamp: at(seconds),
    details: JSON.stringify({ kind, ...fields.extra }) });

// A run of `fix-failing-tests`: find, 50 fixes (41 done, 6 running, 3 failed), then a report not reached yet.
function records(): WorkflowRecordItem[] {
  const list: WorkflowRecordItem[] = [
    item("RunStarted", 0, { text: "Fix failing tests" }),
    item("StepsDeclared", 0, { text: "Fix failing tests", extra: { steps: [{ id: "find", title: "Find the failing tests" },
      { id: "fix", title: "Fix each test", after: ["find"], each: true }, { id: "report", title: "Report", after: ["fix"] }] } }),
    item("StepStarted", 1, { activityId: "find" }),
    item("StepEnded", 8, { activityId: "find", phase: "completed", extra: { durationMs: 7000, stepKey: "find#0" } }),
    item("StepStarted", 8, { activityId: "fix" }),
  ];
  for (let index = 0; index < 50; index++) {
    list.push(item("SessionStarted", 9 + index % 5, { activityId: `child-${index}`, text: `Fix test_${index}`, extra: { stepId: "fix" } }));
    const status = index < 41 ? "completed" : index < 47 ? null : "failed";
    if (status) list.push(item("SessionEnded", 30 + index, { activityId: `child-${index}`, phase: status, extra: { durationMs: 20_000 + index * 100, inputTokens: 1200, outputTokens: 300 } }));
  }
  return list;
}

const state = { opened: [] as string[], runs: [] as string[], copied: [] as string[], reads: 0, gone: 0, mermaid: "flowchart LR\n  s0[\"Find the failing tests · completed\"]:::completed\n" };
const detail = (): WorkflowRunDetailResponse => ({
  status: "ok", run: { id: "run-7f3c2a", sessionId: "session-1", title: "Fix failing tests", state: "running", startedAt: at(0), endedAt: null, callId: "call-1", calls: 60 },
  records: records(), source: "export const meta = { name: \"fix-failing-tests\" };\nreturn 1;", result: null, mermaid: state.mermaid,
});
const api = {
  runDetail: async () => { state.reads++; return detail(); },
} as unknown as WorkflowsApi;
const links = { title: (id: string) => id.startsWith("child-") ? `Fix ${id}` : id === "session-1" ? "Fix the tests" : null, open: (id: string) => { state.opened.push(id); } };

function wrap(child: ReturnType<typeof createElement>, width: number | null) {
  return createElement(StrictMode, null, createElement(SessionLinksContext.Provider, { value: links },
    createElement(OpenRunContext.Provider, { value: run => { state.runs.push(run.runId); } },
      createElement("div", { className: "fixture-frame", style: { width: width === null ? "100%" : `${width}px`, height: "100%" } }, child))));
}

const fixture = {
  state,
  theme(light: boolean) {
    document.documentElement.classList.toggle("bp6-dark", !light);
    document.body.classList.toggle("bp6-dark", !light);
    if (light) document.documentElement.dataset.theme = "light"; else delete document.documentElement.dataset.theme;
  },
  /** The card of the run, as the timeline of its session shows it, at a width. */
  renderCard(width: number) {
    const run = scriptRunOf(records().map(record => ({ ...record, name: "run-7f3c2a" })), "run-7f3c2a");
    flushSync(() => root.render(wrap(createElement(ScriptRunView, { run, title: "Fix failing tests" }), width)));
  },
  /** The tab of the run. */
  renderTab() {
    flushSync(() => root.render(wrap(createElement(RunTab, { api, epoch: "epoch", runId: "run-7f3c2a", visible: true, onActivate: () => { },
      onGone: () => { state.gone++; }, write: async (text: string) => { state.copied.push(text); } }), null)));
  },
  clear() { flushSync(() => root.render(null)); },
};
Object.assign(window, { runFixture: fixture });
