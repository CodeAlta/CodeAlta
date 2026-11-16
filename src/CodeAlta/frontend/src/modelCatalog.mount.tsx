import { StrictMode, useState } from "react";
import { HotkeysProvider } from "@blueprintjs/core";
import { createRoot } from "react-dom/client";
import type { ModelCatalogModelsRequest, ModelCatalogProbeRequest } from "#neoastra";
import { ModelCatalogPanel } from "./ModelCatalogPanel";
import { createNextSendSelectionStore } from "./nextSendSelection";

const state = { reads: [] as string[], probes: [] as string[], listed: "retired", hold: false, pending: [] as (() => void)[],
  status: "ok", replyEpoch: "epoch", replyProvider: null as string | null, reject: false };
const selections = createNextSendSelectionStore(() => null, () => {});
const readProviders = async () => ({ status: "ok", epoch: "epoch", truncated: false,
  providers: ["first", "second", "disabled"].map(id => ({ id, name: id, type: "openai-chat", enabled: id !== "disabled", availability: "Ready", isDefault: false, defaultModel: null, observedAt: null })) });
const readModels = async (request: ModelCatalogModelsRequest) => {
  state.reads.push(request.providerId);
  return { status: "ok", epoch: "epoch", providerId: request.providerId, availability: "Ready", truncated: false,
    models: [{ id: `${request.providerId}-${state.listed}`, name: `${request.providerId}-${state.listed}`, description: null, efforts: [], defaultEffort: null,
      contextTokens: null, inputTokens: null, outputTokens: null, reasoning: null, tools: null, structuredOutput: null, imageInput: null }] };
};
const refreshModels = async (request: ModelCatalogProbeRequest) => {
  state.probes.push(request.providerId);
  if (state.hold) await new Promise<void>(resolve => state.pending.push(resolve));
  if (state.reject) throw new Error("private diagnostic");
  return { status: state.status, epoch: state.replyEpoch, providerId: state.replyProvider ?? request.providerId, availability: "Ready", reason: null };
};
function Fixture() {
  const [mounted, setMounted] = useState(true);
  Object.assign(state, { unmount: () => setMounted(false) });
  return <div className="settings-dialog-content" style={{ height: "100%" }}>{mounted && <ModelCatalogPanel epoch="epoch" readProviders={readProviders} readModels={readModels}
    refreshModels={refreshModels} readChoices={async () => { throw new Error("no session selected"); }} target={null}
    selections={selections} pendingSend={false} pendingSelection={null} onApply={async () => "applied"} />}</div>;
}
Object.assign(window, { catalogFixture: state });
createRoot(document.getElementById("root")!).render(<StrictMode><HotkeysProvider><Fixture /></HotkeysProvider></StrictMode>);
