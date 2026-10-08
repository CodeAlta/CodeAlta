// Test-owned mount of the production composer against a scripted backend: no host and no provider.
// The fixture plays the host's part: it takes or refuses a Send, starts and ends the run, and takes steering.
import { createElement } from "react";
import { createRoot } from "react-dom/client";
import { OwnedSessionPanel } from "./OwnedSessionPanel";
import { createMutationCapability, createOwnedSubmissions } from "./sessionOperations";
import { createSteeringSubmissions } from "./sessionSteering";
import { createCompactionSubmissions } from "./sessionCompaction";
import { createAbortRunSubmissions } from "./sessionAbortRun";
import { createQueueSubmissions } from "./sessionQueue";
import { createRuntimeStateReader } from "./runtimeState";
import { createDraftIndicators } from "./promptDraft";
import { createNextSendSelectionStore } from "./nextSendSelection";
import type { SessionAdmission, SessionRuntimeStateResponse, SessionSendRequest, SessionSteerRequest } from "#neoastra";

const unavailable = async (): Promise<never> => { throw new Error("The composer fixture does not offer this operation"); };
const epoch = "fixture-epoch";
const sessionId = "fixture-session";
const runtime = "11111111-1111-4111-8111-111111111111";
let run: string | null = null;
let runs = 0;
let operations = 0;
let sendMode: "accept" | "busy" | "refuse" = "accept";
let steerMode: "accept" | "refuse" | "expired" = "accept";
const operationId = () => `22222222-2222-4222-8222-${String(++operations).padStart(12, "0")}`;

const queue = createQueueSubmissions(unavailable, unavailable);
const submissions = createOwnedSubmissions(async (request): Promise<SessionAdmission> => {
  fixture.sendCalls.push(request);
  if (sendMode !== "accept") return { status: sendMode === "busy" ? "busy" : "invalid_request", epoch: request.expectedEpoch, receipt: null };
  // An accepted Send is a run: the session works until the test ends it.
  run = `run-${++runs}`;
  return { status: "accepted", epoch: request.expectedEpoch, receipt: { kind: "Send", clientRequestId: request.clientRequestId,
    sessionId: request.sessionId, operationId: operationId(), targetOperationId: null, state: "pending", outcome: null, code: null,
    runId: null, queueInsertion: null } };
}, unavailable);
const steering = createSteeringSubmissions(async (request): Promise<SessionAdmission> => {
  fixture.steerCalls.push(request);
  if (steerMode !== "accept") return { status: steerMode === "refuse" ? "conflict" : "expired", epoch: request.expectedEpoch, receipt: null };
  return { status: "accepted", epoch: request.expectedEpoch, receipt: { kind: "Steer", clientRequestId: request.clientRequestId,
    sessionId: request.sessionId, operationId: operationId(), targetOperationId: null, state: "terminal", outcome: "Completed", code: null,
    runId: request.expectedRunId, queueInsertion: null } };
});

const fixture = {
  sendCalls: [] as SessionSendRequest[],
  steerCalls: [] as SessionSteerRequest[],
  sendMode(value: typeof sendMode) { sendMode = value; },
  steerMode(value: typeof steerMode) { steerMode = value; },
  /** The run a session already has when the composer opens on it. */
  startRun() { run = `run-${++runs}`; return run; },
  finishRun() { run = null; },
  run: () => run,
  /** What the timeline would echo for the prompts sent from this window. */
  echoes: () => submissions.outgoing(epoch, sessionId).map(echo => `${echo.state}:${echo.text}`),
  rows: () => queue.composer.list(epoch, sessionId).map(item => `${item.kind}:${item.state}:${item.count}:${item.text}`),
};
Object.assign(window, { fixture });

const props = {
  epoch, sessionId, submissions, steering, queue,
  compaction: createCompactionSubmissions(unavailable), abortRuns: createAbortRunSubmissions(unavailable),
  capability: createMutationCapability(epoch), draftIndicators: createDraftIndicators(), permissionReviewer: null,
  selections: createNextSendSelectionStore(key => localStorage.getItem(key), (key, value) => localStorage.setItem(key, value)),
  runtimeReader: createRuntimeStateReader(async request => ({ status: "ok", hostEpoch: request.expectedHostEpoch, sessionId: request.sessionId,
    entry: { attachmentGeneration: "12", activeRunId: run, isRetiring: false, isTerminated: false, backgroundTasks: [], queueDrainInProgress: false,
      providerId: "fixture-provider", providerKey: "fixture-provider", modelId: null, reasoningEffort: null, agentPromptId: null,
      pendingAgentPromptId: null, activity: null },
    runtimeInstanceId: runtime, coordinatorTransitionInProgress: false } satisfies SessionRuntimeStateResponse)),
};
createRoot(document.getElementById("app")!).render(createElement("div", { id: "workspace-shell" },
  createElement("div", { className: "session-workspace" },
    createElement("div", { className: "composer-region" }, createElement(OwnedSessionPanel, props)))));
