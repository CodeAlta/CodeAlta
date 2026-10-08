import test from "node:test";
import assert from "node:assert/strict";
import type { SessionRuntimeScopedResponse, SessionUsageResponse } from "#neoastra";
import { boundedInfoCopy, readSessionInfoObservations } from "./sessionInfoObservations";
import type { RuntimeTarget } from "./runtimeObservations";

const epoch = "00000000-0000-0000-0000-000000000001";
const target: RuntimeTarget = { tab: { sessionId: "saved", projectId: null, path: null }, request: {
  expectedHostEpoch: epoch, sessionId: "saved", createdAt: "2026-01-01T00:00:00Z", scope: "global", projectId: null, projectPath: null } };
const runtime = (): SessionRuntimeScopedResponse => ({ status: "ok", hostEpoch: epoch, sessionId: "saved", scope: "global", projectId: null, projectPath: null,
  observation: { status: "ok", hostEpoch: epoch, sessionId: "saved", runtimeInstanceId: epoch, coordinatorTransitionInProgress: false,
    entry: { attachmentGeneration: "2", isTerminated: false, isRetiring: false, activeRunId: "run", backgroundTasks: [], queueDrainInProgress: false,
      providerId: "observed-provider", providerKey: "observed-key", modelId: "observed-model", reasoningEffort: "High", agentPromptId: "current", pendingAgentPromptId: "next", activity: null } } });
const usage = (): SessionUsageResponse => ({ status: "ok", hostEpoch: epoch, sessionId: "saved", runtimeInstanceId: epoch,
  attachmentGeneration: "2", omittedUsageEvents: "3", observation: { sequence: "4", scope: "LastOperation", source: "LocalProviderUsage",
    sourceUpdatedAt: null, eventTimestamp: "2026-01-01T00:00:00Z", hadInvalidValues: true, hadOmittedData: true,
    window: { currentTokens: "0", tokenLimit: null, messageCount: 0, label: null, totalContextEnvelope: null, maxOutputTokens: null },
    lastOperation: null, rateLimits: null, sessionTotal: null } });
const read = (state = runtime(), value = usage()) => readSessionInfoObservations(target, async () => state, async () => value, new AbortController().signal, () => true);

test("info keeps observed configuration, current/pending prompts and usage provenance distinct; zero is not unknown", async () => {
  const result = await read();
  assert.match(JSON.stringify(result.runtime), /observed-model/);
  assert.match(JSON.stringify(result.runtime), /Current attachment agent prompt ID.*current/);
  assert.match(JSON.stringify(result.runtime), /Pending agent prompt ID \(next Send\).*next/);
  assert.match(JSON.stringify(result.usage), /LocalProviderUsage \/ LastOperation \/ 4/);
  assert.match(JSON.stringify(result.usage), /0 \/ Unknown \/ 0/);
  assert.match(JSON.stringify(result.usage), /Yes \/ Yes/);
  assert.match(JSON.stringify(result.usage), /2 \/ 3/);
});

test("info refuses cross-read attachment, host, scope and invalid usage instead of merging", async () => {
  for (const value of [{ ...usage(), attachmentGeneration: "3" }, { ...usage(), hostEpoch: "other" },
    { ...usage(), observation: { ...usage().observation!, source: "invented" } },
    { ...usage(), observation: { ...usage().observation!, window: { currentTokens: "-1", tokenLimit: null, messageCount: null, automationId: null, worktreePath: null, worktreeRoot: null, worktreeName: null, worktreeMissing: false, label: null, totalContextEnvelope: null, maxOutputTokens: null } } }]) {
    const result = await read(runtime(), value);
    assert.match(JSON.stringify(result), /Unavailable/);
    assert.doesNotMatch(JSON.stringify(result), /observed-model/);
  }
  assert.match(JSON.stringify(await read({ ...runtime(), projectId: "wrong" })), /Unavailable/);
  assert.match(JSON.stringify(await read({ ...runtime(), observation: { ...runtime().observation!, entry: {
    ...runtime().observation!.entry!, modelId: "x".repeat(257) } } })), /Unavailable/);
});

test("absent runtime skips usage; no event and errors remain explicit", async () => {
  let reads = 0;
  const absent = { ...runtime(), observation: { ...runtime().observation!, entry: null } };
  const result = await readSessionInfoObservations(target, async () => absent, async () => { reads++; return usage(); }, new AbortController().signal, () => true);
  assert.equal(reads, 0); assert.match(JSON.stringify(result.runtime), /not attached/);
  assert.match(JSON.stringify(await read(runtime(), { ...usage(), status: "no_observation", observation: null })), /No usage observation/);
  const failed = await readSessionInfoObservations(target, async () => runtime(), async () => { throw new Error("private"); }, new AbortController().signal, () => true);
  assert.match(JSON.stringify(failed.usage), /Error: usage read failed/); assert.doesNotMatch(JSON.stringify(failed), /private/);
});

test("synchronous lifetime/ABA fence refuses held replies and never starts a second read", async () => {
  let release!: (value: SessionRuntimeScopedResponse) => void;
  let revision = 1; let reads = 0;
  const result = readSessionInfoObservations(target, () => new Promise(resolve => { release = resolve; }), async () => { reads++; return usage(); },
    new AbortController().signal, () => revision === 1);
  revision = 3; release(runtime());
  assert.match(JSON.stringify(await result), /Unavailable/); assert.equal(reads, 0);
});

test("displayed-detail clipboard budget refuses oversize instead of silently exposing more", () => {
  assert.equal(boundedInfoCopy("Displayed details"), "Displayed details");
  assert.equal(boundedInfoCopy("x".repeat(32769)), null);
  assert.equal(boundedInfoCopy(" "), null);
});
