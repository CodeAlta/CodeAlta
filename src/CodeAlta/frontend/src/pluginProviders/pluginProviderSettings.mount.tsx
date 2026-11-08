// Disposable page: the settings of a provider an ACP plugin runs, signed out when the page opens and ready once tested.
// Driven by pluginProviderSettings.browser.test.ts through `pluginSettingsFixture`.
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import type { GlobalConfigProvider } from "#neoastra";
import { ProviderSettings } from "../ProviderSettings";

const provider: GlobalConfigProvider = { key: "copilot", type: "acp", enabled: true, displayName: null, effectiveName: "Copilot", model: null,
  reasoningEffort: null, apiUrl: null, effectiveApiUrl: null, apiKeyEnv: null, hasApiKey: false,
  defaults: { displayName: "Copilot", model: null, reasoningEffort: null, apiUrl: null, apiKeyEnv: null }, icon: null, color: null, anthropicApiKey: null,
  permissionMode: null, configDir: null, command: null, environment: null, modelsOrder: null, plugin: { iconData: null, tag: null, values: [] } };
const state = { probes: 0, details: 0 };
const result = { status: "ok", message: null, revision: "revision", line: null, column: null, providersApplied: 1, key: null };
const config = {
  providers: async () => ({ status: "ok", revision: "revision", defaultProvider: "copilot", startingProvider: "copilot", providers: [provider],
    providerTypes: ["acp"], reasoningEfforts: [], permissionModes: [], typeDefaults: [], builtIn: [], unsupported: [],
    pluginTypes: [{ type: "acp", name: "ACP agent", tag: null, catalogTitle: null, hasAccount: true, hasCatalog: false, modelsOrder: "provider", permissionModes: [], settings: [] }] }),
  saveProvider: async () => result, deleteProvider: async () => result, addBuiltInProvider: async () => result, duplicateProvider: async () => result,
};
const plugins = {
  pluginProviderDetails: async () => { state.details++; return { status: "ok", summary: "copilot --acp", detail: null, update: "1.0.2", signInMethods: [] }; },
  savePluginProvider: async () => result, pluginCatalog: async () => { throw new Error("Not in this page."); },
  installPluginProvider: async () => { throw new Error("Not in this page."); }, updatePluginProvider: async () => { throw new Error("Not in this page."); },
  pluginProviderSignIn: async () => { throw new Error("Not in this page."); },
} as unknown as NonNullable<Parameters<typeof ProviderSettings>[0]["plugins"]>;
Object.assign(window, { pluginSettingsFixture: state });

createRoot(document.getElementById("root")!).render(<StrictMode><ProviderSettings epoch="epoch" config={config} plugins={plugins}
  // Signed out when the page opens: the form says so above its fields.
  readRuntime={async () => ({ status: "ok", epoch: "epoch", truncated: false,
    providers: [{ id: "copilot", availability: state.probes > 0 ? "Ready" : "Failed", reason: state.probes > 0 ? null : "acp-signed-out", capabilities: null }] }) as never}
  probe={async request => { state.probes++; return { status: "ok", epoch: "epoch", providerId: request.providerId, availability: "Ready", reason: null }; }}
  filesApi={{ list: async () => ({ status: "ok", platform: "windows", locations: [] }), open: async () => ({ status: "ok" }), reveal: async () => ({ status: "ok" }) }}
  onOpenModels={() => {}} onOpenConfiguration={() => {}} /></StrictMode>);
