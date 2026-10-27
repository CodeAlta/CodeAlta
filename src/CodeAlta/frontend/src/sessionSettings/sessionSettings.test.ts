import assert from "node:assert/strict";
import test from "node:test";
import type { RunModel, RunProvider } from "../workItems/runsWith";
import { chooseEffort, chooseModel, chooseProvider, defaultSessionSettings, shownModel, startRequest, switchDefaults, usesDefaults, type SessionSettings } from "./sessionSettings";

const providers: readonly RunProvider[] = [
  { id: "anthropic", name: "Anthropic", isDefault: false, defaultModel: null, defaultReasoning: null },
  { id: "codex", name: "Codex", isDefault: true, defaultModel: "gpt-b", defaultReasoning: "low" },
];
const models: readonly RunModel[] = [
  { id: "gpt-a", efforts: ["low", "high"], defaultEffort: "high" },
  { id: "gpt-b", efforts: ["low", "medium", "high"], defaultEffort: "medium" },
];
const settings = (change: Partial<SessionSettings> = {}): SessionSettings => ({ ...defaultSessionSettings, ...change });

test("the form shows what the defaults give: the default provider, its model and its effort", () => {
  assert.deepEqual(shownModel(providers, models, null, defaultSessionSettings),
    { provider: providers[1], providerId: "codex", modelId: "gpt-b", reasoningEffort: "low" });
  // Before the models are read, the provider only.
  assert.deepEqual(shownModel(providers, null, null, defaultSessionSettings), { provider: providers[1], providerId: "codex", modelId: null, reasoningEffort: null });
  // Without a default provider, the first enabled one.
  const none = providers.map(provider => ({ ...provider, isDefault: false }));
  assert.equal(shownModel(none, null, null, defaultSessionSettings).providerId, "anthropic");
  // What the work was proposed with comes before the defaults.
  assert.deepEqual(shownModel(providers, models, { providerId: "codex", modelId: "gpt-a", reasoningEffort: "high" }, defaultSessionSettings),
    { provider: providers[1], providerId: "codex", modelId: "gpt-a", reasoningEffort: "high" });
});

test("what the user chose is shown as it is, even when it is no longer offered", () => {
  assert.deepEqual(shownModel(providers, [], null, settings({ providerId: "gone", modelId: "old", reasoningEffort: "max" })),
    { provider: null, providerId: "gone", modelId: "old", reasoningEffort: "max" });
  assert.deepEqual(shownModel(providers, models, null, settings({ providerId: "codex", modelId: "retired", reasoningEffort: "high" })),
    { provider: providers[1], providerId: "codex", modelId: "retired", reasoningEffort: "high" });
  assert.equal(shownModel(providers, models, null, settings({ providerId: "codex", modelId: "gpt-a", reasoningEffort: "xhigh" })).reasoningEffort, "xhigh");
});

test("the switch of the defaults: off takes what they give as the choice, on forgets every choice but the agent prompt", () => {
  const shown = shownModel(providers, models, null, defaultSessionSettings);
  const chosen = switchDefaults(settings({ agentPromptId: "plan" }), false, shown);
  assert.deepEqual(chosen, settings({ agentPromptId: "plan", providerId: "codex", modelId: "gpt-b", reasoningEffort: "low" }));
  assert.equal(usesDefaults(chosen), false);
  const back = switchDefaults({ ...chosen, permissionMode: "acceptEdits", reviewBeforeCommit: true, sandbox: "off" }, true, shown);
  assert.deepEqual(back, settings({ agentPromptId: "plan" }));
  assert.equal(usesDefaults(back), true);
  // The options are not models of the defaults: they keep their choice either way.
  const leaf = settings({ options: { subSessions: false } });
  assert.equal(usesDefaults(leaf), true);
  assert.deepEqual(switchDefaults(leaf, false, shown).options, { subSessions: false });
  assert.deepEqual(switchDefaults({ ...leaf, providerId: "codex" }, true, shown), leaf);
});

test("a change keeps what goes with it: another provider has its own model, effort and permission mode", () => {
  const shown = { providerId: "codex", modelId: "gpt-b", reasoningEffort: "low" };
  const chosen = settings({ providerId: "codex", modelId: "gpt-b", reasoningEffort: "low", permissionMode: "plan", sandbox: "off" });
  assert.deepEqual(chooseProvider(chosen, "anthropic"), settings({ providerId: "anthropic", sandbox: "off" }));
  assert.deepEqual(chooseModel(settings(), "gpt-a", shown), settings({ providerId: "codex", modelId: "gpt-a" }));
  assert.deepEqual(chooseEffort(settings({ providerId: "codex" }), "high", shown), settings({ providerId: "codex", modelId: "gpt-b", reasoningEffort: "high" }));
});

test("a start asks nothing of the model with the defaults, and what the form shows otherwise", () => {
  const shown = { providerId: "codex", modelId: "gpt-b", reasoningEffort: "low" };
  assert.deepEqual(startRequest(settings({ agentPromptId: "plan" }), shown), settings({ agentPromptId: "plan" }));
  assert.deepEqual(startRequest(settings({ providerId: "codex", reviewBeforeCommit: false }), shown),
    settings({ providerId: "codex", modelId: "gpt-b", reasoningEffort: "low", reviewBeforeCommit: false }));
  // A model not read yet is left to the host, with its effort.
  assert.deepEqual(startRequest(settings({ providerId: "codex" }), { providerId: "codex", modelId: null, reasoningEffort: null }), settings({ providerId: "codex" }));
  // The options go with the start, with the defaults or not.
  assert.deepEqual(startRequest(settings({ options: { subSessions: false } }), shown), settings({ options: { subSessions: false } }));
  assert.deepEqual(startRequest(settings({ providerId: "codex", options: { subSessions: false } }), shown).options, { subSessions: false });
});
