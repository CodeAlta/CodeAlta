// Test-owned mount of the production composer against isolated, non-mutating session operations.
import { createElement, createRef } from "react";
import { createRoot } from "react-dom/client";
import { OwnedSessionPanel } from "./OwnedSessionPanel";
import { captureSubmission, createMutationCapability, createOwnedSubmissions } from "./sessionOperations";
import { createSteeringSubmissions } from "./sessionSteering";
import { createCompactionSubmissions } from "./sessionCompaction";
import { createAbortRunSubmissions } from "./sessionAbortRun";
import { createQueueSubmissions } from "./sessionQueue";
import { createRuntimeStateReader } from "./runtimeState";
import { createDraftIndicators } from "./promptDraft";
import { dispatchWorkspaceShortcut, type ShortcutSession, type WorkspaceShortcutState } from "./workspaceShortcutDispatch";
import { activateContextShortcut } from "./contextShortcut";
import { createNextSendSelectionStore } from "./nextSendSelection";
import type { SessionAbortRunRequest, SessionCompactRequest, SessionSteerRequest, SessionQueueRequest, SessionAdmission, SessionRuntimeStateResponse } from "#neoastra";

const unavailable = async (): Promise<never> => { throw new Error("Fixture must not submit operations"); };
const epoch = "fixture-epoch";
const sessionId = "fixture-session";
let observedRun: string | null = null;
let attachment = 12;
const originalRuntime = "11111111-1111-4111-8111-111111111111";
let runtime = originalRuntime;
let hasEntry = false;
let retiring = false;
let transitioning = false;
let draining = false;
let currentSession = sessionId;
let shortcutSelection: ShortcutSession | null = { epoch, sessionId, projectId: null };
let workspaceActive = true;
const compactTrigger = createRef<HTMLButtonElement>();
let abortMode: "hold" | "fail" | "uncertain" = "hold";
let settleAbort: ((value: SessionAdmission) => void) | undefined;
let compactMode: "hold" | "busy" | "uncertain" = "hold";
let settleCompact: ((value: SessionAdmission) => void) | undefined;
let steerMode: "hold" | "uncertain" = "hold";
let settleSteer: ((value: SessionAdmission) => void) | undefined;
let queueMode: "hold" | "uncertain" = "hold";
let settleQueue: ((value: SessionAdmission) => void) | undefined;
const counts = { refreshes: 0, catalogOpens: 0, abortCalls: [] as SessionAbortRunRequest[], compactCalls: [] as SessionCompactRequest[],
  steerCalls: [] as SessionSteerRequest[],
  queueCalls: [] as SessionQueueRequest[],
  queueMode(value: typeof queueMode) { queueMode = value; },
  queuePending(id = currentSession) { return props.queue.pending(id); },
  settleQueue(reply: "matching" | "mismatched" | "malformed" = "matching") {
    const request = counts.queueCalls.at(-1)!;
    settleQueue?.({ status: "accepted", epoch: request.expectedEpoch,
      receipt: { kind: "Queue", clientRequestId: reply === "mismatched" ? "wrong-key" : request.clientRequestId,
        sessionId: request.sessionId, operationId: "33333333-3333-4333-8333-333333333333", targetOperationId: null,
        state: "pending", outcome: null, code: null, runId: null,
        queueInsertion: reply === "malformed" ? null : { state: "pending", accepted: null, code: null } } });
    settleQueue = undefined;
  },
  steerMode(value: typeof steerMode) { steerMode = value; },
  settleSteer() {
    const request = counts.steerCalls.at(-1)!;
    settleSteer?.({ status: "accepted", epoch: request.expectedEpoch,
      receipt: { kind: "Steer", clientRequestId: request.clientRequestId, sessionId: request.sessionId,
        operationId: "fixture-steer", targetOperationId: null, state: "completed", outcome: "Completed", code: null,
        runId: request.expectedRunId, queueInsertion: null } });
    settleSteer = undefined;
  },
  observe(run: string | null, generation = 12, instance = originalRuntime) {
    observedRun = run; attachment = generation; runtime = instance; hasEntry = true;
  },
  flags(value: { retiring?: boolean; transitioning?: boolean; draining?: boolean }) {
    retiring = !!value.retiring; transitioning = !!value.transitioning; draining = !!value.draining;
  },
  compactMode(value: typeof compactMode) { compactMode = value; },
  settleCompact() {
    const request = counts.compactCalls.at(-1)!;
    settleCompact?.({ status: "accepted", epoch: request.expectedEpoch,
      receipt: { kind: "Compact", clientRequestId: request.clientRequestId, sessionId: request.sessionId,
        operationId: "fixture-compact", targetOperationId: null, state: "completed", outcome: "Completed", code: null,
        runId: null, queueInsertion: null } });
    settleCompact = undefined;
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
  switchSession(id: string) { currentSession = id; shortcutSelection = { epoch, sessionId: id, projectId: null }; root.render(panel(id)); },
  shortcutSelection(value: ShortcutSession | null) { shortcutSelection = value; },
  workspaceActive(value: boolean) { workspaceActive = value; },
  async retainSend(text: string) {
    const request = captureSubmission(epoch, sessionId, text, "fixture-send")!;
    await props.submissions.submit(request, new AbortController().signal, props.capability, () => {});
    root.render(panel(sessionId));
  },
};
Object.assign(window, { fixture: counts });
const shortcutState: WorkspaceShortcutState = { chordPending: false, sessionInfoPrefix: null, reminderPrefix: null };
window.addEventListener("keydown", event => {
  const selected = shortcutSelection;
  dispatchWorkspaceShortcut(event, shortcutState, {
    workspaceActive, workspaceShell: document.getElementById("workspace-shell"),
    modalOpen: !!document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]'),
    selectedProjectFocused: false, infoTrigger: null, reminderTrigger: null,
    compactTrigger: compactTrigger.current,
    infoSelection: selected && selected.sessionId === currentSession ? { sessionId: currentSession, projectId: null } : null,
    selection: selected, run: action => {
      if (action === "context") activateContextShortcut(document.getElementById("workspace-shell"));
      if (action === "compact") compactTrigger.current?.click();
    },
  });
});
const props = {
  epoch, sessionId, submissions: createOwnedSubmissions(unavailable, unavailable),
  steering: createSteeringSubmissions(async request => {
    counts.steerCalls.push(request);
    if (steerMode === "uncertain") throw new Error("fixture steering wait timed out");
    return new Promise(resolve => { settleSteer = resolve; });
  }), compaction: createCompactionSubmissions(async request => {
    counts.compactCalls.push(request);
    if (compactMode === "busy") return { status: "busy", epoch: request.expectedEpoch, receipt: null };
    if (compactMode === "uncertain") throw new Error("fixture compaction wait timed out");
    return new Promise(resolve => { settleCompact = resolve; });
  }),
  abortRuns: createAbortRunSubmissions(async request => {
    counts.abortCalls.push(request);
    if (abortMode === "fail") return { status: "busy", epoch: request.expectedEpoch, receipt: null };
    if (abortMode === "uncertain") throw new Error("fixture wait timed out");
    return new Promise(resolve => { settleAbort = resolve; });
  }), queue: createQueueSubmissions(async request => {
    counts.queueCalls.push(request);
    if (queueMode === "uncertain") throw new Error("fixture queue waiter lost");
    return new Promise(resolve => { settleQueue = resolve; });
  }, unavailable),
  capability: createMutationCapability(epoch), draftIndicators: createDraftIndicators(), permissionReviewer: null,
  selections: createNextSendSelectionStore(key => localStorage.getItem(key), (key, value) => localStorage.setItem(key, value)),
  runtimeReader: createRuntimeStateReader(async request => {
    counts.refreshes++;
    return { status: "ok", hostEpoch: request.expectedHostEpoch, sessionId: request.sessionId,
      entry: hasEntry ? { attachmentGeneration: String(attachment), activeRunId: observedRun,
        isRetiring: retiring, isTerminated: false, queueDrainInProgress: draining,
        providerId: "fixture-provider", providerKey: "fixture-provider", modelId: null,
        reasoningEffort: null, agentPromptId: null, pendingAgentPromptId: null } : null,
      runtimeInstanceId: runtime, coordinatorTransitionInProgress: transitioning } satisfies SessionRuntimeStateResponse;
  }),
};
const root = createRoot(document.getElementById("app")!);
const panel = (selectedSession: string) => createElement("div", { id: "workspace-shell" },
  createElement("div", { className: "session-workspace" },
    createElement(OwnedSessionPanel, { ...props, sessionId: selectedSession, key: selectedSession, compactTrigger, onOpenReminders: () => { counts.catalogOpens++; } }),
    createElement("div", { className: "project-rename" }, createElement("label", null, "Project name", createElement("input", { defaultValue: "A project" }))),
    createElement("div", { className: "session-rename" }, createElement("label", null, "Session title", createElement("input", { defaultValue: "A session", disabled: true })))));
root.render(panel(sessionId));
