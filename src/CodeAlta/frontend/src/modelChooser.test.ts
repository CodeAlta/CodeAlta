import assert from "node:assert/strict";
import test from "node:test";
import { boundedModelChoices } from "./ModelChooser";
import type { SessionChoicesResponse } from "#neoastra";

const choices: SessionChoicesResponse = { status: "ok", epoch: "epoch", sessionId: "one",
  current: { providerKey: "literal", agentPromptId: "plan", modelId: "a", reasoningEffort: null },
  prompts: [{ id: "plan", name: "Plan" }], models: [
    { id: "a", name: "Exact A", efforts: ["High"], imageInput: null },
    { id: "b", name: "Exact B", efforts: [], imageInput: false },
  ] };

test("chooser admits only bounded observed eligible choices, without inventing capabilities", () => {
  assert.equal(boundedModelChoices(choices), true);
  assert.equal(boundedModelChoices(undefined), false);
  assert.equal(boundedModelChoices({ ...choices, status: "unavailable" } as SessionChoicesResponse), false);
  assert.equal(boundedModelChoices({ ...choices, models: Array(129).fill(choices.models[0]) }), false);
  assert.equal(boundedModelChoices({ ...choices, models: [choices.models[0], choices.models[0]] }), false);
  assert.equal(boundedModelChoices({ ...choices, models: [] }), false);
  assert.equal(boundedModelChoices({ ...choices, models: [{ ...choices.models[0], id: "x".repeat(257) }] }), false);
  assert.equal(choices.models[0].imageInput, null);
  assert.equal(choices.models[1].imageInput, false);
});
