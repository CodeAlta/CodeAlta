import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { ModelCatalogModel, ModelCatalogProvider } from "#neoastra";
import { createTierStore, readTierState } from "./modelTiers";
import { ModelTierSettings } from "./ModelTierSettings";
import { ShellLanguageContext } from "./shellLanguage";

const never = () => assert.fail("rendering must not act");
const provider = (id: string, name: string): ModelCatalogProvider =>
  ({ id, name, type: id, enabled: true, availability: "Ready", defaultModel: null, isDefault: false, observedAt: null });
const model = (id: string, name: string, efforts: string[]): ModelCatalogModel =>
  ({ id, name, efforts, defaultEffort: null, description: null, contextTokens: null, inputTokens: null, outputTokens: null, reasoning: true, tools: true, structuredOutput: null, imageInput: null });

test("the tiers show their models in order with their efforts, the model each gives, and the fallback", () => {
  const store = createTierStore(() => Promise.reject(new Error("not read")));
  store.set("epoch", readTierState({
    fallback: { enabled: true, onQuota: true, onUnavailable: false },
    tiers: [
      { tier: "complex", targets: [{ providerKey: "claude-code", modelId: "opus", reasoning: ["High", "XHigh"] }, { providerKey: "gone", modelId: "m", reasoning: [] }],
        choice: { providerKey: "claude-code", modelId: "opus", reasoning: "High", providerName: "Claude Code", modelName: "Opus" } },
      { tier: "standard", targets: [], choice: null },
      { tier: "quick", targets: [{ providerKey: "claude-code", modelId: "haiku", reasoning: ["Low"] }], choice: null },
    ],
  })!);
  const html = renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale: "en", choice: "en", setLanguage: never } },
    createElement(ModelTierSettings, {
      epoch: "epoch", store, api: { saveModelTiers: never },
      providers: [provider("claude-code", "Claude Code")],
      models: new Map([["claude-code", [model("opus", "Opus", ["Medium", "High", "XHigh"]), model("haiku", "Haiku", ["Low"])]]]),
    })));

  assert.match(html, /<h2 id="model-tiers-title">Model tiers<\/h2>/);
  assert.match(html, /aria-label="Complex"[^>]*>.*Gives Opus · High/);
  assert.match(html, /aria-label="Standard"[^>]*>.*No model yet\./);
  assert.match(html, /aria-label="Quick"[^>]*>.*No model ready/);
  // The models in order, named by the provider's inventory, with their efforts in order.
  assert.ok(html.indexOf("<strong>Opus</strong>") < html.indexOf("<strong>m</strong>"));
  assert.ok(html.indexOf(">High<") < html.indexOf(">XHigh<"));
  // A provider that is no longer configured is named by its key, and said.
  assert.match(html, /data-missing="true" title="Not a configured provider">gone</);
  assert.match(html, /aria-label="Remove Opus from Complex"/);
  assert.match(html, /aria-label="Move Opus"/);
  // The model of a row is a button that changes it, a row of a missing provider included.
  assert.match(html, /<button type="button" class="model-tier-model"[^>]*aria-label="Change the model of Opus"/);
  assert.match(html, /<button type="button" class="model-tier-model"[^>]*aria-label="Change the model of m"/);
  assert.match(html, /aria-label="Add a model to Standard"/);
  // The fallback and its triggers.
  assert.match(html, /Fall back to the next model of the tier when a provider fails/);
  assert.match(html, /Quota reached/);
});
