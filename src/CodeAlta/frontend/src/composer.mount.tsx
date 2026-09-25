// Test-owned mount of the production composer against isolated, non-mutating session operations.
import { createElement } from "react";
import { createRoot } from "react-dom/client";
import { OwnedSessionPanel } from "./OwnedSessionPanel";
import { captureSubmission, createMutationCapability, createOwnedSubmissions } from "./sessionOperations";
import { createSteeringSubmissions } from "./sessionSteering";
import { createCompactionSubmissions } from "./sessionCompaction";
import { createAbortRunSubmissions } from "./sessionAbortRun";
import { createQueueSubmissions } from "./sessionQueue";
import { createRuntimeStateReader } from "./runtimeState";
import { createDraftIndicators } from "./promptDraft";
import { resolveShortcut } from "./shortcuts";
import { activateContextShortcut } from "./contextShortcut";
import { createNextSendSelectionStore } from "./nextSendSelection";
import type { SessionAbortRunRequest, SessionAdmission, SessionRuntimeStateResponse } from "#neoastra";

const unavailable = async (): Promise<never> => { throw new Error("Fixture must not submit operations"); };
const epoch = "fixture-epoch";
const sessionId = "fixture-session";
let observedRun: string | null = null;
let attachment = 12;
let runtime = "fixture-runtime";
let abortMode: "hold" | "fail" | "uncertain" = "hold";
let settleAbort: ((value: SessionAdmission) => void) | undefined;
const counts = { refreshes: 0, catalogOpens: 0, abortCalls: [] as SessionAbortRunRequest[],
  observe(run: string | null, generation = 12, instance = "fixture-runtime") {
    observedRun = run; attachment = generation; runtime = instance;
  },
  mode(value: typeof abortMode) { abortMode = value; },
  settle() {
    const request = counts.abortCalls.at(-1)!;
    settleAbort?.({ status: "accepted", epoch: request.expectedEpoch,
      receipt: { kind: "AbortRun", clientRequestId: request.clientRequestId, sessionId: request.sessionId,
        operationId: "fixture-abort", targetOperationId: null, state: "completed", outcome: "Completed", code: null,
        runId: request.expectedRunId, queueInsertion: null } });
    settleAbort = undefined;
  },
  switchSession(id: string) { root.render(panel(id)); },
  async retainSend(text: string) {
    const request = captureSubmission(epoch, sessionId, text, "fixture-send")!;
    await props.submissions.submit(request, new AbortController().signal, props.capability, () => {});
    root.render(panel(sessionId));
  },
};
Object.assign(window, { fixture: counts });
let chordPending = false;
window.addEventListener("keydown", event => {
  const target = event.target as HTMLElement | null;
  if (target?.closest("dialog[open]")) { chordPending = false; return; }
  const editing = target?.matches("input, textarea, select, [contenteditable='true']") === true;
  const resolved = resolveShortcut(event, chordPending, editing);
  chordPending = resolved.chordPending;
  if (!resolved.handled) return;
  event.preventDefault();
  if (resolved.action === "context") activateContextShortcut(document.getElementById("workspace-shell"));
});
const props = {
  epoch, sessionId, submissions: createOwnedSubmissions(unavailable, unavailable),
  steering: createSteeringSubmissions(unavailable), compaction: createCompactionSubmissions(unavailable),
  abortRuns: createAbortRunSubmissions(async request => {
    counts.abortCalls.push(request);
    if (abortMode === "fail") return { status: "busy", epoch: request.expectedEpoch, receipt: null };
    if (abortMode === "uncertain") throw new Error("fixture wait timed out");
    return new Promise(resolve => { settleAbort = resolve; });
  }), queue: createQueueSubmissions(unavailable, unavailable),
  capability: createMutationCapability(epoch), draftIndicators: createDraftIndicators(), permissionReviewer: null,
  selections: createNextSendSelectionStore(key => localStorage.getItem(key), (key, value) => localStorage.setItem(key, value)),
  runtimeReader: createRuntimeStateReader(async request => {
    counts.refreshes++;
    return { status: "ok", hostEpoch: request.expectedHostEpoch, sessionId: request.sessionId,
      entry: observedRun ? { attachmentGeneration: `gen-${attachment}`, activeRunId: observedRun,
        isRetiring: false, isTerminated: false, queueDrainInProgress: false,
        providerId: "fixture-provider", providerKey: "fixture-provider", modelId: null,
        reasoningEffort: null, agentPromptId: null, pendingAgentPromptId: null } : null,
      runtimeInstanceId: runtime, coordinatorTransitionInProgress: false } satisfies SessionRuntimeStateResponse;
  }),
};
const root = createRoot(document.getElementById("app")!);
const panel = (selectedSession: string) => createElement("div", { id: "workspace-shell" },
  createElement("div", { className: "session-workspace" },
    createElement(OwnedSessionPanel, { ...props, sessionId: selectedSession, key: selectedSession, onOpenReminders: () => { counts.catalogOpens++; } }),
    createElement("div", { className: "project-rename" }, createElement("label", null, "Project name", createElement("input", { defaultValue: "A project" }))),
    createElement("div", { className: "session-rename" }, createElement("label", null, "Session title", createElement("input", { defaultValue: "A session", disabled: true })))));
root.render(panel(sessionId));
