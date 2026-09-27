import test from "node:test";
import assert from "node:assert/strict";
import type { SessionRuntimeScopedResponse } from "#neoastra";
import { createRuntimeObservations, projectRuntimeObservation, type RuntimeTarget } from "./runtimeObservations";
import { tabKey } from "./sessionTabs";
import { paletteAvailable } from "./paletteActions";

const target = (id = "one"): RuntimeTarget => ({ tab: { sessionId: id, projectId: null, path: null },
  request: { expectedHostEpoch: "epoch", sessionId: id, createdAt: "2026-01-01T00:00:00Z", scope: "global", projectId: null, projectPath: null } });
const reply = (id = "one"): SessionRuntimeScopedResponse => ({ status: "ok", hostEpoch: "epoch", sessionId: id, scope: "global", projectId: null, projectPath: null,
  observation: { status: "ok", hostEpoch: "epoch", sessionId: id, runtimeInstanceId: "runtime", coordinatorTransitionInProgress: false,
    entry: { attachmentGeneration: "9", isTerminated: false, isRetiring: false, activeRunId: "run", queueDrainInProgress: false,
      providerId: "fake", providerKey: "fake", modelId: null, reasoningEffort: null, agentPromptId: null, pendingAgentPromptId: null, activity: null } } });

test("runtime observations distinguish active, absent, transition, retiring, termination and errors without idle inference", () => {
  assert.equal(projectRuntimeObservation(reply()).label, "Observed active run");
  const absent = { ...reply(), observation: { ...reply().observation!, entry: null } };
  assert.equal(projectRuntimeObservation(absent).label, "Unknown · not attached");
  absent.observation!.coordinatorTransitionInProgress = true;
  assert.equal(projectRuntimeObservation(absent).label, "Observed transition");
  const retiring = { ...reply(), observation: { ...reply().observation!, entry: { ...reply().observation!.entry!, isRetiring: true } } };
  assert.equal(projectRuntimeObservation(retiring).label, "Observed retiring");
  const ended = { ...reply(), observation: { ...reply().observation!, entry: { ...reply().observation!.entry!, isTerminated: true } } };
  assert.equal(projectRuntimeObservation(ended).label, "Observed terminated attachment");
  assert.equal(projectRuntimeObservation({ ...reply(), status: "closed", observation: null }).label, "Unknown · closed");
  assert.equal(projectRuntimeObservation({ ...reply(), status: "read_failed", observation: null }).label, "Error · read_failed");
});

test("explicit refresh bounds unique rows and response count; no implicit reads", async () => {
  const calls: string[] = [];
  const owner = createRuntimeObservations(async request => { calls.push(request.sessionId); return reply(request.sessionId); });
  assert.equal(calls.length, 0);
  await owner.refresh(Array.from({ length: 40 }, (_, index) => target(String(index))), 3);
  assert.equal(calls.length, 32); assert.equal(owner.getSnapshot().rows.size, 32);
  assert.match(owner.getSnapshot().summary, /11 omitted/u);
  owner.invalidate(); assert.equal(calls.length, 32);
  assert.equal(owner.getSnapshot().rows.get(tabKey(target("0").tab))?.stale, true);
});

test("late canceled request and host/scope ABA cannot publish or clear a newer batch", async () => {
  let release!: (value: SessionRuntimeScopedResponse) => void;
  let originalSignal!: AbortSignal;
  let calls = 0;
  const owner = createRuntimeObservations(async (_request, options) => {
    if (++calls === 1) { originalSignal = options.signal; return new Promise(resolve => { release = resolve; }); }
    return reply();
  });
  const old = owner.refresh([target()]);
  owner.invalidate(); owner.invalidate();
  assert.equal(originalSignal.aborted, true);
  await owner.refresh([target()]);
  const current = owner.getSnapshot();
  release({ ...reply(), hostEpoch: "old-host" }); await old;
  assert.equal(owner.getSnapshot(), current);
  assert.equal(current.rows.get(tabKey(target().tab))?.label, "Observed active run");
});

test("wrong scope and older attachment facts are refused, with fences retained across errors", async () => {
  let value = reply(); const owner = createRuntimeObservations(async () => value);
  const row = () => owner.getSnapshot().rows.get(tabKey(target().tab));
  await owner.refresh([target()]);
  value = { ...reply(), projectId: "wrong" }; await owner.refresh([target()]);
  assert.equal(row()?.label, "Error · identity mismatch");
  value = { ...reply(), observation: { ...reply().observation!, entry: { ...reply().observation!.entry!, attachmentGeneration: "8" } } };
  await owner.refresh([target()]); assert.equal(row()?.label, "Stale attachment");
  await owner.refresh([target()]); assert.equal(row()?.label, "Stale attachment");
  value = { ...reply(), observation: { ...reply().observation!, runtimeInstanceId: "wrong" } };
  await owner.refresh([target()]); assert.equal(row()?.label, "Error · runtime identity changed");
});

test("refresh palette captures exact observed scope and rejects ABA", () => {
  const context = { workspace: true, selection: null, epoch: "host", infoReady: false, promptReady: false, searchReady: false, runtimeScope: "capture-1" };
  assert.equal(paletteAvailable("refreshStatuses", context, context), true);
  assert.equal(paletteAvailable("refreshStatuses", context, { ...context, runtimeScope: "capture-3" }), false);
  assert.equal(paletteAvailable("refreshStatuses", context, { ...context, runtimeScope: undefined }), false);
});
