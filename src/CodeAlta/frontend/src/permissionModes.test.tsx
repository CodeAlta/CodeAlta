import assert from "node:assert/strict";
import test from "node:test";
import { renderToStaticMarkup } from "react-dom/server";
import type { SessionChoicesResponse, SessionSelection } from "#neoastra";
import { PermissionModeMenu } from "./PermissionModeMenu";
import { chosenPermissionMode, offeredPermissionModes, providerPermissionMode } from "./permissionModes";
import { captureSubmission } from "./sessionOperations";
import { changeSelection, completeSelection, restoreSelection, validSelection } from "./sessionSelection";

const current: SessionSelection = { providerKey: "claude", agentPromptId: "default", modelId: "one", reasoningEffort: "High" };
const modes = [{ id: "default" }, { id: "acceptEdits" }, { id: "auto" }, { id: "dontAsk" }, { id: "bypassPermissions" }];
const choices: SessionChoicesResponse = { status: "ok", epoch: "epoch", sessionId: "session", current,
  prompts: [{ id: "default", name: "Default" }],
  models: [{ id: "one", name: "One", efforts: ["High"], imageInput: null, startEffort: "High" },
    { id: "two", name: "Two", efforts: ["Low"], imageInput: null, startEffort: "Low" }],
  permissionModes: modes, defaultPermissionMode: "plan" };
// What a provider without modes reports, and what a host before the modes sent.
const without: SessionChoicesResponse = { ...choices, permissionModes: [], defaultPermissionMode: null };
const older: SessionChoicesResponse = { status: "ok", epoch: "epoch", sessionId: "session", current, prompts: choices.prompts, models: choices.models };

test("a mode is one the provider offers, the provider's own, or none, which keeps the one of the session", () => {
  assert.equal(validSelection(choices, current), true);
  assert.equal(validSelection(choices, { ...current, permissionMode: "acceptEdits" }), true);
  assert.equal(validSelection(choices, { ...current, permissionMode: providerPermissionMode }), true);
  assert.equal(validSelection(choices, { ...current, permissionMode: "plan" }), false, "Plan stays the provider's.");
  assert.equal(validSelection(choices, { ...current, permissionMode: "unknown" }), false);
  // A provider without modes hides the choice: nothing is sent.
  for (const value of [without, older]) {
    assert.deepEqual(offeredPermissionModes(value), []);
    assert.equal(validSelection(value, current), true);
    assert.equal(validSelection(value, { ...current, permissionMode: "acceptEdits" }), false);
    assert.equal(validSelection(value, { ...current, permissionMode: providerPermissionMode }), false);
  }
});

test("a selection stored without a mode, or with one the provider no longer offers, is restored without it", () => {
  assert.deepEqual(restoreSelection(() => JSON.stringify(current), choices), current);
  assert.equal(restoreSelection(() => JSON.stringify(current), choices)?.permissionMode, undefined);
  assert.deepEqual(restoreSelection(() => JSON.stringify({ ...current, permissionMode: "auto" }), choices), { ...current, permissionMode: "auto" });
  // The model is kept: only the mode is forgotten.
  assert.deepEqual(restoreSelection(() => JSON.stringify({ ...current, modelId: "two", reasoningEffort: "Low", permissionMode: "auto" }), without),
    { ...current, modelId: "two", reasoningEffort: "Low", permissionMode: null });
  const kept = { ...current, permissionMode: "dontAsk" };
  assert.equal(completeSelection(choices, kept), kept, "An unchanged selection stays the same object.");
});

test("the mode belongs to the session: another model or prompt keeps it, and the provider's is chosen by name", () => {
  const chosen = changeSelection(choices, current, "permissionMode", "acceptEdits")!;
  assert.equal(chosen.permissionMode, "acceptEdits");
  assert.equal(changeSelection(choices, chosen, "modelId", "two")?.permissionMode, "acceptEdits");
  assert.equal(changeSelection(choices, chosen, "reasoningEffort", "High")?.permissionMode, "acceptEdits");
  // Back to the provider's: nothing to change for a session without a mode of its own.
  assert.equal(changeSelection(choices, chosen, "permissionMode", "")?.permissionMode, null);
  // A session that has one is sent "provider"; choosing the mode it already has sends nothing.
  const overridden = { ...choices, current: { ...current, permissionMode: "auto" } };
  assert.equal(changeSelection(overridden, overridden.current, "permissionMode", "")?.permissionMode, providerPermissionMode);
  assert.equal(changeSelection(overridden, overridden.current, "permissionMode", "auto")?.permissionMode, null);
  assert.equal(changeSelection(overridden, overridden.current, "permissionMode", "plan"), null);
});

test("a saved mode the provider no longer offers does not hold back another change", () => {
  // The session keeps it: the change sends no mode. A provider without modes has no field to choose another.
  for (const value of [{ ...choices, current: { ...current, permissionMode: "removed" } }, { ...without, current: { ...current, permissionMode: "auto" } }]) {
    const changed = changeSelection(value, value.current, "modelId", "two");
    assert.deepEqual(changed, { ...current, modelId: "two", reasoningEffort: "Low", permissionMode: null });
    assert.equal(changeSelection(value, value.current, "agentPromptId", "default")?.permissionMode, null);
  }
});

test("the mode chosen for a session is its own, the one a Send names, or none for the default", () => {
  assert.equal(chosenPermissionMode(choices, current), null);
  assert.equal(chosenPermissionMode(choices, { ...current, permissionMode: "bypassPermissions" }), "bypassPermissions");
  const overridden = { ...choices, current: { ...current, permissionMode: "acceptEdits" } };
  assert.equal(chosenPermissionMode(overridden, overridden.current), "acceptEdits");
  assert.equal(chosenPermissionMode(overridden, { ...current, permissionMode: providerPermissionMode }), null);
});

test("a Send carries the chosen mode, and leaves the field out when the session keeps its own", () => {
  const sent = captureSubmission("epoch", "session", "text", "key", { ...current, permissionMode: providerPermissionMode })!;
  assert.equal(sent.selection?.permissionMode, providerPermissionMode);
  assert.ok(Object.isFrozen(sent.selection));
  assert.equal("permissionMode" in captureSubmission("epoch", "session", "text", "key", current)!.selection!, false);
  assert.equal("permissionMode" in captureSubmission("epoch", "session", "text", "key", { ...current, permissionMode: null })!.selection!, false);
  assert.equal(captureSubmission("epoch", "session", "text", "key", { ...current, permissionMode: " auto" }), null);
});

test("the composer names the mode the session runs in: its own, else the default one, quietly", () => {
  const button = (value: string | null, defaultMode: string | null) =>
    renderToStaticMarkup(<PermissionModeMenu id="permission" value={value} modes={modes} defaultMode={defaultMode} onChange={() => {}} />);
  // No mode of its own: the default one is named, and the button is not marked.
  const quiet = button(null, "bypassPermissions");
  assert.match(quiet, /aria-label="Permissions: Bypass permissions"/);
  assert.match(quiet, /composer-permission-name">Bypass permissions</);
  assert.doesNotMatch(quiet, /data-own/);
  // A mode of its own is named and marked.
  const own = button("default", "bypassPermissions");
  assert.match(own, /data-own="true"/);
  assert.match(own, /composer-permission-name">Ask first</);
  // A default a session cannot be given (the plan mode of a provider), and the setting of the CLI when there is none.
  assert.match(button(null, "plan"), /composer-permission-name">Plan</);
  assert.match(button(null, null), /composer-permission-name">The setting of the CLI</);
});
