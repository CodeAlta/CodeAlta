import assert from "node:assert/strict";
import test from "node:test";
import type { SessionPermissionCommand, SessionPermissionResolution, SessionPermissionsPage } from "#neoastra";
import { createPermissionReviewer, type PermissionReviewState } from "./sessionPermissions";

const epoch = "abcdefab-1234-5678-9abc-abcdefabcdef";
const runtime = "00000000-0000-0000-0000-000000000001";
const request = { expectedHostEpoch: epoch, sessionId: "selected" };
function command(): SessionPermissionCommand {
  return { handle: { operationId: epoch, runtimeInstanceId: runtime, attachmentGeneration: "9223372036854775807",
    sessionId: "selected", runId: null, interactionId: "interaction", attemptId: epoch },
    providerId: "fake", command: "inert command\ncomplete", workingDirectory: "Q:\\fixture", reason: null };
}
function page(entries = [command()]): SessionPermissionsPage {
  return { status: "ok", hostEpoch: epoch, sessionId: "selected", entries, hasMore: false };
}
function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: Error) => void;
  const promise = new Promise<T>((done, fail) => { resolve = done; reject = fail; });
  return { promise, resolve, reject };
}
function ready(states: PermissionReviewState[]): Extract<PermissionReviewState, { kind: "ready" }> {
  const value = states.at(-1); assert.equal(value?.kind, "ready");
  return value as Extract<PermissionReviewState, { kind: "ready" }>;
}
const forbidden = async (): Promise<SessionPermissionResolution> => { throw Error("Unexpected resolve"); };

test("permission review is manual, immutable and uses the latest request only", async () => {
  const first = deferred<SessionPermissionsPage>(), second = deferred<SessionPermissionsPage>();
  let calls = 0; const states: PermissionReviewState[] = [];
  const reviewer = createPermissionReviewer(() => ++calls === 1 ? first.promise : second.promise, forbidden);
  const selection = reviewer.forSelection(request, new AbortController().signal, value => states.push(value));
  assert.equal(calls, 0);
  const a = selection.refresh(), b = selection.refresh();
  const result = { ...page(), hasMore: true };
  second.resolve(result); await b;
  // Adversarial transport mutation only: generated DTOs remain readonly to ordinary callers.
  const mutableTransport = result.entries[0] as { command: string; handle: { attemptId: string } };
  mutableTransport.command = "mutated transport object";
  mutableTransport.handle.attemptId = runtime;
  first.resolve({ ...page(), hostEpoch: "stale delayed host" }); await a;
  assert.equal(states.length, 3);
  assert.equal(ready(states).entries[0].command, "inert command\ncomplete");
  assert.equal(ready(states).entries[0].handle.attemptId, epoch);
  assert.equal(ready(states).hasMore, true);
  assert.equal(Object.isFrozen(ready(states).entries), true);
  assert.equal(Object.isFrozen(ready(states).entries[0].handle), true);
});

test("permission selection abort discards delayed read success and failure", async () => {
  for (const fail of [false, true]) {
    const pending = deferred<SessionPermissionsPage>(); const controller = new AbortController();
    const states: PermissionReviewState[] = [];
    const reviewer = createPermissionReviewer(() => pending.promise, forbidden);
    const scope = reviewer.forSelection(request, controller.signal, value => states.push(value));
    const work = scope.refresh(); controller.abort();
    if (fail) pending.reject(Error("private")); else pending.resolve(page());
    await work; await scope.refresh();
    assert.deepEqual(states, [{ kind: "loading" }]);
  }
});

test("new permission selection fences the old reader even without transport abort", async () => {
  const pending = deferred<SessionPermissionsPage>(); const states: PermissionReviewState[] = [];
  const reviewer = createPermissionReviewer(req => req.sessionId === "selected" ? pending.promise : Promise.resolve({ ...page([]), sessionId: "other" }), forbidden);
  const old = reviewer.forSelection(request, new AbortController().signal, value => states.push(value));
  const work = old.refresh();
  await reviewer.forSelection({ ...request, sessionId: "other" }, new AbortController().signal, value => states.push(value)).refresh();
  pending.resolve({ ...page(), hostEpoch: "wrong" }); await work;
  assert.equal(ready(states).entries.length, 0);
  assert.equal(states.length, 3);
});

test("permission stale epoch and runtime identities latch reload across selections", async () => {
  for (const code of ["stale_epoch", "stale_runtime"]) {
    let calls = 0; const states: PermissionReviewState[] = [];
    const reviewer = createPermissionReviewer(async () => {
      calls++;
      if (calls === 1) return page();
      if (code === "stale_epoch") return { ...page(), hostEpoch: "other" };
      const entry = command();
      return page([{ ...entry, handle: { ...entry.handle, runtimeInstanceId: epoch } }]);
    }, forbidden);
    const scope = reviewer.forSelection(request, new AbortController().signal, value => states.push(value));
    await scope.refresh(); await scope.refresh(); await scope.refresh();
    await reviewer.forSelection(request, new AbortController().signal, value => states.push(value)).refresh();
    assert.equal(calls, 2);
    assert.deepEqual(states.at(-1), { kind: "error", code, reloadRequired: true });
  }
});

test("malformed command windows are rejected whole without enabling approval", async () => {
  const entry = command();
  const invalid = [page([entry, entry]), { ...page(), sessionId: "other" }, page(Array.from({ length: 5 }, command)),
    page([{ ...entry, command: "x".repeat(4097) }]), page([{ ...entry, workingDirectory: "\ud800" }]),
    page([{ ...entry, reason: "x".repeat(1025) }]), page([{ ...entry, providerId: "bad\n" }]),
    page([{ ...entry, handle: { ...entry.handle, attachmentGeneration: "01" } }]),
    page([{ ...entry, handle: { ...entry.handle, attachmentGeneration: "9223372036854775808" } }]),
    page([{ ...entry, handle: { ...entry.handle, operationId: epoch.toUpperCase() } }]),
    page([{ ...entry, handle: { ...entry.handle, runId: "\0" } }])];
  for (const result of invalid) {
    const states: PermissionReviewState[] = []; let resolutions = 0;
    const scope = createPermissionReviewer(async () => result, async () => { resolutions++; throw Error("forbidden"); })
      .forSelection(request, new AbortController().signal, value => states.push(value));
    await scope.refresh(); await scope.decide(entry, "allow_once");
    assert.equal(resolutions, 0);
    assert.deepEqual(states.at(-1), { kind: "error", code: "invalid_response", reloadRequired: false });
  }
});

test("Allow once Deny Cancel forward exact handles and require fresh review after a result", async () => {
  for (const decision of ["allow_once", "deny", "cancel"] as const) {
    const states: PermissionReviewState[] = []; let calls = 0;
    const scope = createPermissionReviewer(async () => page(), async (actual, options) => {
      calls++; assert.deepEqual(actual, { expectedHostEpoch: epoch, handle: command().handle, decision });
      assert.equal(options.timeoutMilliseconds, 8_000);
      return { status: decision === "deny" ? "rejected" : "resolved", hostEpoch: epoch, handle: actual.handle };
    }).forSelection(request, new AbortController().signal, value => states.push(value));
    await scope.refresh();
    const entry = ready(states).entries[0];
    await scope.decide(command(), decision); assert.equal(calls, 0); // Forged/copy row is not the displayed review.
    await scope.decide(entry, decision); await scope.decide(entry, decision);
    assert.equal(calls, 1);
    assert.deepEqual(states.at(-1), { kind: "result", code: decision === "deny" ? "rejected" : "resolved" });
    await scope.refresh(); await scope.decide(entry, decision); assert.equal(calls, 1);
  }
});

test("refresh removes old review authority and double clicks cannot race a pending resolution", async () => {
  const pending = deferred<SessionPermissionResolution>(); const states: PermissionReviewState[] = [];
  let lists = 0, resolves = 0;
  const scope = createPermissionReviewer(async () => { lists++; return page(); }, () => { resolves++; return pending.promise; })
    .forSelection(request, new AbortController().signal, value => states.push(value));
  await scope.refresh(); const old = ready(states).entries[0];
  const refresh = scope.refresh(); await scope.decide(old, "allow_once"); await refresh;
  assert.equal(resolves, 0);
  const entry = ready(states).entries[0];
  const work = scope.decide(entry, "allow_once");
  try {
    await scope.decide(entry, "deny"); await scope.refresh();
    assert.equal(resolves, 1); assert.equal(lists, 2);
  } finally {
    pending.resolve({ status: "resolved", hostEpoch: epoch, handle: entry.handle }); await work;
  }
  assert.deepEqual(states.at(-1), { kind: "result", code: "resolved" });
});

test("permission response identity mismatch or transport failure is uncertain and never retried", async () => {
  for (const outcome of ["wrong_attempt", "wrong_run", "wrong_operation", "wrong_attachment", "wrong_session", "wrong_runtime", "failure", "uncertain"]) {
    const states: PermissionReviewState[] = []; let calls = 0, lists = 0;
    const reviewer = createPermissionReviewer(async () => { lists++; return page(); }, async req => {
      calls++;
      if (outcome === "failure") throw Error("private transport");
      const handle = { ...req.handle };
      if (outcome === "wrong_attempt") handle.attemptId = runtime;
      if (outcome === "wrong_run") handle.runId = "run";
      if (outcome === "wrong_operation") handle.operationId = runtime;
      if (outcome === "wrong_attachment") handle.attachmentGeneration = "1";
      if (outcome === "wrong_session") handle.sessionId = "other";
      if (outcome === "wrong_runtime") handle.runtimeInstanceId = epoch;
      return { status: outcome === "uncertain" ? "uncertain" : "resolved", hostEpoch: epoch, handle };
    });
    const scope = reviewer.forSelection(request, new AbortController().signal, value => states.push(value));
    await scope.refresh(); const entry = ready(states).entries[0];
    await scope.decide(entry, "allow_once"); await scope.refresh(); await scope.decide(entry, "allow_once");
    await reviewer.forSelection(request, new AbortController().signal, value => states.push(value)).refresh();
    assert.equal(calls, 1);
    assert.equal(lists, 1);
    assert.deepEqual(states.at(-1), { kind: "error", code: "uncertain", reloadRequired: true });
    assert.equal(JSON.stringify(states).includes("private"), false);
  }
});

test("detach during decision keeps uncertainty across selections and discards late responses", async () => {
  for (const failure of [false, true]) {
    const pending = deferred<SessionPermissionResolution>(); const states: PermissionReviewState[] = [];
    const controller = new AbortController();
    const reviewer = createPermissionReviewer(async () => page(), () => pending.promise);
    const old = reviewer.forSelection(request, controller.signal, value => states.push(value));
    await old.refresh(); const entry = ready(states).entries[0];
    const work = old.decide(entry, "allow_once"); controller.abort();
    const fresh = reviewer.forSelection(request, new AbortController().signal, value => states.push(value));
    await fresh.refresh(); const count = states.length;
    if (failure) pending.reject(Error("private")); else pending.resolve({ status: "resolved", hostEpoch: epoch, handle: entry.handle });
    await work;
    assert.equal(states.length, count);
    assert.deepEqual(states.at(-1), { kind: "error", code: "uncertain", reloadRequired: true });
  }
});

test("unsettled resolution cannot revive refresh or old review actions after selection uncertainty", async () => {
  for (const abortTransport of [false, true]) {
    const pending = deferred<SessionPermissionResolution>(); const states: PermissionReviewState[] = [];
    const controller = new AbortController();
    let lists = 0, resolves = 0, settled = false;
    const reviewer = createPermissionReviewer(async () => { lists++; return page(); }, async () => {
      resolves++;
      const result = await pending.promise;
      settled = true;
      return result;
    });
    const old = reviewer.forSelection(request, controller.signal, value => states.push(value));
    await old.refresh(); const entry = ready(states).entries[0];
    const work = old.decide(entry, "allow_once");
    try {
      await old.refresh(); await old.decide(entry, "deny");
      assert.equal(lists, 1); assert.equal(resolves, 1);
      assert.deepEqual(states.at(-1), { kind: "resolving" });
      if (abortTransport) controller.abort();
      const fresh = reviewer.forSelection(request, new AbortController().signal, value => states.push(value));
      await fresh.refresh(); await fresh.refresh();
      await fresh.decide(entry, "cancel"); await old.decide(entry, "allow_once");
      assert.equal(settled, false); // Observe the noncooperative transport without waiting for it or using a timer.
      assert.equal(lists, 1); assert.equal(resolves, 1);
      assert.deepEqual(states.at(-1), { kind: "error", code: "uncertain", reloadRequired: true });
    } finally {
      // "Unsettled" is the observed state above, not abandoned fixture work. Join even on assertion failure.
      pending.resolve({ status: "resolved", hostEpoch: epoch, handle: entry.handle });
      await work;
      controller.abort();
    }
    assert.deepEqual(states.at(-1), { kind: "error", code: "uncertain", reloadRequired: true });
  }
});

test("permission resolution stale host latches reload instead of accepting an old success", async () => {
  const states: PermissionReviewState[] = [];
  const scope = createPermissionReviewer(async () => page(), async req => ({ status: "resolved", hostEpoch: "new", handle: req.handle }))
    .forSelection(request, new AbortController().signal, value => states.push(value));
  await scope.refresh(); await scope.decide(ready(states).entries[0], "allow_once");
  assert.deepEqual(states.at(-1), { kind: "error", code: "stale_epoch", reloadRequired: true });
});

test("disabled empty and read failure states never imply execution or completion", async () => {
  for (const status of ["disabled", "empty", "failure"]) {
    const states: PermissionReviewState[] = [];
    const scope = createPermissionReviewer(async () => {
      if (status === "failure") throw Error("private");
      return status === "disabled" ? { ...page([]), status: "disabled" } : page([]);
    }, forbidden).forSelection(request, new AbortController().signal, value => states.push(value));
    await scope.refresh();
    if (status === "empty") assert.equal(ready(states).entries.length, 0);
    else assert.deepEqual(states.at(-1), { kind: "error", code: status === "failure" ? "read_failed" : "disabled", reloadRequired: false });
  }
});
