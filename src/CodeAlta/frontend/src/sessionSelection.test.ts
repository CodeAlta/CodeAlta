import assert from "node:assert/strict";
import test from "node:test";
import { changeSelection, restoreSelection, validSelection } from "./sessionSelection";
import { captureSubmission, createMutationCapability, createOwnedSubmissions } from "./sessionOperations";
import type { SessionChoicesResponse, SessionSelection, SessionSendRequest } from "#neoastra";

const current: SessionSelection = { providerKey: "provider", agentPromptId: "default", modelId: "one", reasoningEffort: "High" };
const choices: SessionChoicesResponse = { status: "ok", epoch: "epoch", sessionId: "session", current,
  prompts: [{ id: "default", name: "Default" }, { id: "plan", name: "Plan" }],
  models: [{ id: "one", name: "One", efforts: ["High"] }, { id: "two", name: "Two", efforts: ["Low"] }] };

test("model selection resets reasoning; prompt and supported effort changes are functional", () => {
  const changed = changeSelection(choices, current, "modelId", "two")!;
  assert.equal(changed.modelId, "two");
  assert.equal(changed.reasoningEffort, null);
  assert.equal(changeSelection(choices, changed, "reasoningEffort", "Low")?.reasoningEffort, "Low");
  assert.equal(changeSelection(choices, changed, "reasoningEffort", "High"), null);
  assert.equal(changeSelection(choices, current, "agentPromptId", "plan")?.agentPromptId, "plan");
  assert.equal(changeSelection(choices, current, "modelId", "")?.modelId, null);
});

test("stored session choices cannot revive a removed model, prompt or another provider", () => {
  assert.deepEqual(restoreSelection(() => JSON.stringify(current), choices), current);
  assert.equal(restoreSelection(() => "invalid", choices), null);
  assert.equal(restoreSelection(() => { throw Error("storage denied"); }, choices), null);
  assert.equal(validSelection(choices, { ...current, providerKey: "other" }), false);
  assert.equal(validSelection(choices, { ...current, agentPromptId: "missing" }), false);
  assert.equal(validSelection(choices, { ...current, modelId: "missing" }), false);
});

test("send freezes selected settings and an uncertain retry never retargets to new settings", async () => {
  const selected = { ...current };
  const request = captureSubmission("epoch", "session", "text", "key", selected)!;
  selected.modelId = "two";
  assert.equal(request.selection?.modelId, "one");
  const sent: SessionSendRequest[] = [];
  const owner = createOwnedSubmissions(async value => { sent.push(value); throw Error("transport"); }, async () => { throw Error("unused"); });
  const capability = createMutationCapability("epoch");
  const signal = new AbortController().signal;
  await owner.submit(request, signal, capability, () => {});
  const retained = owner.pending("session")!.request;
  await owner.submit(captureSubmission("epoch", "session", "text", "other", selected)!, signal, capability, () => {});
  assert.equal(sent.length, 1);
  await owner.submit(retained, signal, capability, () => {});
  assert.equal(sent.length, 2);
  assert.equal(sent[1], retained);
  assert.equal(sent[1].selection?.modelId, "one");
  assert.ok(Object.isFrozen(retained.selection));
});
