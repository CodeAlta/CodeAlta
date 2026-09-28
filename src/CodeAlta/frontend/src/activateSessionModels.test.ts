import assert from "node:assert/strict";
import test from "node:test";
import type { ModelCatalogModelsResponse, SessionChoicesResponse } from "#neoastra";
import { activateSessionModels } from "./activateSessionModels";

const choices: SessionChoicesResponse = { status: "ok", epoch: "epoch", sessionId: "session",
  current: { providerKey: "provider", modelId: "model", reasoningEffort: null, agentPromptId: "default" },
  prompts: [], models: [] };
const catalog: ModelCatalogModelsResponse = { status: "ok", epoch: "epoch", providerId: "provider", availability: "Ready", models: [], truncated: false };

test("explicit load initializes only the session provider and re-reads authoritative choices", async () => {
  const calls: string[] = [];
  const populated = { ...choices, models: [{ id: "model", name: "Real catalog model", efforts: [], imageInput: null }] };
  const result = await activateSessionModels("epoch", "session", async () => {
    calls.push("choices"); return calls.length === 1 ? choices : populated;
  }, async provider => { calls.push(provider); return catalog; }, () => true);
  assert.equal(result, populated);
  assert.deepEqual(calls, ["choices", "provider", "choices"]);
});

test("a stale scope cannot activate a provider", async () => {
  await assert.rejects(activateSessionModels("epoch", "session", async () => choices,
    async () => { assert.fail("must not activate"); }, () => false), { name: "AbortError" });
});

test("provider changes and failed catalog loading never apply old model choices", async () => {
  let reads = 0;
  await assert.rejects(activateSessionModels("epoch", "session", async () => ++reads === 1 ? choices
    : { ...choices, current: { ...choices.current!, providerKey: "other" } }, async () => catalog, () => true), /provider changed/);
  reads = 0;
  await assert.rejects(activateSessionModels("epoch", "session", async () => { reads++; return choices; },
    async () => ({ ...catalog, status: "unavailable" }), () => true), /catalog unavailable/);
  assert.equal(reads, 1);
});
