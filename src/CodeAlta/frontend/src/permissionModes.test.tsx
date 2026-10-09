import assert from "node:assert/strict";
import test from "node:test";
import { renderToStaticMarkup } from "react-dom/server";
import type { SessionChoicesResponse, SessionSelection } from "#neoastra";
import { PermissionChipPart, PermissionModeSelect } from "./PermissionModeSelect";
import { chosenPermissionMode, effectivePermissionMode, offeredPermissionModes, providerPermissionMode } from "./permissionModes";
import { captureSubmission } from "./sessionOperations";
import { changeSelection, completeSelection, restoreSelection, validSelection } from "./sessionSelection";

const current: SessionSelection = { providerKey: "claude", agentPromptId: "default", modelId: "one", reasoningEffort: "High" };
const modes = [{ id: "default", skipsReview: false }, { id: "acceptEdits", skipsReview: true }, { id: "auto", skipsReview: true },
  { id: "dontAsk", skipsReview: true }, { id: "bypassPermissions", skipsReview: true }];
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

test("the mode shown is the chosen one, else the provider's, and says whether the review is skipped", () => {
  assert.equal(chosenPermissionMode(choices, current), null);
  assert.deepEqual(effectivePermissionMode(choices, current), { id: "plan", chosen: false, skipsReview: false });
  assert.deepEqual(effectivePermissionMode(choices, { ...current, permissionMode: "bypassPermissions" }), { id: "bypassPermissions", chosen: true, skipsReview: true });
  const overridden = { ...choices, current: { ...current, permissionMode: "acceptEdits" } };
  assert.equal(chosenPermissionMode(overridden, overridden.current), "acceptEdits");
  assert.equal(chosenPermissionMode(overridden, { ...current, permissionMode: providerPermissionMode }), null);
  assert.deepEqual(effectivePermissionMode({ ...choices, defaultPermissionMode: "dontAsk" }, current), { id: "dontAsk", chosen: false, skipsReview: true });
});

test("a Send carries the chosen mode, and leaves the field out when the session keeps its own", () => {
  const sent = captureSubmission("epoch", "session", "text", "key", { ...current, permissionMode: providerPermissionMode })!;
  assert.equal(sent.selection?.permissionMode, providerPermissionMode);
  assert.ok(Object.isFrozen(sent.selection));
  assert.equal("permissionMode" in captureSubmission("epoch", "session", "text", "key", current)!.selection!, false);
  assert.equal("permissionMode" in captureSubmission("epoch", "session", "text", "key", { ...current, permissionMode: null })!.selection!, false);
  assert.equal(captureSubmission("epoch", "session", "text", "key", { ...current, permissionMode: " auto" }), null);
});

test("the composer offers the provider's setting first, then the modes by name, and warns of those without the review", () => {
  const html = renderToStaticMarkup(<PermissionModeSelect id="permission" value="dontAsk" modes={modes} providerMode="plan" onChange={() => {}} />);
  assert.match(html, /aria-label="Permissions"/);
  assert.match(html, /<option value="" title="plan">Provider setting \(Plan\)<\/option>/);
  for (const name of ["Default", "⚠ Accept edits", "⚠ Auto", "⚠ Don&#x27;t ask", "⚠ Bypass permissions"]) assert.ok(html.includes(`>${name}</option>`), name);
  assert.match(html, /data-skips-review="true"/);
  assert.match(html, /refuses the rest, without CodeAlta&#x27;s review\. <code>dontAsk<\/code>/);
  // The setting of the CLI, when the provider names no mode; its default mode skips nothing.
  const cli = renderToStaticMarkup(<PermissionModeSelect id="permission" value={null} modes={modes} providerMode={null} onChange={() => {}} />);
  assert.match(cli, /<option value="" selected="">The setting of the CLI<\/option>/);
  assert.doesNotMatch(cli, /data-skips-review/);

  const chip = (permission: { id: string; skipsReview: boolean }) => renderToStaticMarkup(<PermissionChipPart {...permission} />);
  assert.match(chip({ id: "acceptEdits", skipsReview: true }), /composer-permission-part" data-skips-review="true" title="Claude Code runs requests without CodeAlta&#x27;s review in this mode\.">.*Accept edits/);
  assert.doesNotMatch(chip({ id: "default", skipsReview: false }), /data-skips-review/);
});
