// Disposable page: Settings > Code Mode and the Code Mode field of a provider, over a host that answers at once.
// Driven by codeModeSettings.browser.test.ts through `codeModeFixture`.
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import type { GlobalConfigCodeModeProviderResponse, GlobalConfigCodeModeScopeItem, GlobalConfigCodeModeSettingsResponse, GlobalConfigSaveCodeModeScopeRequest,
  ModelCatalogModelsResponse } from "#neoastra";
import { CodeModeSettings } from "./CodeModeSettings";
import { ProviderCodeModeField, type CodeModeScopeApi } from "./CodeModeScopeControls";
import { ShellLanguageContext } from "./shellLanguage";

document.documentElement.classList.add("bp6-dark");

// What the host holds: the mode of every session (null: on), of the provider and of its models.
const held = { mode: null as string | null, provider: null as string | null, models: new Map<string, string>([["copilot-haiku", "off"]]) };
const state = { savedModes: [] as string[], savedScopes: [] as GlobalConfigSaveCodeModeScopeRequest[], opened: [] as GlobalConfigCodeModeScopeItem[] };
const settings = (): GlobalConfigCodeModeSettingsResponse => ({ status: "ok", mode: held.mode ?? "on", modes: ["off", "on", "only"],
  scopes: [...(held.provider ? [{ providerKey: "copilot", providerName: "Copilot", modelId: null, mode: held.provider }] : []),
    ...[...held.models].map(([modelId, mode]) => ({ providerKey: "copilot", providerName: "Copilot", modelId, mode }))] });
const api = {
  codeModeSettings: async () => settings(),
  // On is the absence of the setting.
  saveCodeModeSettings: async (request: { mode: string | null }) => { state.savedModes.push(request.mode!); held.mode = request.mode === "on" ? null : request.mode; return settings(); },
};
const scopes: CodeModeScopeApi = {
  codeModeProvider: async (): Promise<GlobalConfigCodeModeProviderResponse> => ({ status: "ok", provider: held.provider, allSessions: held.mode ?? "on",
    models: [...held.models].map(([modelId, mode]) => ({ modelId, mode })) }),
  saveCodeModeScope: async request => {
    state.savedScopes.push(request);
    if (!request.modelId) held.provider = request.mode;
    else if (request.mode) held.models.set(request.modelId, request.mode);
    else held.models.delete(request.modelId);
    return { status: "ok", message: null };
  },
};
const model = (id: string, name: string) => ({ id, name, description: null, efforts: [], defaultEffort: null, contextTokens: null, inputTokens: null, outputTokens: null,
  reasoning: null, tools: null, structuredOutput: null, imageInput: null });
const readModels = async (): Promise<ModelCatalogModelsResponse> => ({ status: "ok", epoch: "epoch", providerId: "copilot", availability: "Ready", truncated: false,
  models: [model("copilot-haiku", "Claude Haiku 4.5"), model("gpt-6.1-sol", "GPT-6.1 Sol"), model("gemini-4-argon", "Gemini 4 Argon")] });
const filesApi = { list: async () => ({ status: "ok", platform: "windows", locations: [] }), open: async () => ({ status: "ok" }), reveal: async () => ({ status: "ok" }) };
Object.assign(window, { codeModeFixture: state });

createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => {} }}>
    <div className="ide-shell" style={{ padding: 12, overflow: "auto", height: "100%", boxSizing: "border-box" }}>
      <CodeModeSettings epoch="epoch" api={api} filesApi={filesApi} onOpenScope={scope => state.opened.push(scope)} />
      <div className="provider-settings-fields" style={{ marginTop: 16, maxWidth: 640 }}>
        <ProviderCodeModeField epoch="epoch" providerKey="copilot" providerName="Copilot" api={scopes} readModels={readModels} />
      </div>
    </div>
  </ShellLanguageContext.Provider>
</StrictMode>);
