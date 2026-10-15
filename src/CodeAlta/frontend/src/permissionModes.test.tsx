import assert from "node:assert/strict";
import test from "node:test";
import { renderToStaticMarkup } from "react-dom/server";
import type { SessionChoicesResponse, SessionSelection } from "#neoastra";
import { PermissionModeSelect } from "./PermissionModeSelect";
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

test("a model or an effort the provider no longer lists does not hold back a change of mode", () => {
  // The session holds an effort its model does not list, a model that is gone or a prompt that is gone: the mode
  // goes to the host by itself, and the rest of the selection stays as it was.
  for (const held of [{ ...current, reasoningEffort: "Max" }, { ...current, modelId: "gone" }, { ...current, agentPromptId: "gone" }]) {
    const value = { ...choices, current: held };
    assert.equal(validSelection(value, held), false);
    assert.deepEqual(changeSelection(value, held, "permissionMode", "acceptEdits"), { ...held, permissionMode: "acceptEdits" });
    // The mode is still one the provider offers, or its own.
    assert.equal(changeSelection(value, held, "permissionMode", "plan"), null);
    assert.equal(changeSelection({ ...without, current: held }, held, "permissionMode", "acceptEdits"), null);
    const own = { ...value, current: { ...held, permissionMode: "auto" } };
    assert.deepEqual(changeSelection(own, own.current, "permissionMode", ""), { ...held, permissionMode: providerPermissionMode });
    assert.equal(changeSelection(own, own.current, "permissionMode", "auto")?.permissionMode, null, "the mode it has is no change");
  }
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

test("the composer names the mode the session runs in: its own, else the default one, which is marked", () => {
  const select = (value: string | null, defaultMode: string | null) =>
    renderToStaticMarkup(<PermissionModeSelect id="permission" value={value} modes={modes} defaultMode={defaultMode} onChange={() => {}} />);
  const options = (html: string) => [...html.matchAll(/<option value="([^"]*)"( selected="")?>([^<]*)<\/option>/g)].map(match => `${match[1]}=${match[3]}${match[2] ? " *" : ""}`);
  // No mode of its own: the default one is chosen, under its name marked Default, and the select is not marked.
  const quiet = select(null, "bypassPermissions");
  assert.match(quiet, /aria-label="Permissions: Bypass permissions"/);
  assert.doesNotMatch(quiet, /data-own/);
  assert.deepEqual(options(quiet), ["default=Ask first", "acceptEdits=Accept edits", "auto=Auto", "dontAsk=Don&#x27;t ask", "=Bypass permissions · Default *"]);
  // A mode of its own is chosen and marked; the description of the mode is the tooltip.
  const own = select("default", "bypassPermissions");
  assert.match(own, /data-own="true"/);
  assert.match(own, /title="Always ask before making changes."/);
  assert.deepEqual(options(own).filter(option => option.endsWith("*")), ["default=Ask first *"]);
  // A default a session cannot be given (the plan mode of a provider), and the setting of the CLI when there is none.
  assert.deepEqual(options(select(null, "plan"))[0], "=Plan · Default *");
  assert.deepEqual(options(select(null, null))[0], "=The setting of the CLI · Default *");
  // A saved mode that is no longer offered stays, marked Unverified.
  assert.ok(options(select("plan", "bypassPermissions")).includes("plan=Plan · Unverified *"));
});
