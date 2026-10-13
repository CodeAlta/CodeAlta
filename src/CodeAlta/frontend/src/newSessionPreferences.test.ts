import assert from "node:assert/strict";
import test from "node:test";
import { chosenNewSessionPreference, newSessionProvider, newSessionSelection, rememberNewSessionPreference, restoreNewSessionPreferences,
  type NewSessionPreference } from "./newSessionPreferences";

const chosen: NewSessionPreference = { providerKey: "codex", agentPromptId: "plan", modelId: "model", reasoningEffort: "high" };
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
  const invalid = [{}, null, [], { ...chosen, providerKey: " " }, { ...chosen, modelId: 42 }, { ...chosen, agentPromptId: "\nagent" },
    { agentPromptId: "plan", modelId: "model" }, { ...chosen, reasoningEffort: {} }, { unknown: "field" }];
  assert.deepEqual([...restoreNewSessionPreferences(() => JSON.stringify({ valid: chosen, agent: { agentPromptId: "plan", modelId: null, other: 1 },
    ...Object.fromEntries(invalid.map((value, i) => [i, value])) }))].sort(), [["agent", { agentPromptId: "plan" }], ["valid", chosen]]);
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
  // The agent alone: the provider and the model go on following the settings.
  assert.deepEqual(chosenNewSessionPreference(undefined, "claude", "agentPromptId", "plan"), { agentPromptId: "plan" });
  assert.deepEqual(chosenNewSessionPreference(undefined, "claude", "reasoningEffort", "high"), { reasoningEffort: "high" });
  // A model is one of the provider in use, and starts from its own effort.
  assert.deepEqual(chosenNewSessionPreference({ agentPromptId: "plan", reasoningEffort: "high" }, "claude", "modelId", "opus"),
    { agentPromptId: "plan", providerKey: "claude", modelId: "opus" });
  // Another provider starts from its default model.
  assert.deepEqual(chosenNewSessionPreference(chosen, "codex", "providerKey", "claude"), { agentPromptId: "plan", providerKey: "claude" });
  assert.deepEqual(chosenNewSessionPreference(chosen, "codex", "reasoningEffort", "low"), { ...chosen, reasoningEffort: "low" });
  // An empty choice is forgotten; the others stay.
  assert.deepEqual(chosenNewSessionPreference(chosen, "codex", "agentPromptId", ""), { providerKey: "codex", modelId: "model", reasoningEffort: "high" });
  assert.deepEqual(chosenNewSessionPreference(chosen, "codex", "modelId", ""), { agentPromptId: "plan", providerKey: "codex" });
  assert.deepEqual(chosenNewSessionPreference({ agentPromptId: "plan" }, "codex", "agentPromptId", ""), {});
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

test("what was chosen is used only while the catalogs offer it", () => {
  assert.equal(newSessionProvider(chosen, providers), "codex");
  assert.equal(newSessionProvider({ providerKey: "gone" }, providers), "claude", "a provider that is disabled or removed");
  assert.deepEqual(newSessionSelection(chosen, "codex", prompts, models, providers[0]), { providerKey: "codex", agentPromptId: "plan", modelId: "model", reasoningEffort: "high" });
  // The model is gone: the default model of the provider, with the chosen effort while that model has it.
  assert.deepEqual(newSessionSelection(chosen, "codex", prompts, [models[0], models[2]], providers[0]),
    { providerKey: "codex", agentPromptId: "plan", modelId: "small", reasoningEffort: "high" });
  // The effort is gone from the model, the agent from the prompts.
  assert.deepEqual(newSessionSelection({ ...chosen, reasoningEffort: "max" }, "codex", [prompts[0]], models, providers[0]),
    { providerKey: "codex", agentPromptId: "default", modelId: "model", reasoningEffort: "low" });
  // A model chosen in another provider says nothing about this one.
  assert.deepEqual(newSessionSelection(chosen, "claude", prompts, [{ id: "model", efforts: [], defaultEffort: null }, { id: "opus", efforts: [], defaultEffort: null }], providers[1]),
    { providerKey: "claude", agentPromptId: "plan", modelId: "opus", reasoningEffort: null });
  // While the catalogs are read, nothing is offered yet.
  assert.deepEqual(newSessionSelection(chosen, "codex", [], [], providers[0]), { providerKey: "codex", agentPromptId: "default", modelId: null, reasoningEffort: null });
});
