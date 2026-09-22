import assert from "node:assert/strict";
import test from "node:test";
import { promptEditorHeight, showAskDetails, showContextAction, showLiveDisplay } from "./workspacePresentation";
import type { DisplayState } from "./sessionDisplay";
import type { AskPage } from "./sessionAsks";
import type { SessionDisplayView, SessionRuntimeStateResponse } from "#neoastra";
import { captureSteering } from "./sessionSteering";
import { captureQueue } from "./sessionQueue";

const empty: DisplayState = { kind: "connected", hostEpoch: "host", sessionId: "session", code: null, cleanupBlocked: false, snapshot: null };
const noAsks: AskPage = { head: null, latest: null, hasMore: false };

test("empty/loading live windows stay out of the timeline, but faults and coverage signals remain", () => {
  assert.equal(showLiveDisplay(null), false);
  assert.equal(showLiveDisplay(empty), false);
  assert.equal(showLiveDisplay({ ...empty, kind: "loading" }), false);
  assert.equal(showLiveDisplay({ ...empty, kind: "error" }), true);
  assert.equal(showLiveDisplay({ ...empty, kind: "error", code: "stale_epoch" }), true);
  assert.equal(showLiveDisplay({ ...empty, kind: "error" }), true);
  assert.equal(showLiveDisplay({ ...empty, cleanupBlocked: true }), true);
  assert.equal(showLiveDisplay({ ...empty, kind: "closed" }), true);
  const snapshot = { hasGap: false, evictedSessions: "0", omittedSessionEvents: "0", session: null } as NonNullable<DisplayState["snapshot"]>;
  assert.equal(showLiveDisplay({ ...empty, snapshot }), false);
  assert.equal(showLiveDisplay({ ...empty, snapshot: { ...snapshot, hasGap: true } }), true);
  assert.equal(showLiveDisplay({ ...empty, snapshot: { ...snapshot, evictedSessions: "1" } }), true);
  const session: SessionDisplayView = { sessionId: "session", revision: "0", configuration: null, queuedPromptCount: null,
    text: [], toolActivities: [], lifecycle: null, statusKind: null, statusMessage: null,
    metadataTruncated: false, transportTruncated: false, evictedTextItems: "0", evictedToolActivities: "0", unsupportedEvents: "0" };
  assert.equal(showLiveDisplay({ ...empty, snapshot: { ...snapshot, session } }), false);
  assert.equal(showLiveDisplay({ ...empty, snapshot: { ...snapshot, session: { ...session, queuedPromptCount: 0 } } }), false);
  assert.equal(showLiveDisplay({ ...empty, snapshot: { ...snapshot, session: { ...session, queuedPromptCount: 2 } } }), true);
  assert.equal(showLiveDisplay({ ...empty, snapshot: { ...snapshot, session: { ...session, queuedPromptCount: 1 } } }), true);
  assert.equal(showLiveDisplay({ ...empty, snapshot: { ...snapshot, session: { ...session, text: [{ runId: "run", contentId: "text", kind: "Assistant", text: "live", isComplete: false, isTruncated: false, startedWithDelta: false }] } } }), true);
  assert.equal(showLiveDisplay({ ...empty, snapshot: { ...snapshot, session: { ...session, toolActivities: [{ providerId: "p", runId: null, activityId: "t", phase: "Started", name: null, isNameTruncated: false }] } } }), true);
  assert.equal(showLiveDisplay({ ...empty, snapshot: { ...snapshot, session: { ...session, unsupportedEvents: "1" } } }), true);
});

test("empty asks collapse to a manual refresh; uncertainty, retained actions, and backend dispositions remain visible", () => {
  assert.equal(showAskDetails(undefined, 0, false, false), false);
  assert.equal(showAskDetails(noAsks, 0, false, false), false);
  assert.equal(showAskDetails(noAsks, 1, false, false), true);
  assert.equal(showAskDetails(noAsks, 0, true, false), true);
  assert.equal(showAskDetails(noAsks, 0, false, true), true);
  assert.equal(showAskDetails({ ...noAsks, hasMore: true }, 0, false, false), true);
  assert.equal(showAskDetails({ ...noAsks, latest: {} as NonNullable<AskPage["latest"]> }, 0, false, false), true);
  assert.equal(showAskDetails({ ...noAsks, head: {} as NonNullable<AskPage["head"]> }, 0, false, false), true);
});

test("contextual controls appear only for an eligible observation or a retained exact request", () => {
  assert.equal(showContextAction(false, false), false);
  assert.equal(showContextAction(true, false), true);
  assert.equal(showContextAction(false, true), true);
  const observed: SessionRuntimeStateResponse = { status: "ok", hostEpoch: "epoch", sessionId: "session",
    runtimeInstanceId: "abcdefab-1234-5678-9abc-abcdefabcdef", coordinatorTransitionInProgress: false,
    entry: { attachmentGeneration: "1", activeRunId: "run", isTerminated: false, isRetiring: false,
      queueDrainInProgress: false, providerId: "provider", providerKey: "provider", modelId: null,
      reasoningEffort: null, agentPromptId: null, pendingAgentPromptId: null } };
  const eligibleSteer = (value: SessionRuntimeStateResponse | undefined) => showContextAction(
    captureSteering("epoch", "session", value, "x", "availability") !== null, false);
  const eligibleQueue = (value: SessionRuntimeStateResponse | undefined) => showContextAction(
    captureQueue("epoch", "session", value, "x", "availability") !== null, false);
  assert.equal(eligibleSteer(observed), true);
  assert.equal(eligibleQueue(observed), true);
  assert.equal(eligibleSteer({ ...observed, entry: { ...observed.entry!, activeRunId: null } }), false);
  assert.equal(eligibleQueue({ ...observed, entry: { ...observed.entry!, pendingAgentPromptId: "pending" } }), false);
  assert.equal(eligibleSteer({ ...observed, hostEpoch: "other" }), false);
  assert.equal(eligibleQueue(undefined), false);
  assert.equal(showContextAction(eligibleSteer(undefined), true), true);
});

test("prompt height grows with content and follows viewport changes without exceeding the bound", () => {
  assert.equal(promptEditorHeight(20, 800), 50);
  assert.equal(promptEditorHeight(110, 800), 110);
  assert.equal(promptEditorHeight(400, 800), 240);
  assert.equal(promptEditorHeight(400, 400), 120);
  assert.equal(promptEditorHeight(400, 1000), 240);
});
