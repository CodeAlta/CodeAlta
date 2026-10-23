import assert from "node:assert/strict";
import test from "node:test";
import { chosenNewSessionPreference, newSessionProvider, newSessionSelection, newSessionTier, newSessionTierProvider, providersInSpace, rememberNewSessionPreference, restoreNewSessionPreferences, tierNewSessionPreference,
  type NewSessionPreference } from "./newSessionPreferences";

const chosen: NewSessionPreference = { providerKey: "codex", modelId: "model", reasoningEffort: "high" };
// A draft whose agent prompt was chosen for the session it starts.
const drafted = { ...chosen, agentPromptId: "plan" };
const providers = [{ id: "codex", isDefault: false, defaultModel: "small", defaultReasoning: "low" }, { id: "claude", isDefault: true, defaultModel: "opus", defaultReasoning: null }];
const models = [{ id: "small", efforts: ["low", "high"], defaultEffort: "low" }, { id: "model", efforts: ["low", "medium", "high"], defaultEffort: "medium" },
  { id: "plain", efforts: [], defaultEffort: null }];
const prompts = [{ id: "default" }, { id: "plan" }];

test("new-session choices survive reopening the window and stay scoped to each project or Chats", () => {
  let stored = "";
  let preferences = rememberNewSessionPreference(new Map(), "project", chosen, value => { stored = value; });
  preferences = rememberNewSessionPreference(preferences, "chats", { providerKey: "other" }, value => { stored = value; });
  assert.deepEqual(restoreNewSessionPreferences(() => stored), preferences);
  const next = rememberNewSessionPreference(preferences, "project", { ...chosen, modelId: "next" }, () => {});
  assert.equal(next.get("project")?.modelId, "next");
  assert.equal(preferences.get("project")?.modelId, "model", "updates do not mutate earlier snapshots");
  assert.deepEqual(next.get("chats"), preferences.get("chats"));
  // A scope with nothing chosen any more is forgotten, not kept as an empty entry.
  const forgotten = rememberNewSessionPreference(next, "chats", {}, value => { stored = value; });
  assert.equal(forgotten.has("chats"), false);
  assert.deepEqual(Object.keys(JSON.parse(stored)), ["project"]);
});

test("unreadable and malformed creation preferences never become valid selections", () => {
  for (const read of [() => null, () => "{", () => "[]", () => { throw new Error("denied"); }, () => "x".repeat(1_000_001)])
    assert.equal(restoreNewSessionPreferences(read).size, 0);
  const invalid = [{}, null, [], { ...chosen, providerKey: " " }, { ...chosen, modelId: 42 }, { ...chosen, tier: "\ntier" },
    { modelId: "model" }, { ...chosen, reasoningEffort: {} }, { unknown: "field" }];
  assert.deepEqual([...restoreNewSessionPreferences(() => JSON.stringify({ valid: chosen, effort: { reasoningEffort: "high", modelId: null, other: 1 },
    ...Object.fromEntries(invalid.map((value, i) => [i, value])) }))].sort(), [["effort", { reasoningEffort: "high" }], ["valid", chosen]]);
});

test("the agent prompt of a draft is never remembered for the next new sessions", () => {
  // What an earlier version stored: the agent prompt is dropped, and a scope where it was the only choice is forgotten.
  const restored = restoreNewSessionPreferences(() => JSON.stringify({ project: drafted, chats: { agentPromptId: "plan" } }));
  assert.deepEqual([...restored], [["project", chosen]]);
  // Another choice made with an agent prompt in the draft does not carry the agent prompt.
  assert.equal("agentPromptId" in chosenNewSessionPreference(drafted, "codex", "reasoningEffort", "low"), false);
  assert.equal("agentPromptId" in chosenNewSessionPreference(drafted, "codex", "modelId", "small"), false);
  assert.equal("agentPromptId" in chosenNewSessionPreference(drafted, "codex", "providerKey", "claude"), false);
  assert.equal("agentPromptId" in tierNewSessionPreference(drafted, "quick", { providerKey: "codex", modelId: "small", reasoning: "low" }), false);
  // Without a choice in the draft, the next session starts with the default agent prompt.
  assert.equal(newSessionSelection(restored.get("project"), "codex", prompts, models, providers[0]).agentPromptId, "default");
});

test("bounded preference history retains recent choices even when storage is unavailable", () => {
  let preferences: ReadonlyMap<string, NewSessionPreference> = new Map();
  for (let i = 0; i < 260; i++) preferences = rememberNewSessionPreference(preferences, `${i}`, chosen, () => { throw new Error("full"); });
  assert.equal(preferences.size, 256);
  assert.equal(preferences.has("0"), false);
  assert.deepEqual(preferences.get("259"), chosen);
  assert.equal(restoreNewSessionPreferences(() => JSON.stringify(Object.fromEntries(Array.from({ length: 260 }, (_, i) => [i, chosen])))).size, 256);
});

test("only what the user changes is remembered, with what it stands on", () => {
  // The effort alone: the provider and the model go on following the settings.
  assert.deepEqual(chosenNewSessionPreference(undefined, "claude", "reasoningEffort", "high"), { reasoningEffort: "high" });
  // A model is one of the provider in use, and starts from its own effort.
  assert.deepEqual(chosenNewSessionPreference({ reasoningEffort: "high" }, "claude", "modelId", "opus"), { providerKey: "claude", modelId: "opus" });
  // Another provider starts from its default model.
  assert.deepEqual(chosenNewSessionPreference(chosen, "codex", "providerKey", "claude"), { providerKey: "claude" });
  assert.deepEqual(chosenNewSessionPreference(chosen, "codex", "reasoningEffort", "low"), { ...chosen, reasoningEffort: "low" });
  // An empty choice is forgotten; the others stay.
  assert.deepEqual(chosenNewSessionPreference(chosen, "codex", "reasoningEffort", ""), { providerKey: "codex", modelId: "model" });
  assert.deepEqual(chosenNewSessionPreference(chosen, "codex", "modelId", ""), { providerKey: "codex" });
  assert.deepEqual(chosenNewSessionPreference({ reasoningEffort: "high" }, "codex", "reasoningEffort", ""), {});
});

test("the permission mode of a new session is remembered with the provider in use, as the mode of a session", () => {
  const moded: NewSessionPreference = { ...chosen, permissionMode: "bypassPermissions" };
  assert.deepEqual(chosenNewSessionPreference(chosen, "codex", "permissionMode", "bypassPermissions"), moded);
  assert.deepEqual(chosenNewSessionPreference(undefined, "claude", "permissionMode", "acceptEdits"), { permissionMode: "acceptEdits" });
  // Another model or effort keeps the mode; another provider has modes of its own and starts from its default one.
  assert.equal(chosenNewSessionPreference(moded, "codex", "modelId", "small").permissionMode, "bypassPermissions");
  assert.equal(chosenNewSessionPreference(moded, "codex", "modelId", "").permissionMode, "bypassPermissions");
  assert.equal(chosenNewSessionPreference(moded, "codex", "reasoningEffort", "low").permissionMode, "bypassPermissions");
  assert.equal(chosenNewSessionPreference(moded, "codex", "providerKey", "claude").permissionMode, undefined);
  // The default mode, chosen again, is no mode of its own.
  assert.deepEqual(chosenNewSessionPreference(moded, "codex", "permissionMode", ""), chosen);
  // A tier keeps it while it keeps the provider in use.
  assert.equal(tierNewSessionPreference(moded, "quick", { providerKey: "codex", modelId: "small", reasoning: "low" }, "codex").permissionMode, "bypassPermissions");
  assert.equal(tierNewSessionPreference(moded, "quick", { providerKey: "claude", modelId: "opus", reasoning: null }, "codex").permissionMode, undefined);
  // It is read back with the rest.
  assert.deepEqual(restoreNewSessionPreferences(() => JSON.stringify({ scope: moded })).get("scope"), moded);
});

test("a new session of a space is offered the providers of that space only, and starts with one of them", () => {
  const listed = [{ id: "claude-business", isDefault: true }, { id: "codex", isDefault: false }, { id: "claude-home", isDefault: false }];
  // The spaces as the window knows them now: a change of them is offered at once.
  let assigned: Record<string, readonly string[]> = { "claude-business": ["business"], "claude-home": ["default", "personal"] };
  const spacesOf = (key: string) => assigned[key] ?? [];
  assert.deepEqual(providersInSpace(listed, "business", spacesOf).map(provider => provider.id), ["claude-business", "codex"]);
  assert.deepEqual(providersInSpace(listed, null, spacesOf).map(provider => provider.id), ["codex", "claude-home"], "no space shown is the default space");
  assert.deepEqual(providersInSpace(listed, "personal", spacesOf).map(provider => provider.id), ["codex", "claude-home"]);
  // The default provider of the settings is in another space: the first provider of this one.
  assert.equal(newSessionProvider(undefined, providersInSpace(listed, "personal", spacesOf)), "codex");
  // What was chosen in another space is not taken here.
  assert.equal(newSessionProvider({ providerKey: "claude-business" }, providersInSpace(listed, "personal", spacesOf)), "codex");
  assigned = { ...assigned, codex: ["default"] };
  assert.deepEqual(providersInSpace(listed, "business", spacesOf).map(provider => provider.id), ["claude-business"]);
  // A host without spaces filters nothing.
  assert.equal(providersInSpace(listed, "business", null).length, 3);
});

test("a new session starts from the defaults of the settings until something was chosen, and follows them when they change", () => {
  assert.equal(newSessionProvider(undefined, providers), "claude");
  assert.equal(newSessionProvider(undefined, [providers[0]]), "codex", "without a default provider, the first enabled one");
  assert.equal(newSessionProvider(undefined, []), "");
  assert.deepEqual(newSessionSelection(undefined, "codex", prompts, models, providers[0]), { providerKey: "codex", agentPromptId: "default", modelId: "small", reasoningEffort: "low" });
  // Nothing was stored by looking: another default model in Settings is the one of the next session.
  assert.deepEqual(newSessionSelection(undefined, "codex", prompts, models, { defaultModel: "model", defaultReasoning: "medium" }),
    { providerKey: "codex", agentPromptId: "default", modelId: "model", reasoningEffort: "medium" });
  // An agent that was chosen leaves the model to the settings.
  assert.deepEqual(newSessionSelection({ agentPromptId: "plan" }, "codex", prompts, models, { defaultModel: "model", defaultReasoning: null }),
    { providerKey: "codex", agentPromptId: "plan", modelId: "model", reasoningEffort: "high" });
});

test("a new session starts with the agent prompt of its space, unless the user chose one", () => {
  assert.equal(newSessionSelection(undefined, "codex", prompts, models, providers[0], null, "plan").agentPromptId, "plan");
  assert.equal(newSessionSelection({ agentPromptId: "default" }, "codex", prompts, models, providers[0], null, "plan").agentPromptId, "default",
    "the choice of the user wins");
  assert.equal(newSessionSelection(undefined, "codex", [prompts[0]], models, providers[0], null, "plan").agentPromptId, "default",
    "an agent the space no longer has");
});

test("what was chosen is used only while the catalogs offer it", () => {
  assert.equal(newSessionProvider(chosen, providers), "codex");
  assert.equal(newSessionProvider({ providerKey: "gone" }, providers), "claude", "a provider that is disabled or removed");
  assert.deepEqual(newSessionSelection(drafted, "codex", prompts, models, providers[0]), { providerKey: "codex", agentPromptId: "plan", modelId: "model", reasoningEffort: "high" });
  // The model is gone: the default model of the provider, with the chosen effort while that model has it.
  assert.deepEqual(newSessionSelection(drafted, "codex", prompts, [models[0], models[2]], providers[0]),
    { providerKey: "codex", agentPromptId: "plan", modelId: "small", reasoningEffort: "high" });
  // The effort is gone from the model, the agent from the prompts.
  assert.deepEqual(newSessionSelection({ ...drafted, reasoningEffort: "max" }, "codex", [prompts[0]], models, providers[0]),
    { providerKey: "codex", agentPromptId: "default", modelId: "model", reasoningEffort: "low" });
  // A model chosen in another provider says nothing about this one.
  assert.deepEqual(newSessionSelection(drafted, "claude", prompts, [{ id: "model", efforts: [], defaultEffort: null }, { id: "opus", efforts: [], defaultEffort: null }], providers[1]),
    { providerKey: "claude", agentPromptId: "plan", modelId: "opus", reasoningEffort: null });
  // While the catalogs are read, nothing is offered yet.
  assert.deepEqual(newSessionSelection(drafted, "codex", [], [], providers[0]), { providerKey: "codex", agentPromptId: "default", modelId: null, reasoningEffort: null });
});

test("with tiers, a new session the user chose nothing for starts on the model of the standard tier", () => {
  const standard = { providerKey: "codex", modelId: "model", reasoning: "medium" };
  assert.equal(newSessionTierProvider(undefined, providers, standard), "codex");
  assert.equal(newSessionTierProvider({ providerKey: "claude" }, providers, standard), null, "A provider the user chose comes first.");
  assert.equal(newSessionTierProvider(undefined, providers, { ...standard, providerKey: "off" }), null, "A provider that is not enabled is not taken.");
  assert.equal(newSessionTierProvider(undefined, providers, null), null);
  assert.deepEqual(newSessionSelection(undefined, "codex", prompts, models, providers[0], standard),
    { providerKey: "codex", agentPromptId: "default", modelId: "model", reasoningEffort: "medium" });
  // A model the user chose for the provider stays; a tier model the provider does not list gives the default.
  assert.equal(newSessionSelection({ providerKey: "codex", modelId: "small" }, "codex", prompts, models, providers[0], standard).modelId, "small");
  assert.equal(newSessionSelection(undefined, "codex", prompts, models, providers[0], { ...standard, modelId: "gone" }).modelId, "small");
});

test("a tier chosen for new sessions is remembered with its model, kept with a new effort and dropped with another model", () => {
  const chosenTier = tierNewSessionPreference({ providerKey: "claude" }, "quick", { providerKey: "codex", modelId: "small", reasoning: "low" });
  assert.deepEqual(chosenTier, { providerKey: "codex", modelId: "small", reasoningEffort: "low", tier: "quick" });
  let stored = "";
  const remembered = rememberNewSessionPreference(new Map(), "scope", chosenTier, value => { stored = value; });
  assert.deepEqual(restoreNewSessionPreferences(() => stored).get("scope"), chosenTier);
  assert.equal(chosenNewSessionPreference(remembered.get("scope"), "codex", "reasoningEffort", "high").tier, "quick");
  assert.equal(chosenNewSessionPreference(remembered.get("scope"), "codex", "modelId", "model").tier, undefined);
  assert.equal(chosenNewSessionPreference(remembered.get("scope"), "codex", "providerKey", "claude").tier, undefined);
  assert.deepEqual(tierNewSessionPreference(undefined, "complex", { providerKey: "claude", modelId: "opus", reasoning: null }), { providerKey: "claude", modelId: "opus", tier: "complex" });
});

test("a new session is on a tier only while that tier gives its model", () => {
  const choices = { complex: null, standard: { providerKey: "codex", modelId: "model", reasoning: "medium" }, quick: null };
  const chosen = { providerKey: "codex", modelId: "model", tier: "standard" };
  assert.equal(newSessionTier(chosen, { providerKey: "codex", modelId: "model" }, choices), "standard");
  // In another space, or once the tiers changed, the tier gives another model: the model chosen stays, without the tier.
  assert.equal(newSessionTier(chosen, { providerKey: "codex", modelId: "model" }, { ...choices, standard: { providerKey: "claude", modelId: "haiku" } }), null);
  assert.equal(newSessionTier(chosen, { providerKey: "codex", modelId: "small" }, choices), null);
  // A user who chose nothing starts on the standard tier when the session starts with its model.
  assert.equal(newSessionTier(undefined, { providerKey: "codex", modelId: "model" }, choices), "standard");
  assert.equal(newSessionTier(undefined, { providerKey: "codex", modelId: "small" }, choices), null);
  assert.equal(newSessionTier({ providerKey: "codex" }, { providerKey: "codex", modelId: "model" }, choices), null, "A provider the user chose is not a tier.");
  assert.equal(newSessionTier(chosen, { providerKey: "codex", modelId: "model" }, null), null, "Without tiers there is no tier.");
});
