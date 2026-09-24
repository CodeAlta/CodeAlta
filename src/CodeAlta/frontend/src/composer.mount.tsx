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
import { resolveShortcut } from "./shortcuts";
import { activateContextShortcut } from "./contextShortcut";
import { createNextSendSelectionStore } from "./nextSendSelection";

const unavailable = async (): Promise<never> => { throw new Error("Fixture must not submit operations"); };
const epoch = "fixture-epoch";
const sessionId = "fixture-session";
const counts = { refreshes: 0, catalogOpens: 0 };
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
  abortRuns: createAbortRunSubmissions(unavailable), queue: createQueueSubmissions(unavailable, unavailable),
  capability: createMutationCapability(epoch), draftIndicators: createDraftIndicators(), permissionReviewer: null,
  selections: createNextSendSelectionStore(key => localStorage.getItem(key), (key, value) => localStorage.setItem(key, value)),
  runtimeReader: createRuntimeStateReader(async () => {
    counts.refreshes++;
    return { status: "ok", hostEpoch: epoch, sessionId,
      entry: null, runtimeInstanceId: "fixture-runtime", coordinatorTransitionInProgress: false };
  }),
};
createRoot(document.getElementById("app")!).render(createElement("div", { id: "workspace-shell" },
  createElement("div", { className: "session-workspace" },
    createElement(OwnedSessionPanel, props),
    createElement("div", { className: "project-rename" }, createElement("label", null, "Project name", createElement("input", { defaultValue: "A project" }))),
    createElement("div", { className: "session-rename" }, createElement("label", null, "Session title", createElement("input", { defaultValue: "A session", disabled: true }))))));
