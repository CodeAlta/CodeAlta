import { createRoot } from "react-dom/client";
import { ModelCatalogPanel } from "./ModelCatalogPanel";
import { OwnedSessionPanel } from "./OwnedSessionPanel";
import { applyCatalogNextSend, createNextSendSelectionStore } from "./nextSendSelection";
import { createMutationCapability, createOwnedSubmissions } from "./sessionOperations";
import { createSteeringSubmissions } from "./sessionSteering";
import { createCompactionSubmissions } from "./sessionCompaction";
import { createAbortRunSubmissions } from "./sessionAbortRun";
import { createQueueSubmissions } from "./sessionQueue";
import { createRuntimeStateReader } from "./runtimeState";
import { createDraftIndicators } from "./promptDraft";
import type { ModelCatalogModelsResponse, ModelCatalogProvidersResponse, SessionChoicesResponse, SessionSendRequest } from "#neoastra";

const providerReads: Array<{ epoch: string; resolve: (response: ModelCatalogProvidersResponse) => void; reject: (error: Error) => void }> = [];
const modelReads: Array<{ epoch: string; providerId: string; resolve: (response: ModelCatalogModelsResponse) => void; reject: (error: Error) => void }> = [];
const readProviders: React.ComponentProps<typeof ModelCatalogPanel>["readProviders"] = request => new Promise((resolve, reject) => {
  providerReads.push({ epoch: request.expectedEpoch, resolve, reject });
});
const readModels: React.ComponentProps<typeof ModelCatalogPanel>["readModels"] = request => new Promise((resolve, reject) => {
  modelReads.push({ epoch: request.expectedEpoch, providerId: request.providerId, resolve, reject });
});
const choices: SessionChoicesResponse = { status: "ok", epoch: "epoch-1", sessionId: "one",
  current: { providerKey: "beta", agentPromptId: "plan", modelId: "beta-text", reasoningEffort: "High" },
  prompts: [{ id: "plan", name: "Plan" }, { id: "default", name: "Default" }],
  models: [{ id: "beta-text", name: "Text model", efforts: ["High"] }, { id: "beta-image", name: "Image model", efforts: ["Low", "Medium"] }] };
const unavailable = async (): Promise<never> => { throw new Error("Fixture must not submit this operation."); };
const sent: SessionSendRequest[] = [];
const capability = createMutationCapability("epoch-1");
const submissions = createOwnedSubmissions(async request => { sent.push(request); throw Error("uncertain test-owned Send"); }, unavailable);
const selections = createNextSendSelectionStore(key => localStorage.getItem(key), (key, value) => localStorage.setItem(key, value));
const root = createRoot(document.getElementById("app")!);
const fixture = {
  providerReads, modelReads, sent, choicesReads: 0, holdChoices: false,
  releaseChoices: null as (() => void) | null,
  epoch: "epoch-1" as string | null, sessionId: null as string | null, view: "models" as "models" | "composer",
  show(epoch: string | null) { fixture.epoch = epoch; fixture.view = "models"; render(); },
  session(sessionId: string | null) { fixture.sessionId = sessionId; render(); },
  openModels() { fixture.view = "models"; render(); },
  leaveCatalog() { fixture.view = "composer"; render(); },
  readChoices: async (epoch: string, sessionId: string): Promise<SessionChoicesResponse> => {
    fixture.choicesReads++;
    if (fixture.holdChoices) { fixture.releaseChoices = null; await new Promise<void>(resolve => { fixture.releaseChoices = resolve; }); }
    return { ...choices, epoch, sessionId };
  },
};
Object.assign(window, { catalogFixture: fixture });
const onApply: React.ComponentProps<typeof ModelCatalogPanel>["onApply"] = async (target, signal) => {
  const result = await applyCatalogNextSend(target, () => ({ epoch: fixture.epoch, sessionId: signal.aborted ? null : fixture.sessionId,
    active: fixture.view === "models", canMutate: capability.canMutate(), pending: !!submissions.pending(target.sessionId) }), fixture.readChoices, selections);
  if (result === "applied" && !signal.aborted) { fixture.view = "composer"; render(); }
  return result;
};
function render() {
  const epoch = fixture.epoch;
  const sessionId = fixture.sessionId;
  if (fixture.view === "composer" && epoch && sessionId) {
    root.render(<div className="session-workspace"><OwnedSessionPanel epoch={epoch} sessionId={sessionId}
      selections={selections} submissions={submissions} capability={capability} draftIndicators={createDraftIndicators()}
      steering={createSteeringSubmissions(unavailable)} compaction={createCompactionSubmissions(unavailable)}
      abortRuns={createAbortRunSubmissions(unavailable)} queue={createQueueSubmissions(unavailable, unavailable)}
      permissionReviewer={null} runtimeReader={createRuntimeStateReader(async () => ({ status: "ok", hostEpoch: epoch,
        sessionId, entry: null, runtimeInstanceId: "literal-runtime", coordinatorTransitionInProgress: false }))} /></div>);
  } else root.render(<ModelCatalogPanel epoch={epoch} readProviders={readProviders} readModels={readModels}
    readChoices={(request) => fixture.readChoices(request.expectedEpoch, request.sessionId)}
    selections={selections} target={epoch && sessionId ? { epoch, sessionId } : null}
    pendingSend={!!(sessionId && submissions.pending(sessionId))}
    pendingSelection={sessionId ? submissions.pending(sessionId)?.request.selection ?? null : null} onApply={onApply} />);
}
render();
