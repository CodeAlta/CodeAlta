import assert from "node:assert/strict";
import test from "node:test";
import { composerCommandBlock, composerControlCommands, composerControlState, isComposerControlCommand } from "./composerCommands";
import { translate } from "./localization";

const t = (key: Parameters<typeof translate>[1]) => translate("en", key);

test("a composer command is offered exactly when its control would act, and otherwise says why in the words of the control", () => {
  for (const command of composerControlCommands) {
    assert.equal(composerCommandBlock(command, { disabled: false, reason: null }, t), null);
    // A reason the control keeps for a time it is enabled says nothing: the control acts.
    assert.equal(composerCommandBlock(command, { disabled: false, reason: "stale" }, t), null);
    assert.equal(composerCommandBlock(command, { disabled: true, reason: "Available once the session is idle." }, t), "Available once the session is idle.");
    assert.equal(composerCommandBlock(command, { disabled: true, reason: null }, t), "Not available in this session now.");
  }
  // The composer shows Stop only while a turn runs and Clear queue only while prompts wait.
  assert.equal(composerCommandBlock("abort", null, t), "No turn is running.");
  assert.equal(composerCommandBlock("clearQueue", null, t), "No prompt is queued.");
  assert.equal(composerCommandBlock("compact", null, t), "Not available in this session now.");
  assert.equal(composerCommandBlock("send", null, t), "Not available in this session now.");
  assert.equal(composerCommandBlock("abort", null, key => translate("fr", key)), "Aucun tour n’est en cours.");
});

test("a control is read as the composer renders it", () => {
  assert.equal(composerControlState(null), null);
  assert.equal(composerControlState(undefined), null);
  assert.deepEqual(composerControlState({ disabled: true, dataset: { unavailable: "Reload required." } }), { disabled: true, reason: "Reload required." });
  assert.deepEqual(composerControlState({ disabled: true, dataset: { unavailable: "" } }), { disabled: true, reason: null });
  assert.deepEqual(composerControlState({ disabled: false, dataset: {} }), { disabled: false, reason: null });
});

test("only the commands a composer control carries out follow a control", () => {
  for (const command of ["send", "abort", "clearQueue", "compact"] as const) assert.equal(isComposerControlCommand(command), true);
  for (const command of ["steer", "nextPrompt", "fork", "rewind", "usage", "help"] as const) assert.equal(isComposerControlCommand(command), false);
});
