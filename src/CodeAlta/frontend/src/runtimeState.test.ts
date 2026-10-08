import assert from "node:assert/strict";
import test from "node:test";
import { createRuntimeStateReader, type RuntimeState } from "./runtimeState";
import { createMutationCapability } from "./sessionOperations";
import type { SessionRuntimeStateResponse } from "#neoastra";

const request = { expectedHostEpoch: "host", sessionId: "selected" };
function response(overrides: Partial<SessionRuntimeStateResponse> = {}): SessionRuntimeStateResponse {
  return { status: "ok", hostEpoch: "host", sessionId: "selected", runtimeInstanceId: "runtime",
    coordinatorTransitionInProgress: false, entry: null, ...overrides };
}
type Invoke = Parameters<typeof createRuntimeStateReader>[0];
class Fixture {
  readonly originals: { stage: string; task: Promise<unknown>; original?: Promise<unknown>; observer: Promise<void>; outcome: string; error?: unknown; synchronousFailure?: boolean; expected?: { error: unknown } }[] = [];
  readonly controllers: AbortController[] = [];
  readonly releases: (() => void)[] = [];
  readonly readers: ReturnType<typeof createRuntimeStateReader>[] = [];
  readonly expectations: { error: unknown; seen: boolean }[] = [];
  readonly stopFailures: unknown[] = [];
  stopping = false;
  expired = false;
  deadlineFailure: Error | undefined;
  launch<T>(stage: string, start: () => Promise<T>, expected?: { error: unknown; seen?: boolean }, rethrowSynchronous = false): Promise<T> {
    let resolve!: (value: T | PromiseLike<T>) => void, reject!: (error: unknown) => void;
    const task = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
    const record: (typeof this.originals)[number] = { stage, task, observer: Promise.resolve(), outcome: "pending", expected };
    record.observer = task.then(() => { record.outcome = "success"; }, error => {
      record.outcome = "failure"; record.error = error;
      if (expected && error === expected.error) expected.seen = true;
    });
    this.originals.push(record);
    try { record.original = start(); resolve(record.original as Promise<T>); } catch (error) {
      record.synchronousFailure = true;
      reject(error);
      // Preserve actual synchronous invoke behavior while retaining its failure record/observer.
      if (rethrowSynchronous) throw error;
    }
    return task;
  }
  scope() { const controller = new AbortController(); this.controllers.push(controller); if (this.stopping) controller.abort(); return controller; }
  gate<T>(cleanupValue: T) {
    let resolve!: (value: T) => void;
    const promise = this.launch("gate", () => new Promise<T>(done => { resolve = done; }));
    this.releases.push(() => resolve(cleanupValue));
    if (this.stopping) resolve(cleanupValue);
    return { promise, resolve };
  }
  reader(invoke: Invoke, expected?: { error: unknown }) {
    const expectation = expected ? { ...expected, seen: false } : undefined;
    if (expectation) this.expectations.push(expectation);
    const reader = createRuntimeStateReader((...args) => this.launch("invoke", () => invoke(...args), expectation, true));
    this.readers.push(reader);
    return { ...reader, forSelection: (...args: Parameters<typeof reader.forSelection>) => {
      const selection = reader.forSelection(...args);
      return { ...selection, refresh: () => this.launch("refresh", () => {
        const result = selection.refresh();
        this.keepDrain(reader); // Observe the retained worker before its scheduled launch.
        return result;
      }) };
    } };
  }
  keepDrain(reader: ReturnType<typeof createRuntimeStateReader>) {
    try {
      const drain = reader.settled();
      if (!this.originals.some(record => record.stage === "drain" && record.original === drain)) this.launch("drain", () => drain);
    } catch (error) { this.stopFailures.push(error); }
  }
  stop() {
    this.stopping = true;
    for (const release of this.releases) { try { release(); } catch (error) { this.stopFailures.push(error); } }
    for (const controller of this.controllers) { try { controller.abort(); } catch (error) { this.stopFailures.push(error); } }
  }
  static async run(action: (fixture: Fixture) => Promise<void>) {
    const fixture = new Fixture();
    let timer!: ReturnType<typeof setTimeout>, finish!: () => void;
    const deadline = new Promise<void>((resolve, reject) => {
      finish = resolve;
      timer = setTimeout(() => {
        fixture.expired = true;
        fixture.deadlineFailure = new Error("Runtime-state deadline; uncertain frontend waiters/dependents retained, host termination unknown", { cause: fixture });
        fixture.stop(); reject(fixture.deadlineFailure);
      }, 5_000);
    });
    const observer = deadline.then(() => {}, () => {});
    const setup = fixture.launch("setup", () => Promise.resolve());
    const body = fixture.launch("body", () => setup.then(() => action(fixture)));
    let primary: unknown; let failed = false;
    try { await Promise.race([body, deadline]); } catch (error) { primary = error; failed = true; }
    finally {
      fixture.stop();
      try {
        for (const reader of fixture.readers) fixture.keepDrain(reader);
        if (!fixture.expired) {
          const join = (async () => { for (let index = 0; index < fixture.originals.length; index++) await fixture.originals[index].observer; })();
          try { await Promise.race([join, deadline]); if (fixture.expired) throw fixture.deadlineFailure; } catch (error) {
            throw new AggregateError(failed ? [primary, error] : [error], "Runtime-state cleanup uncertain", { cause: { fixture, body, join, observer } });
          }
          const failures = fixture.originals.filter(record => record.outcome === "failure" &&
            (!record.expected || record.expected.error !== record.error)).map(record => record.error);
          failures.push(...fixture.stopFailures);
          for (const expected of fixture.expectations) if (!expected.seen) failures.push(new Error("Expected invoke failure was not observed", { cause: expected }));
          if (failed) failures.unshift(primary);
          if (failures.length) throw new AggregateError([...new Set(failures)], "Runtime-state fixture failed", { cause: fixture });
        }
      } finally { clearTimeout(timer); finish(); await observer; }
    }
    if (fixture.expired) throw new AggregateError([...new Set(failed ? [primary, fixture.deadlineFailure] : [fixture.deadlineFailure])],
      "Runtime-state deadline permanently failed", { cause: fixture });
    if (failed) throw primary;
  }
}

test("manual only: original waiter and one latest pending refresh; displaced promises settle explicitly", () => Fixture.run(async f => {
  const a = f.gate(response()), b = f.gate(response()), entered = f.gate<void>(undefined);
  const states: RuntimeState[] = []; let calls = 0;
  const loader = f.reader(() => { entered.resolve(undefined); return ++calls === 1 ? a.promise : b.promise; }).forSelection(request, f.scope().signal, value => states.push(value));
  assert.equal(calls, 0);
  const first = loader.refresh(); await entered.promise;
  let latest = loader.refresh();
  for (let index = 0; index < 32; index++) {
    const displaced = latest; latest = loader.refresh();
    assert.equal(await displaced, "superseded");
    assert.equal(calls, 1);
  }
  a.resolve(response()); assert.equal(await first, "superseded");
  b.resolve(response({ coordinatorTransitionInProgress: true })); assert.equal(await latest, "settled");
  assert.equal(calls, 2);
  assert.equal(states.at(-1)?.kind, "ready");
  assert.equal((states.at(-1) as Extract<RuntimeState, { kind: "ready" }>).snapshot.coordinatorTransitionInProgress, true);
}));

test("selection/epoch detach discards ordinary delayed success and expected failure without overlapping waiters", () => Fixture.run(async f => {
  for (const fail of [false, true]) {
    const pending = f.gate(response()), entered = f.gate<void>(undefined);
    const scope = f.scope(); const states: RuntimeState[] = [];
    const failure = new Error("private");
    const reader = f.reader(async req => {
      if (req.expectedHostEpoch === "new") return response({ hostEpoch: "new", sessionId: "other" });
      entered.resolve(undefined);
      const value = await pending.promise; if (fail) throw failure; return value;
    }, fail ? { error: failure } : undefined);
    const old = reader.forSelection(request, scope.signal, value => states.push(value));
    const work = old.refresh(); await entered.promise; scope.abort();
    const fresh = reader.forSelection({ expectedHostEpoch: "new", sessionId: "other" }, f.scope().signal, value => states.push(value));
    const refreshing = fresh.refresh();
    pending.resolve(response()); await work; await refreshing;
    const count = states.length; assert.equal(await old.refresh(), "detached");
    assert.equal(states.length, count);
    assert.equal((states.at(-1) as Extract<RuntimeState, { kind: "ready" }>).snapshot.sessionId, "other");
  }
}));

test("stale host requires reload and cannot retry the old epoch", () => Fixture.run(async f => {
  for (const result of [response({ hostEpoch: "new" }), response({ status: "stale_epoch" })]) {
    let calls = 0; const states: RuntimeState[] = [];
    const reader = f.reader(async () => { calls++; return result; });
    const loader = reader.forSelection(request, f.scope().signal, value => states.push(value));
    await loader.refresh(); await loader.refresh();
    const selectedAgain = reader.forSelection({ ...request, sessionId: "another" }, f.scope().signal, value => states.push(value));
    await selectedAgain.refresh();
    assert.equal(calls, 1); assert.deepEqual(states.at(-1), { kind: "error", code: "stale_epoch" });
  }
}));

test("runtime identity changes require reload; attachment generations are not ordered", () => Fixture.run(async f => {
  const entry = { attachmentGeneration: "9007199254740993", isTerminated: false, isRetiring: true, activeRunId: null,
    backgroundTasks: [], queueDrainInProgress: true, providerId: "fake", providerKey: "fake", modelId: null, reasoningEffort: null, agentPromptId: "default", pendingAgentPromptId: "plan", activity: null };
  const results = [response({ entry }), response({ entry: { ...entry, attachmentGeneration: "1" } }),
    response({ entry: { ...entry, attachmentGeneration: "9223372036854775807" } }), response({ runtimeInstanceId: "replacement" })];
  const states: RuntimeState[] = []; let calls = 0;
  const loader = f.reader(async () => results[calls++]).forSelection(request, f.scope().signal, value => states.push(value));
  await loader.refresh(); await loader.refresh();
  assert.equal((states.at(-1) as Extract<RuntimeState, { kind: "ready" }>).snapshot.entry!.attachmentGeneration, "1");
  await loader.refresh();
  assert.equal((states.at(-1) as Extract<RuntimeState, { kind: "ready" }>).snapshot.entry!.attachmentGeneration, "9223372036854775807");
  await loader.refresh(); await loader.refresh();
  assert.equal(calls, 4); assert.deepEqual(states.at(-1), { kind: "error", code: "stale_runtime" });
}));

test("absence stays absence, wrong selection and failures never become idle state", () => Fixture.run(async f => {
  for (const [result, code] of [[response(), null], [response({ sessionId: "other" }), "invalid_response"], [response({ status: "wire_limit" }), "wire_limit"], [null, "read_failed"]] as const) {
    const states: RuntimeState[] = [];
    const failure = new Error("private");
    const loader = f.reader(async () => { if (!result) throw failure; return result; }, !result ? { error: failure } : undefined)
      .forSelection(request, f.scope().signal, value => states.push(value));
    await loader.refresh();
    if (code) assert.deepEqual(states.at(-1), { kind: "error", code });
    else assert.equal((states.at(-1) as Extract<RuntimeState, { kind: "ready" }>).snapshot.entry, null);
    assert.equal(JSON.stringify(states).includes("private"), false);
  }
}));

test("late correlated host/runtime replacement invalidates captured capability; malformed evidence does not", () => Fixture.run(async f => {
  for (const patch of [
    { runtimeInstanceId: "replacement" }, { hostEpoch: "replacement" }, { status: "stale_epoch" },
    { runtimeInstanceId: "replacement", sessionId: "wrong" }, { hostEpoch: "\ud800" },
    { hostEpoch: "replacement", runtimeInstanceId: "" }, { runtimeInstanceId: "\ud800" },
    { status: "stale_epoch", runtimeInstanceId: "\ud800" },
    { status: "stale_epoch", entry: { attachmentGeneration: "" } as NonNullable<SessionRuntimeStateResponse["entry"]> },
  ]) {
    const pending = f.gate(response()), entered = f.gate<void>(undefined);
    const capability = createMutationCapability("host"); const states: RuntimeState[] = []; let calls = 0;
    capability.subscribe(() => { throw new Error("subscriber fault cannot turn replacement into read_failed"); });
    const reader = f.reader(() => { if (++calls === 1) return Promise.resolve(response()); entered.resolve(undefined); return pending.promise; });
    const scope = f.scope();
    const loader = reader.forSelection(request, scope.signal, value => states.push(value), capability.observe);
    await loader.refresh(); const old = loader.refresh(); await entered.promise;
    const displaced = loader.refresh(); scope.abort(); assert.equal(await displaced, "detached");
    const fresh = reader.forSelection(request, f.scope().signal, value => states.push(value), capability.observe);
    const count = states.length;
    pending.resolve(response(patch)); await old;
    const valid = Object.keys(patch).length === 1 && [patch.runtimeInstanceId, patch.hostEpoch, patch.status].some(value => value === "replacement" || value === "stale_epoch");
    assert.equal(capability.canMutate(), !valid);
    if (valid) {
      assert.equal(await fresh.refresh(), "blocked"); assert.equal(calls, 2);
      assert.equal(states.at(-1)?.kind, "error");
    } else assert.equal(states.length, count);
  }
}));

test("correlated backend error envelopes permit null runtime identity without fabricating current state", () => Fixture.run(async f => {
  for (const status of ["stale_epoch", "wire_limit", "closed", "read_failed"]) {
    const capability = createMutationCapability("host"); const states: RuntimeState[] = [];
    const loader = f.reader(async () => response({ status, runtimeInstanceId: null, coordinatorTransitionInProgress: null }))
      .forSelection(request, f.scope().signal, value => states.push(value), capability.observe);
    await loader.refresh();
    assert.deepEqual(states.at(-1), { kind: "error", code: status });
    assert.equal(capability.canMutate(), status !== "stale_epoch");
  }
}));

test("legacy opaque identities remain ordinal and do not become ordered attachment counters", () => Fixture.run(async f => {
  for (const sessionId of ["legacy/session", "\ufeff", "a\0b"]) {
    const states: RuntimeState[] = [];
    const loader = f.reader(async () => response({ sessionId, runtimeInstanceId: "legacy-runtime", entry: null }))
      .forSelection({ ...request, sessionId }, f.scope().signal, value => states.push(value));
    await loader.refresh(); assert.equal(states.at(-1)?.kind, "ready");
  }
}));

test("pending explicit refresh does not revive after detach or a rejected original waiter", () => Fixture.run(async f => {
  const release = f.gate<void>(undefined), entered = f.gate<void>(undefined), scope = f.scope();
  const failure = Object.assign(new Error("frontend timeout is not backend termination"), { name: "AbortError" });
  let calls = 0;
  const loader = f.reader(async () => { calls++; entered.resolve(undefined); await release.promise; throw failure; }, { error: failure })
    .forSelection(request, scope.signal, () => {});
  const first = loader.refresh(); await entered.promise;
  const pending = loader.refresh(); scope.abort(); assert.equal(await pending, "detached");
  release.resolve(undefined); assert.equal(await first, "detached"); assert.equal(calls, 1);
}));

test("obsolete runtime replacement blocks an already explicit pending refresh without issuing it", () => Fixture.run(async f => {
  const pending = f.gate(response()), entered = f.gate<void>(undefined);
  const capability = createMutationCapability("host"); let calls = 0;
  const states: RuntimeState[] = [];
  const loader = f.reader(() => { if (++calls === 1) return Promise.resolve(response()); entered.resolve(undefined); return pending.promise; })
    .forSelection(request, f.scope().signal, value => states.push(value), capability.observe);
  await loader.refresh();
  const original = loader.refresh(); await entered.promise;
  const latest = loader.refresh(); assert.equal(calls, 2);
  pending.resolve(response({ runtimeInstanceId: "replacement" }));
  assert.equal(await original, "blocked"); assert.equal(await latest, "blocked");
  assert.equal(calls, 2); assert.equal(capability.canMutate(), false);
  assert.deepEqual(states.at(-1), { kind: "error", code: "stale_runtime" });
}));

test("retained runtime drain joins original worker and late explicit successor, not merely refresh outcomes", () => Fixture.run(async f => {
  const firstReply = f.gate(response()), secondReply = f.gate(response());
  const firstEntered = f.gate<void>(undefined), secondEntered = f.gate<void>(undefined);
  let calls = 0, drained = false;
  const reader = f.reader(() => {
    if (++calls === 1) { firstEntered.resolve(undefined); return firstReply.promise; }
    secondEntered.resolve(undefined); return secondReply.promise;
  });
  const oldScope = f.scope();
  const first = reader.forSelection(request, oldScope.signal, () => {}).refresh();
  const drain = reader.settled();
  const observedDrain = f.launch("drain-observer", () => drain.then(() => { drained = true; }));
  await firstEntered.promise; oldScope.abort();
  const second = reader.forSelection(request, f.scope().signal, () => {}).refresh();
  assert.equal(reader.settled(), drain); assert.equal(calls, 1);
  firstReply.resolve(response()); assert.equal(await first, "detached");
  await secondEntered.promise;
  assert.equal(reader.settled(), drain); assert.equal(drained, false);
  secondReply.resolve(response()); assert.equal(await second, "settled");
  await observedDrain; assert.equal(drained, true); assert.equal(calls, 2);
}));

test("runtime drain retains a late failed original and joins it after pending refresh detaches", () => Fixture.run(async f => {
  const entered = f.gate<void>(undefined), release = f.gate<void>(undefined), scope = f.scope();
  const failure = Object.assign(new Error("late original cancellation-shaped failure"), { name: "AbortError" });
  let calls = 0, drained = false;
  const reader = f.reader(async () => { calls++; entered.resolve(undefined); await release.promise; throw failure; }, { error: failure });
  const selected = reader.forSelection(request, scope.signal, () => {});
  const first = selected.refresh(); const drain = reader.settled();
  const observedDrain = f.launch("drain-observer", () => drain.then(() => { drained = true; }));
  await entered.promise; const latest = selected.refresh(); scope.abort();
  assert.equal(await latest, "detached"); assert.equal(reader.settled(), drain); assert.equal(drained, false);
  release.resolve(undefined); assert.equal(await first, "detached"); await observedDrain;
  assert.equal(drained, true); assert.equal(calls, 1);
}));

test("loading reentrancy sees the retained drain and queues behind the original refresh", () => Fixture.run(async f => {
  const firstReply = f.gate(response()), secondReply = f.gate(response());
  const firstEntered = f.gate<void>(undefined), secondEntered = f.gate<void>(undefined);
  let calls = 0, reentered = false;
  const reader = f.reader(() => {
    if (++calls === 1) { firstEntered.resolve(undefined); return firstReply.promise; }
    secondEntered.resolve(undefined); return secondReply.promise;
  });
  let selected!: ReturnType<typeof reader.forSelection>;
  let capturedDrain: Promise<void> | undefined;
  let successor: ReturnType<typeof selected.refresh> | undefined;
  selected = reader.forSelection(request, f.scope().signal, state => {
    if (state.kind !== "loading" || reentered) return;
    reentered = true;
    capturedDrain = reader.settled();
    successor = selected.refresh();
  });
  const idle = reader.settled();
  const first = selected.refresh(); const drain = reader.settled();
  assert.notEqual(drain, idle); assert.equal(capturedDrain, drain);
  await firstEntered.promise; assert.equal(calls, 1);
  firstReply.resolve(response()); assert.equal(await first, "superseded");
  await secondEntered.promise; assert.equal(reader.settled(), drain);
  secondReply.resolve(response()); assert.equal(await successor, "settled"); await drain;
  assert.equal(calls, 2); assert.equal(reader.notificationFailure(), undefined);
}));

test("synchronous invoke failure and reentrant explicit refresh share the retained original drain", () => Fixture.run(async f => {
  const reply = f.gate(response()), entered = f.gate<void>(undefined);
  const failure = new Error("synchronous invoke failure"); let calls = 0;
  let reader!: ReturnType<typeof f.reader>;
  let selected!: ReturnType<typeof reader.forSelection>;
  let successor: ReturnType<typeof selected.refresh> | undefined;
  let capturedDrain: Promise<void> | undefined;
  reader = f.reader(() => {
    if (++calls === 1) {
      capturedDrain = reader.settled();
      successor = selected.refresh();
      throw failure;
    }
    entered.resolve(undefined); return reply.promise;
  }, { error: failure });
  selected = reader.forSelection(request, f.scope().signal, () => {});
  const first = selected.refresh(); const drain = reader.settled();
  assert.equal(await first, "superseded"); await entered.promise;
  assert.equal(capturedDrain, drain); assert.equal(reader.settled(), drain); assert.equal(calls, 2);
  reply.resolve(response()); assert.equal(await successor, "settled"); await drain;
  const failedInvoke = f.originals.find(record => record.stage === "invoke" && record.error === failure);
  assert.equal(failedInvoke?.synchronousFailure, true); assert.equal(failedInvoke?.outcome, "failure");
  assert.equal(failedInvoke?.original, undefined); // No original RPC promise was returned by the throwing call.
  assert.equal(calls, 2);
}));
