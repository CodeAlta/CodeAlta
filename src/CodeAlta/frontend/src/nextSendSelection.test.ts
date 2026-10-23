import assert from "node:assert/strict";
import test from "node:test";
import type { SessionChoicesResponse, SessionSelection } from "#neoastra";
import { applyCatalogNextSend, createNextSendSelectionStore } from "./nextSendSelection";
import { captureSubmission, createMutationCapability, createOwnedSubmissions } from "./sessionOperations";

const current: SessionSelection = { providerKey: "alpha", agentPromptId: "plan", modelId: "old", reasoningEffort: "High" };
const choices: SessionChoicesResponse = { status: "ok", epoch: "epoch", sessionId: "one", current,
  prompts: [{ id: "plan", name: "Plan" }, { id: "default", name: "Default" }],
  models: [{ id: "old", name: "Old", efforts: ["High"], imageInput: null, startEffort: "High" },
    { id: "new", name: "New", efforts: ["Low", "Medium"], imageInput: null, startEffort: "Low" }] };
const target = { epoch: "epoch", sessionId: "one", providerKey: "alpha", modelId: "new", reasoningEffort: null };

test("catalog model handoff preserves session prompt, starts the model with its effort and freezes the next Send", async () => {
  const storage = new Map<string, string>();
  const store = createNextSendSelectionStore(key => storage.get(key) ?? null, (key, value) => { storage.set(key, value); });
  let selected = "one";
  const admission = () => ({ epoch: "epoch", sessionId: selected, active: true, canMutate: true, pending: false });
  assert.equal(await applyCatalogNextSend(target, admission, async () => choices, store), "applied");
  assert.deepEqual(store.get("epoch", "one", choices), { ...current, modelId: "new", reasoningEffort: "Low" });
  const send = captureSubmission("epoch", "one", "literal text", "literal-key", store.get("epoch", "one", choices))!;
  assert.deepEqual(send.selection, { ...current, modelId: "new", reasoningEffort: "Low" });
  assert.equal(await applyCatalogNextSend({ ...target, reasoningEffort: "Medium" }, admission, async () => choices, store), "applied");
  assert.equal(send.selection?.reasoningEffort, "Low", "a captured Send is immutable");
  assert.equal(store.get("epoch", "one", choices)?.reasoningEffort, "Medium");
  const second = { ...choices, sessionId: "two" };
  assert.equal(store.get("epoch", "two", second), null);
  selected = "two";
  assert.equal(await applyCatalogNextSend(target, admission, async () => choices, store), "selection_changed");
  assert.equal(storage.size, 1);
});

test("a fresh owned read gates provider, model, effort, epoch, selected session and uncertain Send", async () => {
  const writes: string[] = [];
  const store = createNextSendSelectionStore(() => null, (_key, value) => { writes.push(value); });
  const owner = createOwnedSubmissions(async () => { throw Error("uncertain"); }, async () => { throw Error("unused"); });
  const capability = createMutationCapability("epoch");
  let sessionId: string | null = "one";
  let epoch = "epoch";
  let catalogActive = true;
  const admission = () => ({ epoch, sessionId, active: catalogActive, canMutate: capability.canMutate(), pending: !!owner.pending("one") });
  let finish!: (value: SessionChoicesResponse) => void;
  const inFlight = applyCatalogNextSend(target, admission, () => new Promise(resolve => { finish = resolve; }), store);
  sessionId = "two";
  finish(choices);
  assert.equal(await inFlight, "selection_changed");
  sessionId = "one";
  const leftScreen = applyCatalogNextSend(target, admission, () => new Promise(resolve => { finish = resolve; }), store);
  catalogActive = false;
  finish(choices);
  assert.equal(await leftScreen, "selection_changed", "leaving the catalog fences even a same-session response");
  catalogActive = true;
  assert.equal(await applyCatalogNextSend({ ...target, providerKey: "beta" }, admission, async () => choices, store), "different_provider");
  assert.equal(await applyCatalogNextSend({ ...target, modelId: "missing" }, admission, async () => choices, store), "unavailable");
  assert.equal(await applyCatalogNextSend({ ...target, reasoningEffort: "High" }, admission, async () => choices, store), "unavailable");
  assert.equal(await applyCatalogNextSend(target, admission, async () => ({ ...choices, status: "unavailable" }), store), "unavailable");
  assert.equal(await applyCatalogNextSend(target, admission, async () => ({ ...choices, epoch: "different" }), store), "stale_epoch");
  const request = captureSubmission("epoch", "one", "literal text", "first-key", current)!;
  await owner.submit(request, new AbortController().signal, capability, () => {});
  assert.equal(await applyCatalogNextSend(target, admission, async () => { throw Error("must not read"); }, store), "pending");
  assert.deepEqual(owner.pending("one")?.request, request);
  epoch = "different";
  assert.equal(await applyCatalogNextSend(target, admission, async () => choices, store), "stale_epoch");
  assert.deepEqual(writes, []);
});

test("instance-owned selection survives a denied storage write but never revives against changed choices", () => {
  const store = createNextSendSelectionStore(() => { throw Error("storage blocked"); }, () => { throw Error("storage blocked"); });
  const next = { ...current, modelId: "new", reasoningEffort: "Low" };
  assert.equal(store.set("epoch", "one", choices, next), true);
  assert.deepEqual(store.get("epoch", "one", choices), next);
  assert.equal(store.get("epoch", "one", { ...choices, models: [choices.models[0]] }), null);
  assert.equal(store.get("new-epoch", "one", { ...choices, epoch: "new-epoch" }), null);
  assert.equal(store.set("epoch", "one", choices, { ...current, modelId: null, reasoningEffort: null }), false, "a listed model is named");
});

test("a selection of a provider the session left is forgotten, and the next Send takes the session's own", () => {
  // The host moved the session to another provider (a fallback after a failure): what was chosen for the former one
  // can never be sent, and must not keep the composer from sending.
  const store = createNextSendSelectionStore(() => null, () => {});
  const next = { ...current, modelId: "new", reasoningEffort: "Low" };
  assert.equal(store.set("epoch", "one", choices, next), true);
  const moved: SessionChoicesResponse = { ...choices, current: { ...current, providerKey: "beta" } };
  assert.equal(store.get("epoch", "one", moved), null);
  assert.equal(store.current("epoch", "one"), null);
  // Within the same provider, a selection the catalog no longer offers is kept, unverified, as before.
  assert.equal(store.set("epoch", "one", choices, next), true);
  assert.equal(store.get("epoch", "one", { ...choices, models: [choices.models[0]] }), null);
  assert.deepEqual(store.current("epoch", "one"), next);
});

test("a selection kept while the provider listed no model names the session's model once it does", () => {
  const store = createNextSendSelectionStore(() => null, () => {});
  const unlisted = { ...choices, current: { ...current, modelId: null, reasoningEffort: null }, models: [] };
  const kept = { ...unlisted.current, agentPromptId: "default" };
  assert.equal(store.set("epoch", "one", unlisted, kept), true);
  assert.deepEqual(store.get("epoch", "one", choices), { ...current, agentPromptId: "default" });
  assert.deepEqual(store.current("epoch", "one"), { ...current, agentPromptId: "default" });
});

test("mounted selection notifications isolate throwing listeners and refuse a changed catalog after restore", async () => {
  const saved = { ...current, modelId: "new", reasoningEffort: "Low" };
  const store = createNextSendSelectionStore(() => JSON.stringify({ selection: saved, base: { agentPromptId: "plan", modelId: "old", reasoningEffort: "High" } }),
    () => { throw Error("storage denied"); });
  const notifications: SessionSelection[] = [];
  store.subscribe(() => { throw Error("disconnected view"); });
  const unsubscribe = store.subscribe(value => notifications.push(value.selection));
  assert.deepEqual(store.get("epoch", "one", choices), saved);
  const different = { ...choices, models: [choices.models[0]] };
  const admission = () => ({ epoch: "epoch", sessionId: "one", active: true, canMutate: true, pending: false });
  assert.equal(await applyCatalogNextSend({ ...target, modelId: "old" }, admission, async () => different, store), "selection_changed");
  assert.deepEqual(notifications, []);
  assert.equal(store.set("epoch", "one", choices, current), true);
  assert.deepEqual(notifications, [current]);
  unsubscribe();
  assert.equal(store.set("epoch", "one", choices, current), true);
  assert.deepEqual(notifications, [current]);
});

test("a change of the host to the agent prompt, the model or the effort of the session wins over a selection made before it", () => {
  const storage = new Map<string, string>();
  const store = createNextSendSelectionStore(key => storage.get(key) ?? null, (key, value) => { storage.set(key, value); });
  const atDefault: SessionChoicesResponse = { ...choices, current: { ...current, agentPromptId: "default" } };
  // The user chooses Plan for the next Send of a session that runs with Default: kept while the host keeps Default.
  const plan = { ...atDefault.current!, agentPromptId: "plan" };
  assert.equal(store.set("epoch", "one", atDefault, plan), true);
  assert.deepEqual(store.get("epoch", "one", atDefault), plan);
  // The agent switched itself to another agent prompt (`alta session set_agent`): the composer shows the session's.
  const reviewing: SessionChoicesResponse = { ...atDefault, prompts: [...atDefault.prompts, { id: "review", name: "Review" }],
    current: { ...atDefault.current!, agentPromptId: "review" } };
  assert.equal(store.get("epoch", "one", reviewing), null);
  assert.equal(store.current("epoch", "one"), null);
  // It is not restored either once the window opens again.
  assert.equal(createNextSendSelectionStore(key => storage.get(key) ?? null, () => {}).get("epoch", "one", atDefault), null);
  // Another model or effort given by the host is a change as well.
  assert.equal(store.set("epoch", "one", atDefault, plan), true);
  assert.equal(store.get("epoch", "one", { ...atDefault, current: { ...atDefault.current!, modelId: "new", reasoningEffort: "Low" } }), null);
  // A selection stored by an earlier version, without what the host had then, is not restored.
  storage.set("codealta.desktop.selection.one", JSON.stringify(plan));
  assert.equal(createNextSendSelectionStore(key => storage.get(key) ?? null, () => {}).get("epoch", "one", atDefault), null);
});

test("after a Send that took the selection, the agent switching back to the former agent prompt is shown", () => {
  const storage = new Map<string, string>();
  const store = createNextSendSelectionStore(key => storage.get(key) ?? null, (key, value) => { storage.set(key, value); });
  const atDefault: SessionChoicesResponse = { ...choices, current: { ...current, agentPromptId: "default" } };
  const plan = { ...atDefault.current!, agentPromptId: "plan" };
  assert.equal(store.set("epoch", "one", atDefault, plan), true);
  // The plan is sent and the session runs in Plan: the selection stays shown, as the session has it.
  store.sent("epoch", "one", captureSubmission("epoch", "one", "plan it", "key", plan)!.selection);
  const inPlan: SessionChoicesResponse = { ...atDefault, current: plan };
  assert.deepEqual(store.get("epoch", "one", inPlan), plan);
  // The plan is approved and the agent goes back to Default, even while nobody looked at the session.
  assert.equal(createNextSendSelectionStore(key => storage.get(key) ?? null, () => {}).get("epoch", "one", atDefault), null);
  assert.equal(store.get("epoch", "one", atDefault), null);
  // A Send of another selection than the one kept leaves it as it is.
  assert.equal(store.set("epoch", "one", atDefault, plan), true);
  store.sent("epoch", "one", { ...plan, modelId: "new", reasoningEffort: "Low" });
  assert.deepEqual(store.get("epoch", "one", atDefault), plan);
  store.sent("epoch", "one", null);
  assert.deepEqual(store.get("epoch", "one", atDefault), plan);
});
