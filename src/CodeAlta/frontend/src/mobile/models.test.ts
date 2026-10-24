import assert from "node:assert/strict";
import test from "node:test";
import type { ModelOption, Options } from "./contract";
import { choiceLabel, choiceRequest, forgetMode, groupVariants, initialChoice, orderModels, parseModel, pickEffort, pickModel, rememberChoice, tierChoice, tierText } from "./models";

const model = (provider: string, id: string, name = id, efforts: string[] = ["low", "medium", "high"], defaultEffort: string | null = "medium"): ModelOption =>
  ({ provider, id, name, providerName: provider.toUpperCase(), efforts, defaultEffort });
const options = (overrides: Partial<Options> = {}): Options => ({
  models: [
    model("claude", "claude-opus-4-7", "Opus 4.7"), model("claude", "claude-sonnet-4-5", "Sonnet 4.5"), model("claude", "claude-opus-4-1", "Opus 4.1"),
    model("codex", "gpt-6.1", "GPT-6.1"), model("codex", "gpt-6.1-fast", "GPT-6.1 Fast"), model("codex", "gpt-5", "GPT-5", [], null),
  ],
  prompts: [{ id: "default", name: "Default", description: null }, { id: "plan", name: "Plan", description: null }],
  tiers: [
    { tier: "complex", provider: "claude", model: "claude-opus-4-7", reasoning: "high", error: null },
    { tier: "standard", provider: "codex", model: "gpt-6.1", reasoning: "medium", error: null },
    { tier: "quick", provider: "claude", model: "claude-haiku-9", reasoning: null, error: "The model is not offered." },
  ],
  defaultTier: "standard",
  defaults: { provider: "claude", model: "claude-sonnet-4-5", reasoning: "low", prompt: "default" },
  error: null,
  ...overrides,
});

test("model ids are read as a base, a version and a suffix", () => {
  assert.deepEqual(parseModel("claude-opus-4-7"), { base: "claude-opus", version: [4, 7], suffix: "" });
  assert.deepEqual(parseModel("claude-4.5-opus"), { base: "claude-opus", version: [4, 5], suffix: "" });
  assert.deepEqual(parseModel("cursor-kimi-k3"), { base: "kimi", version: [3], suffix: "" });
  assert.deepEqual(parseModel("gpt-6.1-sol-fast"), { base: "gpt", version: [6, 1], suffix: "sol-fast" });
});

test("the frontier holds the latest version of each line, the rest goes to other models", () => {
  const { frontier, other } = orderModels(options().models);
  assert.deepEqual(frontier.map(m => m.id), ["claude-opus-4-7", "claude-sonnet-4-5", "gpt-6.1", "gpt-6.1-fast"]);
  assert.deepEqual(other.map(m => m.id), ["claude-opus-4-1", "gpt-5"]);
});

test("ids that differ only by a variant are one entry with options", () => {
  const [gpt] = groupVariants([model("codex", "gpt-6.1"), model("codex", "gpt-6.1-fast")]);
  assert.equal(gpt.entry.id, "gpt-6.1");
  assert.deepEqual(gpt.options, ["fast"]);
  assert.equal(gpt.find(["fast"])?.id, "gpt-6.1-fast");
  assert.equal(gpt.find(["thinking"]), undefined);
});

test("a provider's model is the wanted one, else the configured one, else the first of its frontier", () => {
  const o = options();
  assert.equal(pickModel(o, "claude", "claude-opus-4-1")?.id, "claude-opus-4-1");
  assert.equal(pickModel(o, "claude", "gone")?.id, "claude-sonnet-4-5");
  assert.equal(pickModel(o, "codex")?.id, "gpt-6.1");
  assert.equal(pickEffort(o, o.models[1]), "low", "the effort of the settings for their model");
  assert.equal(pickEffort(o, o.models[0], "high"), "high");
  assert.equal(pickEffort(o, o.models[0], "max"), "medium", "an effort the model has not falls back to its default");
  assert.equal(pickEffort(o, o.models[5]), "");
});

test("nothing chosen for a project: the default tier of the settings", () => {
  const { choice } = initialChoice(options(), {});
  assert.deepEqual(choice, { tier: "standard", provider: "codex", model: "gpt-6.1", effort: "medium" });
  assert.deepEqual(choiceRequest(choice), { tier: "standard" });
  assert.equal(choiceLabel(options(), choice), "Standard · GPT-6.1");
});

test("a default tier that cannot be used gives the configured provider and model instead", () => {
  const { choice } = initialChoice(options({ defaultTier: "quick" }), {});
  assert.deepEqual(choice, { tier: null, provider: "claude", model: "claude-sonnet-4-5", effort: "low" });
  assert.deepEqual(choiceRequest(choice), { provider: "claude", model: "claude-sonnet-4-5", reasoning: "low" });
});

test("a tier never shows another model than its own, and says why it cannot be chosen", () => {
  const o = options();
  assert.equal(tierChoice(o, "quick"), null);
  assert.equal(tierText(o, o.tiers[2]), "The model is not offered.");
  assert.equal(tierText(o, o.tiers[0]), "Opus 4.7");
  // A tier whose model the list does not have keeps its own model and effort: the server starts it with the tier.
  const unlisted = options({ tiers: [{ tier: "complex", provider: "claude", model: "claude-opus-5", reasoning: "max", error: null }] });
  assert.deepEqual(tierChoice(unlisted, "complex"), { tier: "complex", provider: "claude", model: "claude-opus-5", effort: "max" });
  assert.equal(tierText(unlisted, unlisted.tiers[0]), "claude-opus-5");
  assert.equal(choiceLabel(unlisted, tierChoice(unlisted, "complex")!), "Complex · claude-opus-5");
});

test("the tier a project was left on comes back while it has the same model; a tier whose model changed does not", () => {
  const kept = initialChoice(options(), { tier: "complex", providerKey: "claude", modelId: "claude-opus-4-7", reasoningEffort: "high" });
  assert.equal(kept.choice.tier, "complex");
  const changed = initialChoice(options(), { tier: "complex", providerKey: "claude", modelId: "claude-opus-4-1" });
  assert.deepEqual(changed.choice, { tier: null, provider: "claude", model: "claude-opus-4-1", effort: "medium" });
  assert.equal(changed.remembered.tier, undefined);
});

test("a provider, model or effort no longer offered is forgotten, not kept to fail again", () => {
  const gone = initialChoice(options(), { agentPromptId: "plan", providerKey: "copilot", modelId: "x", reasoningEffort: "high" });
  assert.equal(gone.choice.provider, "claude");
  assert.deepEqual(gone.remembered, { agentPromptId: "plan" });
  const effort = initialChoice(options(), { providerKey: "claude", modelId: "claude-opus-4-7", reasoningEffort: "max" });
  assert.deepEqual(effort.choice, { tier: null, provider: "claude", model: "claude-opus-4-7", effort: "medium" });
  assert.deepEqual(effort.remembered, { providerKey: "claude", modelId: "claude-opus-4-7" });
});

test("what is remembered of a choice", () => {
  assert.deepEqual(rememberChoice("plan", { tier: "complex", provider: "claude", model: "claude-opus-4-7", effort: "high" }),
    { agentPromptId: "plan", providerKey: "claude", modelId: "claude-opus-4-7", reasoningEffort: "high", tier: "complex" });
  assert.deepEqual(rememberChoice(undefined, { tier: null, provider: "codex", model: "gpt-5", effort: "" }), { providerKey: "codex", modelId: "gpt-5" });
});

test("a permission mode is remembered with its provider, and forgotten with it", () => {
  assert.equal(rememberChoice(undefined, { tier: null, provider: "claude", model: "claude-opus-4-7", effort: "high" }, "acceptEdits").permissionMode, "acceptEdits");
  assert.equal(rememberChoice(undefined, { tier: null, provider: "", model: "", effort: "" }, "acceptEdits").permissionMode, undefined, "no provider, no mode");
  const kept = initialChoice(options(), { providerKey: "claude", modelId: "claude-opus-4-7", permissionMode: "acceptEdits" });
  assert.equal(kept.remembered.permissionMode, "acceptEdits");
  const gone = initialChoice(options(), { providerKey: "copilot", modelId: "x", permissionMode: "acceptEdits" });
  assert.equal(gone.remembered.permissionMode, undefined, "a provider no longer offered takes its mode with it");
  assert.deepEqual(forgetMode({ providerKey: "claude", permissionMode: "auto" }), { providerKey: "claude" });
});
