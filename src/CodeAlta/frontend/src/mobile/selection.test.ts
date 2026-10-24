import assert from "node:assert/strict";
import test from "node:test";
import type { Session, SessionChoices } from "./contract";
import { appliedSelection, chipText, effortFor, isPending, modeDescription, modeName, pickedChanges, pickSessionModel, providerPermissionMode, shownSelection } from "./selection";

const choices: SessionChoices = {
  current: { provider: "codex", providerName: "Codex", model: "gpt-6.1-sol", modelName: "GPT-6.1 Sol", reasoning: "low", agent: "default", agentName: "Default", permissionMode: null },
  models: [
    { id: "gpt-6.1-sol", name: "GPT-6.1 Sol", efforts: ["low", "medium", "high"], startEffort: "medium" },
    { id: "gpt-6.1-argon", name: "GPT-6.1 Argon", efforts: ["medium", "high"], startEffort: "high" },
    { id: "gpt-5-mini", name: "GPT-5 mini", efforts: [], startEffort: null },
  ],
  prompts: [{ id: "default", name: "Default" }, { id: "plan", name: "Plan" }],
  permissionModes: [{ id: "default", name: "Ask first" }, { id: "acceptEdits" }],
  error: null,
};
const session = {
  id: "s1", title: "Fix", projectId: "p1", project: "parser", summary: null, lastActivity: null, live: null,
  model: { provider: "codex", providerName: "Codex", id: "gpt-6.1-sol", name: "GPT-6.1 Sol", reasoning: "low" }, agent: { id: "plan", name: "Plan" },
} satisfies Session;

test("the chip says the model and its effort, and the agent when it is not the default one", () => {
  assert.deepEqual(chipText(shownSelection(null, choices)), { model: "GPT-6.1 Sol · low", agent: null });
  // Before the choices are read, what the list says of the session.
  assert.deepEqual(chipText(shownSelection(session, null)), { model: "GPT-6.1 Sol · low", agent: "Plan" });
  assert.deepEqual(chipText(shownSelection(session, choices, { model: "gpt-5-mini" })), { model: "GPT-5 mini", agent: null }, "a model without efforts shows none");
});

test("a model picked keeps the effort shown when it has it, else takes its start effort", () => {
  const shown = shownSelection(session, choices);
  assert.deepEqual(pickSessionModel(choices, {}, shown, "gpt-6.1-argon"), { model: "gpt-6.1-argon", reasoning: "high" });
  assert.deepEqual(pickSessionModel(choices, { reasoning: "medium" }, shownSelection(session, choices, { reasoning: "medium" }), "gpt-6.1-argon"), { model: "gpt-6.1-argon", reasoning: "medium" });
  assert.deepEqual(pickSessionModel(choices, {}, shown, "gpt-5-mini"), { model: "gpt-5-mini", reasoning: undefined });
  assert.equal(effortFor(undefined, "low"), undefined);
});

test("a send carries what differs from what the session runs on, and nothing else", () => {
  assert.deepEqual(pickedChanges(choices, {}), {});
  assert.deepEqual(pickedChanges(choices, { model: "gpt-6.1-sol", reasoning: "low", agent: "default" }), {}, "picking what it has is no change");
  assert.deepEqual(pickedChanges(choices, { reasoning: "high" }), { reasoning: "high" });
  assert.deepEqual(pickedChanges(choices, { model: "gpt-6.1-argon", reasoning: "medium" }), { model: "gpt-6.1-argon", reasoning: "medium" });
  assert.deepEqual(pickedChanges(choices, { agent: "plan", permissionMode: "acceptEdits" }), { agent: "plan", permissionMode: "acceptEdits" });
  assert.equal(isPending(choices, { reasoning: "low" }), false);
  assert.equal(isPending(choices, { reasoning: "high" }), true);
});

test("once sent, the session runs on what the send carried, until its choices are read again", () => {
  const current = appliedSelection(choices, { model: "gpt-6.1-argon", reasoning: "high", agent: "plan", permissionMode: "acceptEdits" });
  assert.deepEqual(current, { ...choices.current, model: "gpt-6.1-argon", modelName: "GPT-6.1 Argon", reasoning: "high", agent: "plan", agentName: "Plan", permissionMode: "acceptEdits" });
  assert.equal(appliedSelection(choices, { reasoning: "high" }).reasoning, "high");
  assert.equal(appliedSelection({ ...choices, current }, { permissionMode: providerPermissionMode }).permissionMode, null);
});

test("the provider's mode is sent only to a session that has a mode of its own", () => {
  assert.deepEqual(pickedChanges(choices, { permissionMode: providerPermissionMode }), {});
  const own = { ...choices, current: { ...choices.current, permissionMode: "acceptEdits" } };
  assert.deepEqual(pickedChanges(own, { permissionMode: providerPermissionMode }), { permissionMode: providerPermissionMode });
  assert.equal(shownSelection(null, own, { permissionMode: providerPermissionMode }).permissionMode, null);
  assert.equal(shownSelection(null, own).permissionMode, "acceptEdits");
});

test("modes are named as alta names them, else as the desktop does", () => {
  assert.equal(modeName("default", choices.permissionModes), "Ask first");
  assert.equal(modeName("acceptEdits", choices.permissionModes), "Accept edits");
  assert.equal(modeName("custom"), "custom");
  assert.equal(modeName(null), "The provider's setting");
  assert.equal(modeDescription("bypassPermissions"), "Accept all permissions.");
  assert.equal(modeDescription("custom"), "");
});
