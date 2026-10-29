import assert from "node:assert/strict";
import test from "node:test";
import { changeSelection, completeSelection, restoreSelection, validSelection } from "./sessionSelection";
import { captureSubmission, createMutationCapability, createOwnedSubmissions } from "./sessionOperations";
import type { SessionChoicesResponse, SessionSelection, SessionSendRequest } from "#neoastra";

const current: SessionSelection = { providerKey: "provider", agentPromptId: "default", modelId: "one", reasoningEffort: "High" };
const choices: SessionChoicesResponse = { status: "ok", epoch: "epoch", sessionId: "session", current,
  prompts: [{ id: "default", name: "Default" }, { id: "plan", name: "Plan" }],
  models: [{ id: "one", name: "One", efforts: ["High"], imageInput: null, startEffort: "High" },
    { id: "two", name: "Two", efforts: ["Low", "Medium"], imageInput: null, startEffort: "Medium" },
    { id: "plain", name: "Plain", efforts: [], imageInput: null, startEffort: null }] };

test("context is session-scoped, exact, clearable offline and reset on a model change", () => {
  const available = { ...choices, models: choices.models.map(model => ({ ...model, contextSizes: [272000, 1050000] })) };
  const selected = changeSelection(available, current, "context", "1050000")!;
  assert.deepEqual(selected.context, { tokens: 1050000 });
  assert.equal(changeSelection(available, current, "context", "500000"), null);
  assert.equal(validSelection(choices, selected), false);
  assert.deepEqual(changeSelection(choices, selected, "context", "")?.context, { tokens: null });
  assert.deepEqual(changeSelection(available, selected, "modelId", "two")?.context, { tokens: null });
  const send = captureSubmission("epoch", "session", "hello", "key", selected)!;
  selected.context!.tokens = 272000;
  assert.equal(send.selection?.context?.tokens, 1050000);
  assert.equal(Object.isFrozen(send.selection?.context), true);
});

test("another model starts with its own effort; prompt and supported effort changes are functional", () => {
  const changed = changeSelection(choices, current, "modelId", "two")!;
  assert.equal(changed.modelId, "two");
  assert.equal(changed.reasoningEffort, "Medium");
  assert.equal(changeSelection(choices, changed, "reasoningEffort", "Low")?.reasoningEffort, "Low");
  assert.equal(changeSelection(choices, changed, "reasoningEffort", "High"), null);
  assert.equal(changeSelection(choices, current, "agentPromptId", "plan")?.agentPromptId, "plan");
  assert.deepEqual(changeSelection(choices, current, "modelId", "plain"), { ...current, modelId: "plain", reasoningEffort: null });
});

test("a selection names a listed model and one of its efforts: there is no default one", () => {
  assert.equal(changeSelection(choices, current, "modelId", ""), null);
  assert.equal(validSelection(choices, { ...current, modelId: null, reasoningEffort: null }), false);
  assert.equal(validSelection(choices, { ...current, reasoningEffort: null }), false);
  assert.equal(validSelection(choices, { ...current, modelId: "plain", reasoningEffort: null }), true);
  // A provider that lists nothing leaves the model open, so that the prompt can still be chosen.
  const unlisted = { ...choices, current: { ...current, modelId: null, reasoningEffort: null }, models: [] };
  assert.equal(validSelection(unlisted, { ...current, modelId: null, reasoningEffort: null }), true);
  assert.equal(changeSelection(unlisted, unlisted.current, "agentPromptId", "plan")?.agentPromptId, "plan");
});

test("a model that lists no effort takes the one the session holds, or none", () => {
  // The session was given an effort elsewhere (the alta tool, an automation, a work item) for a model whose
  // provider does not say which efforts it has: the selection is one the session can be sent with.
  const held = { ...current, modelId: "plain", reasoningEffort: "Low" };
  const value = { ...choices, current: held };
  assert.equal(validSelection(value, held), true);
  assert.equal(validSelection(value, { ...held, reasoningEffort: null }), true);
  // Its other choices can be made, and keep the effort.
  assert.deepEqual(changeSelection(value, held, "agentPromptId", "plan"), { ...held, agentPromptId: "plan" });
  assert.deepEqual(restoreSelection(() => JSON.stringify(held), value), held);
  assert.equal(completeSelection(value, held), held);
  // Another model starts with its own effort, and this model is chosen without one.
  assert.deepEqual(changeSelection(value, held, "modelId", "two"), { ...held, modelId: "two", reasoningEffort: "Medium" });
  assert.deepEqual(changeSelection(choices, current, "modelId", "plain"), { ...current, modelId: "plain", reasoningEffort: null });
  // A model that lists its efforts still takes one of them only.
  assert.equal(validSelection(choices, { ...current, modelId: "two", reasoningEffort: "High" }), false);
  assert.equal(changeSelection(choices, { ...current, modelId: "two", reasoningEffort: "High" }, "agentPromptId", "plan"), null);
});

test("a selection kept without a model or an effort takes what the session runs with", () => {
  const open = { ...current, agentPromptId: "plan", modelId: null, reasoningEffort: null };
  assert.deepEqual(completeSelection(choices, open), { ...current, agentPromptId: "plan" });
  assert.deepEqual(completeSelection(choices, { ...current, modelId: "two", reasoningEffort: null }), { ...current, modelId: "two", reasoningEffort: "Medium" });
  assert.deepEqual(completeSelection(choices, { ...current, modelId: "plain", reasoningEffort: null }), { ...current, modelId: "plain", reasoningEffort: null });
  assert.equal(completeSelection(choices, current), current);
  assert.deepEqual(completeSelection(choices, { ...open, providerKey: "other" }), { ...open, providerKey: "other" });
  // What an earlier version saved as "provider default" is restored as the model of the session.
  assert.deepEqual(restoreSelection(() => JSON.stringify(open), choices), { ...current, agentPromptId: "plan" });
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
