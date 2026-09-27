import { createRoot } from "react-dom/client";
import { InventoryLanguageFixture } from "./inventoryLanguage.mount";
import { PromptCatalogPanel } from "./PromptCatalogPanel";
import { OwnedSessionPanel } from "./OwnedSessionPanel";
import { applyPromptNextSend, createNextSendSelectionStore } from "./nextSendSelection";
import { createMutationCapability, createOwnedSubmissions } from "./sessionOperations";
import { createSteeringSubmissions } from "./sessionSteering";
import { createCompactionSubmissions } from "./sessionCompaction";
import { createAbortRunSubmissions } from "./sessionAbortRun";
import { createQueueSubmissions } from "./sessionQueue";
import { createRuntimeStateReader } from "./runtimeState";
import { createDraftIndicators } from "./promptDraft";
import type { PromptCatalogResponse, SessionChoicesResponse, SessionSendRequest } from "#neoastra";

const reads: Array<{ sessionId: string; epoch: string; resolve: (value: PromptCatalogResponse) => void; reject: (error: Error) => void }> = [];
const readPrompts: React.ComponentProps<typeof PromptCatalogPanel>["readPrompts"] = request => new Promise((resolve, reject) => {
  reads.push({ epoch: request.expectedEpoch, sessionId: request.sessionId, resolve, reject });
});
const choices: SessionChoicesResponse = { status: "ok", epoch: "e1", sessionId: "one",
  current: { providerKey: "beta", agentPromptId: "default", modelId: "model", reasoningEffort: "High" },
  prompts: [{ id: "default", name: "Default" }, { id: "plan", name: "Plan" }],
  models: [{ id: "model", name: "Model", efforts: ["High"], imageInput: null }] };
const unavailable = async (): Promise<never> => { throw new Error("Fixture must not submit this operation."); };
const sent: SessionSendRequest[] = [];
const capability = createMutationCapability("e1");
const submissions = createOwnedSubmissions(async request => { sent.push(request); throw Error("uncertain test-owned Send"); }, unavailable);
const selections = createNextSendSelectionStore(key => localStorage.getItem(key), (key, value) => localStorage.setItem(key, value));
const root = createRoot(document.getElementById("app")!);
const fixture = {
  reads, sent, choices, imageOwner: submissions.imageDrafts, choicesReads: 0, hold: false, release: null as (() => void) | null,
  lastChoices: null as SessionChoicesResponse | null,
  publish(promptId: string) {
    const value = fixture.lastChoices!;
    selections.set(value.epoch!, value.sessionId!, value, { ...value.current!, agentPromptId: promptId });
  },
  repeatSelection() {
    const value = fixture.lastChoices!;
    const current = selections.current(value.epoch!, value.sessionId!) ?? value.current!;
    selections.set(value.epoch!, value.sessionId!, value, current);
  },
  selectRemovedModel() {
    const value = { ...fixture.lastChoices!, models: [...fixture.lastChoices!.models,
      { id: "removed", name: "Removed", efforts: ["Low"], imageInput: null }] };
    selections.set(value.epoch!, value.sessionId!, value, { ...value.current!, modelId: "removed", reasoningEffort: "Low" });
  },
  epoch: "e1" as string | null, sessionId: "one" as string | null, view: "prompts" as "prompts" | "composer",
  show(epoch: string | null) { fixture.epoch = epoch; fixture.view = "prompts"; render(); },
  session(id: string | null) { fixture.sessionId = id; render(); },
  open() { fixture.view = "prompts"; render(); },
  leave() { fixture.view = "composer"; render(); },
  async readChoices(epoch: string, sessionId: string): Promise<SessionChoicesResponse> {
    fixture.choicesReads++;
    if (fixture.hold) { fixture.release = null; await new Promise<void>(resolve => { fixture.release = resolve; }); }
    fixture.lastChoices = { ...choices, epoch, sessionId };
    return fixture.lastChoices;
  },
};
Object.assign(window, { promptFixture: fixture });
const onApply: React.ComponentProps<typeof PromptCatalogPanel>["onApply"] = async (target, signal) => {
  const result = await applyPromptNextSend(target, () => ({ epoch: fixture.epoch,
    sessionId: signal.aborted ? null : fixture.sessionId, active: fixture.view === "prompts" && !signal.aborted,
    canMutate: capability.canMutate(), pending: !!submissions.pending(target.sessionId) }), fixture.readChoices, selections);
  if (result === "applied" && !signal.aborted) { fixture.view = "composer"; render(); }
  return result;
};
function render() {
  const epoch = fixture.epoch, sessionId = fixture.sessionId;
  if (fixture.view === "composer" && epoch && sessionId) root.render(<div className="session-workspace">
    <OwnedSessionPanel epoch={epoch} sessionId={sessionId} selections={selections} submissions={submissions}
      capability={capability} draftIndicators={createDraftIndicators()}
      steering={createSteeringSubmissions(unavailable)} compaction={createCompactionSubmissions(unavailable)}
      abortRuns={createAbortRunSubmissions(unavailable)} queue={createQueueSubmissions(unavailable, unavailable)}
      permissionReviewer={null} runtimeReader={createRuntimeStateReader(async () => ({ status: "ok", hostEpoch: epoch,
        sessionId, entry: null, runtimeInstanceId: "literal-runtime", coordinatorTransitionInProgress: false }))} /></div>);
  else root.render(<InventoryLanguageFixture><PromptCatalogPanel epoch={epoch} target={epoch && sessionId ? { epoch, sessionId } : null}
    readPrompts={readPrompts} readChoices={readChoices} selections={selections}
    pendingSend={!!(sessionId && submissions.pending(sessionId))}
    pendingSelection={sessionId ? submissions.pending(sessionId)?.request.selection ?? null : null} onApply={onApply} /></InventoryLanguageFixture>);
}
const readChoices: React.ComponentProps<typeof PromptCatalogPanel>["readChoices"] = request => fixture.readChoices(request.expectedEpoch, request.sessionId);
render();
