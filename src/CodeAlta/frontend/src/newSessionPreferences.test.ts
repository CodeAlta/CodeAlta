import assert from "node:assert/strict";
import test from "node:test";
import type { SessionSelection } from "#neoastra";
import { rememberNewSessionPreference, restoreNewSessionPreferences } from "./newSessionPreferences";

const selection: SessionSelection = { providerKey: "codex", agentPromptId: "default", modelId: "model", reasoningEffort: "high" };

test("new-session choices survive reopening the window and stay scoped to each project or Chats", () => {
  let stored = "";
  let preferences = rememberNewSessionPreference(new Map(), "project", selection, value => { stored = value; });
  preferences = rememberNewSessionPreference(preferences, "chats", { ...selection, providerKey: "other", modelId: null, reasoningEffort: null }, value => { stored = value; });
  assert.deepEqual(restoreNewSessionPreferences(() => stored), preferences);
  const next = rememberNewSessionPreference(preferences, "project", { ...selection, modelId: "next" }, () => {});
  assert.equal(next.get("project")?.modelId, "next");
  assert.equal(preferences.get("project")?.modelId, "model", "updates do not mutate earlier snapshots");
  assert.deepEqual(next.get("chats"), preferences.get("chats"));
});

test("unreadable and malformed creation preferences never become valid selections", () => {
  for (const read of [() => null, () => "{", () => "[]", () => { throw new Error("denied"); }, () => "x".repeat(1_000_001)])
    assert.equal(restoreNewSessionPreferences(read).size, 0);
  const invalid = [{}, null, { ...selection, providerKey: " " }, { ...selection, modelId: 42 }, { ...selection, agentPromptId: "\nagent" },
    { ...selection, modelId: null }, { ...selection, reasoningEffort: {} }];
  assert.deepEqual([...restoreNewSessionPreferences(() => JSON.stringify({ valid: selection, ...Object.fromEntries(invalid.map((value, i) => [i, value])) }))], [["valid", selection]]);
});

test("bounded preference history retains recent choices even when storage is unavailable", () => {
  let preferences: ReadonlyMap<string, SessionSelection> = new Map();
  for (let i = 0; i < 260; i++) preferences = rememberNewSessionPreference(preferences, `${i}`, selection, () => { throw new Error("full"); });
  assert.equal(preferences.size, 256);
  assert.equal(preferences.has("0"), false);
  assert.deepEqual(preferences.get("259"), selection);
  assert.equal(restoreNewSessionPreferences(() => JSON.stringify(Object.fromEntries(Array.from({ length: 260 }, (_, i) => [i, selection])))).size, 256);
});
