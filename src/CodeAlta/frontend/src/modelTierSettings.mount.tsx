// Disposable page: the editor of the model tiers, for the global tiers of Settings > Models and for the own tiers of a
// space, over a store and a save that answer at once. Driven by modelTierSettings.browser.test.ts through `tiersFixture`.
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import type { GlobalConfigModelTiersResponse, GlobalConfigSaveModelTiersRequest, ModelCatalogModel, ModelCatalogProvider, ModelTierList } from "#neoastra";
import { createTierStore, readTierState } from "./modelTiers";
import { ModelTierSettings } from "./ModelTierSettings";
import { ShellLanguageContext } from "./shellLanguage";

document.documentElement.classList.add("bp6-dark");

const provider = (id: string, name: string): ModelCatalogProvider =>
  ({ id, name, type: id, enabled: true, availability: "Ready", defaultModel: null, isDefault: false, observedAt: null });
const model = (id: string, name: string, efforts: string[]): ModelCatalogModel =>
  ({ id, name, efforts, defaultEffort: null, description: null, contextTokens: null, inputTokens: null, outputTokens: null, reasoning: true, tools: true, structuredOutput: null, imageInput: null });
const providers = [provider("claude-code", "Claude Code"), provider("codex", "Codex")];
const models = new Map([
  ["claude-code", [model("opus", "Opus", ["Medium", "High", "XHigh"]), model("sonnet", "Sonnet", ["Low", "Medium", "High"]), model("haiku", "Haiku", ["Low"])]],
  ["codex", [model("gpt-6.1", "GPT-6.1", ["Low", "Medium", "High", "XHigh"]), model("gpt-6.2", "GPT-6.2", ["Low", "Medium"])]],
]);
const list = (tier: string, targets: [string, string, string[]][]): ModelTierList =>
  ({ tier, targets: targets.map(([providerKey, modelId, reasoning]) => ({ providerKey, modelId, reasoning })), choice: null });

// What the host holds, by scope ("" for the global tiers), and what was saved.
const held = new Map<string, ModelTierList[]>([
  ["", [list("complex", [["claude-code", "opus", ["High", "XHigh"]], ["codex", "gpt-6.1", ["XHigh"]], ["gone", "m", []]]), list("standard", []), list("quick", [])]],
  ["business", [list("complex", []), list("standard", []), list("quick", [["claude-code", "haiku", ["Low"]], ["codex", "gpt-6.1", ["Medium", "High"]]])]],
]);
const fallback = { enabled: false, onQuota: true, onUnavailable: true };
const state = { saved: [] as GlobalConfigSaveModelTiersRequest[] };
const reply = (spaceId: string | null): GlobalConfigModelTiersResponse =>
  ({ status: "ok", message: null, source: spaceId ? "space" : "global", spaceId, fallback, tiers: held.get(spaceId ?? "")! });
const store = createTierStore(async request => reply(request.spaceId));
const api = {
  saveModelTiers: async (request: GlobalConfigSaveModelTiersRequest) => {
    state.saved.push(request);
    held.set(request.spaceId ?? "", [...request.tiers]);
    return reply(request.spaceId);
  },
};
store.set("epoch", readTierState(reply(null))!);
store.set("epoch", readTierState(reply("business"))!, { spaceId: "business" });
Object.assign(window, { tiersFixture: state });

createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => {} }}>
    <div className="ide-shell" style={{ padding: 12, overflow: "auto", height: "100%", boxSizing: "border-box" }}>
      <div id="global"><ModelTierSettings epoch="epoch" providers={providers} models={models} api={api} store={store} /></div>
      <div id="space"><ModelTierSettings epoch="epoch" spaceId="business" providers={providers} models={models} api={api} store={store} /></div>
    </div>
  </ShellLanguageContext.Provider>
</StrictMode>);
