import assert from "node:assert/strict";
import test from "node:test";
import { boundedPromptChoices, promptChoicesSignature } from "./PromptChooser";
import { changeSelection } from "./sessionSelection";
import { createNextSendSelectionStore } from "./nextSendSelection";
import type { SessionChoicesResponse } from "#neoastra";

const choices: SessionChoicesResponse = { status: "ok", epoch: "epoch", sessionId: "one",
  current: { providerKey: "literal", agentPromptId: "default", modelId: "a", reasoningEffort: "High" },
  prompts: [{ id: "default", name: "Default" }, { id: "<plan>.*", name: "Settings" }],
  models: [{ id: "a", name: "A", efforts: ["High"], imageInput: true }] };

test("cached prompt choices refuse incomplete, ambiguous and oversized authority", () => {
  assert.equal(boundedPromptChoices(choices), true);
  for (const value of [undefined, { ...choices, status: "unavailable" }, { ...choices, current: null },
    { ...choices, prompts: null }, { ...choices, prompts: [] }, { ...choices, prompts: [null] },
    { ...choices, prompts: Array(129).fill(choices.prompts[0]) }, { ...choices, prompts: [choices.prompts[0], choices.prompts[0]] },
    { ...choices, prompts: [{ id: "x".repeat(257), name: "Name" }] },
    { ...choices, prompts: [{ id: "default", name: "x".repeat(513) }] },
    { ...choices, prompts: [{ id: "default", name: "" }] }, { ...choices, models: null }])
    assert.equal(boundedPromptChoices(value as SessionChoicesResponse | undefined), false);
});

test("literal prompt-only owner changes preserve model/effort and snapshot facts", () => {
  const writes: string[] = [];
  const owner = createNextSendSelectionStore(() => null, (_, value) => writes.push(value));
  const before = promptChoicesSignature(choices);
  const next = changeSelection(choices, choices.current!, "agentPromptId", "<plan>.*")!;
  assert.deepEqual(next, { ...choices.current, agentPromptId: "<plan>.*" });
  assert.equal(owner.set("epoch", "one", choices, next), true);
  assert.equal(owner.current("epoch", "one"), next);
  assert.equal(writes.length, 1);
  assert.equal(promptChoicesSignature(choices), before);
  assert.equal(changeSelection(choices, next, "agentPromptId", "missing"), null);
  assert.notEqual(promptChoicesSignature({ ...choices, current: next }), before);
});
