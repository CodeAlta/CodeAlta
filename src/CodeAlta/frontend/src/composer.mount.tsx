// Test-owned mount of the production composer against isolated, non-mutating session operations.
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

const unavailable = async (): Promise<never> => { throw new Error("Fixture must not submit operations"); };
const epoch = "fixture-epoch";
const sessionId = "fixture-session";
const props = {
  epoch, sessionId, submissions: createOwnedSubmissions(unavailable, unavailable),
  steering: createSteeringSubmissions(unavailable), compaction: createCompactionSubmissions(unavailable),
  abortRuns: createAbortRunSubmissions(unavailable), queue: createQueueSubmissions(unavailable, unavailable),
  capability: createMutationCapability(epoch), draftIndicators: createDraftIndicators(), permissionReviewer: null,
  runtimeReader: createRuntimeStateReader(async () => ({ status: "ok", hostEpoch: epoch, sessionId,
    entry: null, runtimeInstanceId: "fixture-runtime", coordinatorTransitionInProgress: false })),
};
createRoot(document.getElementById("app")!).render(createElement("div", { className: "session-workspace" },
  createElement(OwnedSessionPanel, props),
  createElement("div", { className: "project-rename" }, createElement("label", null, "Project name", createElement("input", { defaultValue: "A project" }))),
  createElement("div", { className: "session-rename" }, createElement("label", null, "Session title", createElement("input", { defaultValue: "A session", disabled: true })))));
