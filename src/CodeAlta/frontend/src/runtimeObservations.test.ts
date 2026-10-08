import test from "node:test";
import assert from "node:assert/strict";
import type { SessionRuntimeScopedResponse } from "#neoastra";
import { createRuntimeObservations, projectRunning, projectRuntimeObservation, sessionRunning, type RuntimeTarget } from "./runtimeObservations";
import { tabKey } from "./sessionTabs";

const target = (id = "one"): RuntimeTarget => ({ tab: { sessionId: id, projectId: null, path: null },
  request: { expectedHostEpoch: "epoch", sessionId: id, createdAt: "2026-01-01T00:00:00Z", scope: "global", projectId: null, projectPath: null } });
const reply = (id = "one"): SessionRuntimeScopedResponse => ({ status: "ok", hostEpoch: "epoch", sessionId: id, scope: "global", projectId: null, projectPath: null,
  observation: { status: "ok", hostEpoch: "epoch", sessionId: id, runtimeInstanceId: "runtime", coordinatorTransitionInProgress: false,
    entry: { attachmentGeneration: "9", isTerminated: false, isRetiring: false, activeRunId: "run", queueDrainInProgress: false,
      providerId: "fake", providerKey: "fake", modelId: null, reasoningEffort: null, agentPromptId: null, pendingAgentPromptId: null, activity: null } } });

test("runtime observations distinguish active, absent, transition, retiring, termination and errors without idle inference", () => {
  assert.equal(projectRuntimeObservation(reply()).label, "Observed active run");
  assert.equal(projectRuntimeObservation(reply()).running, true);
  const absent = { ...reply(), observation: { ...reply().observation!, entry: null } };
  assert.equal(projectRuntimeObservation(absent).label, "Unknown · not attached");
  assert.equal(projectRuntimeObservation(absent).running, false);
  absent.observation!.coordinatorTransitionInProgress = true;
  assert.equal(projectRuntimeObservation(absent).label, "Observed transition");
  const retiring = { ...reply(), observation: { ...reply().observation!, entry: { ...reply().observation!.entry!, isRetiring: true } } };
  assert.equal(projectRuntimeObservation(retiring).label, "Observed retiring");
  assert.equal(projectRuntimeObservation(retiring).running, false);
  const ended = { ...reply(), observation: { ...reply().observation!, entry: { ...reply().observation!.entry!, isTerminated: true } } };
  assert.equal(projectRuntimeObservation(ended).label, "Observed terminated attachment");
  assert.equal(projectRuntimeObservation(ended).running, false);
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
  assert.equal(row()?.running, undefined);
  value = { ...reply(), observation: { ...reply().observation!, entry: { ...reply().observation!.entry!, attachmentGeneration: "8" } } };
  await owner.refresh([target()]); assert.equal(row()?.label, "Stale attachment");
  await owner.refresh([target()]); assert.equal(row()?.label, "Stale attachment");
  value = { ...reply(), observation: { ...reply().observation!, runtimeInstanceId: "wrong" } };
  await owner.refresh([target()]); assert.equal(row()?.label, "Error · runtime identity changed");
});

test("a row keeps its activity while it reloads, and an open panel's report wins over the polled one", async () => {
  let value = reply();
  let release: (() => void) | undefined;
  const owner = createRuntimeObservations(async () => { if (release === undefined) return value; await new Promise<void>(resolve => { release = resolve; }); return value; });
  const tab = target().tab, running = () => sessionRunning(owner.getSnapshot(), tab);
  assert.equal(running(), false);
  await owner.refresh([target()]);
  assert.equal(running(), true); assert.equal(projectRunning(owner.getSnapshot(), null), true);
  assert.equal(projectRunning(owner.getSnapshot(), "other"), false);

  value = { ...reply(), observation: { ...reply().observation!, entry: { ...reply().observation!.entry!, activeRunId: null } } };
  release = () => { };
  const reloading = owner.refresh([target()]);
  assert.equal(owner.getSnapshot().rows.get(tabKey(tab))?.label, "Loading observation…");
  assert.equal(running(), true, "still shown as running until the new answer arrives");
  release(); await reloading;
  assert.equal(running(), false);

  owner.setLive(tab, true);
  assert.equal(running(), true); assert.equal(projectRunning(owner.getSnapshot(), null), true);
  let notified = 0;
  owner.subscribe(() => { notified++; });
  owner.setLive(tab, true); assert.equal(notified, 0, "an unchanged report publishes nothing");
  owner.setLive(tab, false); assert.equal(running(), false); assert.equal(notified, 1);
  owner.setLive(tab, null);
  release = undefined; value = reply();
  await owner.refresh([target()]);
  assert.equal(running(), true, "without a panel the polled observation decides");
  owner.setLive(tab, false);
  assert.equal(running(), false); assert.equal(projectRunning(owner.getSnapshot(), null), false);
});

test("a refresh that changes no running session keeps the running set, and never shows it stale on the way", async () => {
  const owner = createRuntimeObservations(async request => reply(request.sessionId));
  await owner.refresh([target("one"), target("two")]);
  const running = owner.getRunning();
  assert.deepEqual([...running].sort(), [tabKey(target("one").tab), tabKey(target("two").tab)].sort());
  // What follows the set (App) is rendered again only when the set is another one: each publication is looked at.
  const seen = new Set<ReadonlySet<string>>();
  let publications = 0;
  const unsubscribe = owner.subscribe(() => { publications++; seen.add(owner.getRunning()); });
  await owner.refresh([target("one"), target("two")]);
  assert.ok(publications >= 3);
  assert.deepEqual([...seen], [running]);
  owner.setLive(target("one").tab, false);
  unsubscribe();
  assert.notEqual(owner.getRunning(), running);
  assert.deepEqual([...owner.getRunning()], [tabKey(target("two").tab)]);
  assert.equal(sessionRunning(owner.getSnapshot(), target("one").tab), false);
});
