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

test("a selection kept while the provider listed no model names the session's model once it does", () => {
  const store = createNextSendSelectionStore(() => null, () => {});
  const unlisted = { ...choices, current: { ...current, modelId: null, reasoningEffort: null }, models: [] };
  const kept = { ...unlisted.current, agentPromptId: "default" };
  assert.equal(store.set("epoch", "one", unlisted, kept), true);
  assert.deepEqual(store.get("epoch", "one", choices), { ...current, agentPromptId: "default" });
  assert.deepEqual(store.current("epoch", "one"), { ...current, agentPromptId: "default" });
});

test("mounted selection notifications isolate throwing listeners and refuse a changed catalog after restore", async () => {
  const saved = JSON.stringify({ ...current, modelId: "new", reasoningEffort: "Low" });
  const store = createNextSendSelectionStore(() => saved, () => { throw Error("storage denied"); });
  const notifications: SessionSelection[] = [];
  store.subscribe(() => { throw Error("disconnected view"); });
  const unsubscribe = store.subscribe(value => notifications.push(value.selection));
  assert.deepEqual(store.get("epoch", "one", choices), JSON.parse(saved));
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
