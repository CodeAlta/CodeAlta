import assert from "node:assert/strict";
import test from "node:test";
import { defaultRunProvider, runModel, runProvider, type RunModel, type RunProvider } from "./runsWith";

const provider = (id: string, more: Partial<RunProvider> = {}): RunProvider => ({ id, name: id.toUpperCase(), isDefault: false, defaultModel: null, defaultReasoning: null, ...more });
const model = (id: string, efforts: string[] = [], defaultEffort: string | null = null): RunModel => ({ id, efforts, defaultEffort });

test("work starts with the default provider, and with the first enabled one when none is the default", () => {
  const providers = [provider("anthropic"), provider("codex", { isDefault: true }), provider("copilot")];
  assert.equal(defaultRunProvider(providers)?.id, "codex");
  assert.equal(defaultRunProvider([provider("anthropic"), provider("copilot")])?.id, "anthropic");
  assert.equal(defaultRunProvider([]), null);
});

test("the provider is the one chosen, then the one the item was proposed with, then the default", () => {
  const providers = [provider("anthropic"), provider("codex", { isDefault: true }), provider("copilot")];
  const recorded = { providerId: "copilot", modelId: "m", reasoningEffort: null };
  assert.equal(runProvider(providers, null, {})?.id, "codex");
  assert.equal(runProvider(providers, recorded, {})?.id, "copilot", "the session that proposed the item ran with it");
  assert.equal(runProvider(providers, recorded, { providerId: "anthropic" })?.id, "anthropic", "the user chose another");
  assert.equal(runProvider(providers, { ...recorded, providerId: "Copilot" }, {})?.id, "copilot", "a provider key has no case");
  // A provider that is no longer enabled is passed over: the next source gives the provider.
  assert.equal(runProvider(providers, { ...recorded, providerId: "gone" }, {})?.id, "codex");
  assert.equal(runProvider(providers, recorded, { providerId: "gone" })?.id, "copilot");
  assert.equal(runProvider([], recorded, {}), null);
});

test("the model and the effort are those chosen, then those of the item, then those a new session starts with", () => {
  const codex = provider("codex", { defaultModel: "gpt-b", defaultReasoning: "low" });
  const models = [model("gpt-a", ["low", "medium", "high"]), model("gpt-b", ["low", "high"]), model("plain")];
  const recorded = { providerId: "codex", modelId: "gpt-a", reasoningEffort: "medium" };

  assert.deepEqual(runModel(codex, models, null, {}), { modelId: "gpt-b", reasoningEffort: "low" }, "the configured model and effort of the provider");
  assert.deepEqual(runModel(provider("codex"), models, null, {}), { modelId: "gpt-a", reasoningEffort: "high" }, "the first model, with High");
  assert.deepEqual(runModel(codex, models, recorded, {}), { modelId: "gpt-a", reasoningEffort: "medium" }, "what the item was proposed with");
  assert.deepEqual(runModel(codex, models, { ...recorded, reasoningEffort: "Medium" }, {}), { modelId: "gpt-a", reasoningEffort: "medium" }, "the effort as the model spells it");

  // What the item was proposed with says nothing of another provider, of a model that is gone, or of another model.
  assert.deepEqual(runModel(codex, models, { ...recorded, providerId: "copilot" }, {}), { modelId: "gpt-b", reasoningEffort: "low" });
  assert.deepEqual(runModel(codex, models, { ...recorded, modelId: "retired" }, {}), { modelId: "gpt-b", reasoningEffort: "low" });
  assert.deepEqual(runModel(codex, models, recorded, { modelId: "gpt-b" }), { modelId: "gpt-b", reasoningEffort: "low" }, "another model takes its own effort");
  assert.deepEqual(runModel(codex, models, recorded, { modelId: "gpt-a", reasoningEffort: "high" }), { modelId: "gpt-a", reasoningEffort: "high" });
  assert.deepEqual(runModel(codex, models, recorded, { reasoningEffort: "high" }), { modelId: "gpt-a", reasoningEffort: "high" }, "an effort alone keeps the model");
  assert.deepEqual(runModel(codex, models, recorded, { modelId: "gpt-b", reasoningEffort: "medium" }), { modelId: "gpt-b", reasoningEffort: "low" }, "an effort the model has not");
  assert.deepEqual(runModel(codex, models, null, { modelId: "plain" }), { modelId: "plain", reasoningEffort: null }, "a model without efforts");

  // Nothing is named while the provider lists no model: the host decides.
  assert.deepEqual(runModel(codex, [], recorded, {}), { modelId: null, reasoningEffort: null });
  assert.deepEqual(runModel(null, models, recorded, {}), { modelId: null, reasoningEffort: null });
});
