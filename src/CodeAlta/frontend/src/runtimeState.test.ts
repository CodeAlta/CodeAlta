import assert from "node:assert/strict";
import test from "node:test";
import { createRuntimeStateReader, type RuntimeState } from "./runtimeState";
import type { SessionRuntimeStateResponse } from "#neoastra";

const request = { expectedHostEpoch: "host", sessionId: "selected" };
function response(overrides: Partial<SessionRuntimeStateResponse> = {}): SessionRuntimeStateResponse {
  return { status: "ok", hostEpoch: "host", sessionId: "selected", runtimeInstanceId: "runtime",
    coordinatorTransitionInProgress: false, entry: null, ...overrides };
}
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(done => { resolve = done; }); return { promise, resolve }; }

test("manual only; later request wins even when the earlier response arrives last", async () => {
  const a = deferred<SessionRuntimeStateResponse>(), b = deferred<SessionRuntimeStateResponse>();
  const states: RuntimeState[] = []; let calls = 0;
  const loader = createRuntimeStateReader(() => ++calls === 1 ? a.promise : b.promise).forSelection(request, new AbortController().signal, value => states.push(value));
  assert.equal(calls, 0);
  const first = loader.refresh(), second = loader.refresh();
  b.resolve(response({ coordinatorTransitionInProgress: true })); await second;
  a.resolve(response()); await first;
  assert.equal(states.length, 3);
  assert.equal(states.at(-1)?.kind, "ready");
  assert.equal((states.at(-1) as Extract<RuntimeState, { kind: "ready" }>).snapshot.coordinatorTransitionInProgress, true);
});

test("selection/epoch detach discards both delayed success and failure", async () => {
  for (const fail of [false, true]) {
    const pending = deferred<SessionRuntimeStateResponse>();
    const scope = new AbortController(); const states: RuntimeState[] = [];
    const reader = createRuntimeStateReader(async req => {
      if (req.expectedHostEpoch === "new") return response({ hostEpoch: "new", sessionId: "other" });
      const value = await pending.promise; if (fail) throw Error("private"); return value;
    });
    const old = reader.forSelection(request, scope.signal, value => states.push(value));
    const work = old.refresh(); scope.abort();
    const fresh = reader.forSelection({ expectedHostEpoch: "new", sessionId: "other" }, new AbortController().signal, value => states.push(value));
    await fresh.refresh(); const count = states.length;
    pending.resolve(response()); await work; await old.refresh();
    assert.equal(states.length, count);
    assert.equal((states.at(-1) as Extract<RuntimeState, { kind: "ready" }>).snapshot.sessionId, "other");
  }
});

test("stale host requires reload and cannot retry the old epoch", async () => {
  for (const result of [response({ hostEpoch: "new" }), response({ status: "stale_epoch" })]) {
    let calls = 0; const states: RuntimeState[] = [];
    const reader = createRuntimeStateReader(async () => { calls++; return result; });
    const loader = reader.forSelection(request, new AbortController().signal, value => states.push(value));
    await loader.refresh(); await loader.refresh();
    const selectedAgain = reader.forSelection({ ...request, sessionId: "another" }, new AbortController().signal, value => states.push(value));
    await selectedAgain.refresh();
    assert.equal(calls, 1); assert.deepEqual(states.at(-1), { kind: "error", code: "stale_epoch" });
  }
});

test("runtime identity changes require reload; attachment generations are not ordered", async () => {
  const entry = { attachmentGeneration: "9007199254740993", isTerminated: false, isRetiring: true, activeRunId: null,
    queueDrainInProgress: true, providerId: "fake", providerKey: "fake", modelId: null, reasoningEffort: null, agentPromptId: "default", pendingAgentPromptId: "plan" };
  const results = [response({ entry }), response({ entry: { ...entry, attachmentGeneration: "1" } }), response({ runtimeInstanceId: "replacement" })];
  const states: RuntimeState[] = []; let calls = 0;
  const loader = createRuntimeStateReader(async () => results[calls++]).forSelection(request, new AbortController().signal, value => states.push(value));
  await loader.refresh(); await loader.refresh();
  assert.equal((states.at(-1) as Extract<RuntimeState, { kind: "ready" }>).snapshot.entry!.attachmentGeneration, "1");
  await loader.refresh(); await loader.refresh();
  assert.equal(calls, 3); assert.deepEqual(states.at(-1), { kind: "error", code: "stale_runtime" });
});

test("absence stays absence, wrong selection and failures never become idle state", async () => {
  for (const [result, code] of [[response(), null], [response({ sessionId: "other" }), "invalid_response"], [response({ status: "wire_limit" }), "wire_limit"], [null, "read_failed"]] as const) {
    const states: RuntimeState[] = [];
    const loader = createRuntimeStateReader(async () => { if (!result) throw Error("private"); return result; }).forSelection(request, new AbortController().signal, value => states.push(value));
    await loader.refresh();
    if (code) assert.deepEqual(states.at(-1), { kind: "error", code });
    else assert.equal((states.at(-1) as Extract<RuntimeState, { kind: "ready" }>).snapshot.entry, null);
    assert.equal(JSON.stringify(states).includes("private"), false);
  }
});
