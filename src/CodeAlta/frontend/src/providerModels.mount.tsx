import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import type { GlobalConfigProvider, ModelCatalogModelsRequest, ModelCatalogModelsResponse } from "#neoastra";
import { ProviderSettings } from "./ProviderSettings";

const provider = (key: string): GlobalConfigProvider => ({ key, type: "openai-chat", enabled: true, displayName: key, effectiveName: key, model: null,
  reasoningEffort: null, apiUrl: "http://localhost/v1", effectiveApiUrl: "http://localhost/v1", apiKeyEnv: null, hasApiKey: false,
  defaults: { displayName: key, model: null, reasoningEffort: null, apiUrl: null, apiKeyEnv: null }, icon: null, color: null, anthropicApiKey: null, permissionMode: null, configDir: null, command: null });
const state = { calls: [] as ModelCatalogModelsRequest[], saved: [] as unknown[], status: "ok", hold: false, pending: [] as (() => void)[],
  replyEpoch: "epoch", replyProvider: null as string | null, empty: false, truncated: false, listed: 0 };
// The first provider's template names a default model its live inventory no longer lists.
const providers = [{ ...provider("first"), defaults: { ...provider("first").defaults, model: "first-retired" } }, provider("second")];
const readModels = async (request: ModelCatalogModelsRequest): Promise<ModelCatalogModelsResponse> => {
  state.calls.push(request);
  const reply = { status: state.status, epoch: state.replyEpoch, providerId: state.replyProvider ?? request.providerId, availability: "Ready", truncated: state.truncated,
    models: (state.empty ? [] : ["fast", "reasoning", ...Array.from({ length: state.listed }, (_, index) => `listed-${String(index).padStart(3, "0")}`)]).map(id => ({ id: `${request.providerId}-${id}`, name: `${id} model`, description: null, efforts: [],
      defaultEffort: null, contextTokens: null, inputTokens: null, outputTokens: null, reasoning: null, tools: null, structuredOutput: null, imageInput: null })) };
  if (state.hold) await new Promise<void>(resolve => state.pending.push(resolve));
  return reply;
};
const result = { status: "ok", message: null, revision: "revision", line: null, column: null, providersApplied: 2, key: null };
const config = {
  providers: async () => ({ status: "ok", revision: "revision", defaultProvider: "first", startingProvider: "first", providers,
    providerTypes: ["openai-chat"], reasoningEfforts: [], permissionModes: [], typeDefaults: [], builtIn: [], unsupported: [] }),
  saveProvider: async (request: unknown) => { state.saved.push(request); return result; },
  deleteProvider: async () => result, addBuiltInProvider: async () => result, duplicateProvider: async () => result,
};
Object.assign(window, { modelsFixture: state });
createRoot(document.getElementById("root")!).render(<StrictMode><ProviderSettings epoch="epoch" config={config} readModels={readModels}
  readRuntime={async () => ({ status: "ok", epoch: "epoch", providers: [], truncated: false })}
  probe={async request => ({ status: "ok", epoch: "epoch", providerId: request.providerId, availability: "Ready", reason: null })}
  filesApi={{ list: async () => ({ status: "ok", platform: "windows", locations: [] }), open: async () => ({ status: "ok" }), reveal: async () => ({ status: "ok" }) }}
  onOpenModels={() => {}} onOpenConfiguration={() => {}} /></StrictMode>);
