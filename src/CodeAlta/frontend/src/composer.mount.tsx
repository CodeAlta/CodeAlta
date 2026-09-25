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
import type { SessionAbortRunRequest, SessionCompactRequest, SessionAdmission, SessionRuntimeStateResponse } from "#neoastra";

const unavailable = async (): Promise<never> => { throw new Error("Fixture must not submit operations"); };
const epoch = "fixture-epoch";
const sessionId = "fixture-session";
let observedRun: string | null = null;
let attachment = 12;
let runtime = "fixture-runtime";
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
const counts = { refreshes: 0, catalogOpens: 0, abortCalls: [] as SessionAbortRunRequest[], compactCalls: [] as SessionCompactRequest[],
  observe(run: string | null, generation = 12, instance = "fixture-runtime") {
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
  steering: createSteeringSubmissions(unavailable), compaction: createCompactionSubmissions(async request => {
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
  }), queue: createQueueSubmissions(unavailable, unavailable),
  capability: createMutationCapability(epoch), draftIndicators: createDraftIndicators(), permissionReviewer: null,
  selections: createNextSendSelectionStore(key => localStorage.getItem(key), (key, value) => localStorage.setItem(key, value)),
  runtimeReader: createRuntimeStateReader(async request => {
    counts.refreshes++;
    return { status: "ok", hostEpoch: request.expectedHostEpoch, sessionId: request.sessionId,
      entry: hasEntry ? { attachmentGeneration: `gen-${attachment}`, activeRunId: observedRun,
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
