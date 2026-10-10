import assert from "node:assert/strict";
import test from "node:test";
import type { SessionPermissionCommand, SessionPermissionResolution, SessionPermissionsPage } from "#neoastra";
import { createPermissionReviewer, type PermissionReviewState } from "./sessionPermissions";

const epoch = "abcdefab-1234-5678-9abc-abcdefabcdef";
const runtime = "00000000-0000-0000-0000-000000000001";
const otherEpoch = "00000000-0000-0000-0000-000000000002";
const request = Object.freeze({ expectedHostEpoch: epoch, sessionId: "selected" });
function command(): SessionPermissionCommand {
  return { handle: { operationId: epoch, runtimeInstanceId: runtime, attachmentGeneration: "9223372036854775807",
    sessionId: "selected", runId: null, interactionId: "interaction", attemptId: epoch },
    providerId: "fake", kind: "commandExecution", command: "inert command\ncomplete", workingDirectory: "Q:\\fixture",
    grantRoot: null, reason: null };
}
function fileChange(): SessionPermissionCommand {
  return { ...command(), kind: "fileChange", command: null, workingDirectory: null, grantRoot: "Q:\\fixture" };
}
function page(entries = [command()]): SessionPermissionsPage {
  return { status: "ok", hostEpoch: epoch, sessionId: "selected", entries, hasMore: false };
}
function ready(states: PermissionReviewState[]): Extract<PermissionReviewState, { kind: "ready" }> {
  const value = states.at(-1); assert.equal(value?.kind, "ready");
  return value as Extract<PermissionReviewState, { kind: "ready" }>;
}
const forbidden = async (): Promise<SessionPermissionResolution> => { throw Error("Unexpected resolve"); };

// Fixture-local ownership only: no host, roots, providers, timers used for ordering, or detached work.
class Fixture {
  private readonly work = new Map<Promise<unknown>, Promise<void>>();
  private readonly failures: unknown[] = [];
  private readonly expected = new Set<Error>();
  private readonly releases: (() => void)[] = [];
  private readonly controllers: AbortController[] = [];
  private cleaning = false;

  keep<T>(task: Promise<T>): Promise<T> {
    if (!this.work.has(task)) {
      const observer = task.then(() => {}, error => {
        if (!this.expected.has(error)) this.failures.push(error);
      });
      this.work.set(task, observer); // Keep the original and its rejection observer, not just a timeout race.
    }
    return task;
  }
  failure(message: string): Error {
    const error = Error(message);
    this.expected.add(error); // Only exact scripted transport failures are expected.
    return error;
  }
  gate<T>(cleanupValue: T) {
    let resolve!: (value: T) => void;
    let reject!: (reason: Error) => void;
    const promise = this.keep(new Promise<T>((done, fail) => { resolve = done; reject = fail; }));
    const release = () => resolve(cleanupValue);
    this.releases.push(release);
    if (this.cleaning) release();
    return { promise, resolve, reject };
  }
  controller(): AbortController {
    const controller = new AbortController();
    this.controllers.push(controller);
    if (this.cleaning) controller.abort();
    return controller;
  }
  reviewer(list: Parameters<typeof createPermissionReviewer>[0], resolve: Parameters<typeof createPermissionReviewer>[1] = forbidden) {
    const invoke = <T>(call: () => Promise<T>) => {
      try { return this.keep(call()); }
      catch (error) { return this.keep(Promise.reject<T>(error)); }
    };
    return createPermissionReviewer((req, options) => invoke(() => list(req, options)),
      (req, options) => invoke(() => resolve(req, options)));
  }
  wait<T>(task: Promise<T>): Promise<T> {
    this.keep(task);
    return this.keep(new Promise<T>((resolve, reject) => {
      const timer = setTimeout(() => {
        clearTimeout(timer);
        const error = Error("Fixture deadline; original work is not confirmed terminated");
        this.failures.push(error); // A caught timeout still permanently fails this fixture.
        reject(error);
      }, 5_000);
      this.keep(task.then(value => { clearTimeout(timer); resolve(value); },
        error => { clearTimeout(timer); reject(error); }));
    }));
  }
  static async run(body: (fixture: Fixture) => Promise<void>): Promise<void> {
    const f = new Fixture();
    let bodySettled = false;
    const originalBody = f.keep(Promise.resolve().then(() => f.keep(body(f))));
    f.keep(originalBody.then(() => { bodySettled = true; }, () => { bodySettled = true; }));
    try { await f.wait(originalBody); }
    catch (error) { f.failures.push(error); }
    finally {
      f.cleaning = true;
      // Release/cancel independently before any dependent join. Late registrations self-clean too.
      for (const release of f.releases) {
        try { release(); } catch (error) { f.failures.push(error); }
      }
      for (const controller of f.controllers) {
        try { controller.abort(); } catch (error) { f.failures.push(error); }
      }
      try { await f.wait(originalBody); }
      catch (error) { f.failures.push(error); }
      // Take the final snapshot only after the body can no longer publish work. A timeout is not that proof.
      if (bodySettled) {
        // No resources are released on failure. Joining observers joins their original transports/wrappers.
        try { await f.wait(Promise.all([...f.work.values()])); }
        catch (error) { f.failures.push(error); }
      }
    }
    if (f.failures.length) throw Object.assign(new AggregateError([...new Set(f.failures)],
      "Permission fixture failed; uncertain work remains retained"), { retainedFixture: f });
  }
}

function resolution(status = "resolved"): SessionPermissionResolution {
  return { status, hostEpoch: epoch, handle: command().handle };
}
function origin(decision = "allow_once", entry = command()) {
  return { expectedHostEpoch: epoch, handle: entry.handle, decision };
}

test("permission review is manual, immutable and uses the latest request only", () => Fixture.run(async f => {
  const first = f.gate(page()), second = f.gate(page());
  let calls = 0; const states: PermissionReviewState[] = [];
  const reviewer = f.reviewer(() => ++calls === 1 ? first.promise : second.promise);
  const selection = reviewer.forSelection(request, f.controller().signal, value => states.push(value));
  assert.equal(calls, 0);
  const a = f.keep(selection.refresh()), b = f.keep(selection.refresh());
  const result = { ...page(), hasMore: true };
  second.resolve(result); await f.wait(b);
  // Adversarial transport mutation only: generated DTOs remain readonly to ordinary callers.
  const mutableTransport = result.entries[0] as { command: string; handle: { attemptId: string } };
  mutableTransport.command = "mutated transport object";
  mutableTransport.handle.attemptId = runtime;
  first.resolve({ ...page(), hostEpoch: otherEpoch }); await f.wait(a);
  assert.equal(states.length, 3);
  assert.equal(ready(states).entries[0].command, "inert command\ncomplete");
  assert.equal(ready(states).entries[0].handle.attemptId, epoch);
  assert.equal(ready(states).hasMore, true);
  assert.equal(Object.isFrozen(ready(states).entries), true);
  assert.equal(Object.isFrozen(ready(states).entries[0].handle), true);
}));

test("permission selection abort discards delayed read success and failure", () => Fixture.run(async f => {
  for (const fail of [false, true]) {
    const pending = f.gate(page()); const controller = f.controller();
    const states: PermissionReviewState[] = [];
    const reviewer = f.reviewer((_, options) => {
      assert.equal(options.signal, controller.signal); return pending.promise;
    });
    const scope = reviewer.forSelection(request, controller.signal, value => states.push(value));
    const work = f.keep(scope.refresh()); controller.abort();
    if (fail) pending.reject(f.failure("private")); else pending.resolve(page());
    await f.wait(work); await f.wait(scope.refresh());
    assert.deepEqual(states, [{ kind: "loading" }]);
  }
}));

test("new permission selection fences the old reader even without transport abort", () => Fixture.run(async f => {
  const pending = f.gate(page()); const states: PermissionReviewState[] = [];
  const reviewer = f.reviewer(req => req.sessionId === "selected" ? pending.promise : Promise.resolve({ ...page([]), sessionId: "other" }));
  const old = reviewer.forSelection(request, f.controller().signal, value => states.push(value));
  const work = f.keep(old.refresh());
  await f.wait(reviewer.forSelection({ ...request, sessionId: "other" }, f.controller().signal, value => states.push(value)).refresh());
  pending.resolve({ ...page(), hostEpoch: otherEpoch }); await f.wait(work);
  assert.equal(ready(states).entries.length, 0);
  assert.equal(states.length, 3);
}));

test("permission stale epoch and runtime identities latch reload across selections", () => Fixture.run(async f => {
  for (const code of ["stale_epoch", "stale_runtime"]) {
    let calls = 0; const states: PermissionReviewState[] = [];
    const reviewer = f.reviewer(async () => {
      calls++;
      if (calls === 1) return page();
      if (code === "stale_epoch") return { ...page(), hostEpoch: otherEpoch };
      const entry = command();
      return page([{ ...entry, handle: { ...entry.handle, runtimeInstanceId: epoch } }]);
    }, forbidden);
    const scope = reviewer.forSelection(request, f.controller().signal, value => states.push(value));
    await f.wait(scope.refresh()); await f.wait(scope.refresh()); await f.wait(scope.refresh());
    await f.wait(reviewer.forSelection(request, f.controller().signal, value => states.push(value)).refresh());
    assert.equal(calls, 2);
    assert.deepEqual(states.at(-1), { kind: "error", code, reloadRequired: true });
  }
}));

test("malformed command windows are rejected whole without enabling approval", () => Fixture.run(async f => {
  const entry = command();
  const invalid = [page([entry, entry]), { ...page(), sessionId: "other" }, page(Array.from({ length: 5 }, command)),
    page([{ ...entry, command: "x".repeat(4097) }]), page([{ ...entry, workingDirectory: "\ud800" }]),
    page([{ ...entry, reason: "x".repeat(1025) }]), page([{ ...entry, providerId: "bad\n" }]),
    page([{ ...entry, handle: { ...entry.handle, attachmentGeneration: "01" } }]),
    page([{ ...entry, handle: { ...entry.handle, attachmentGeneration: "9223372036854775808" } }]),
    page([{ ...entry, handle: { ...entry.handle, operationId: epoch.toUpperCase() } }]),
    page([{ ...entry, handle: { ...entry.handle, runId: "\0" } }]),
    // Neither kind may carry the other's fields, and neither may arrive without its own.
    page([{ ...entry, kind: "fileChange" }]), page([{ ...entry, grantRoot: "Q:\\fixture" }]),
    page([{ ...fileChange(), grantRoot: null }]), page([{ ...fileChange(), command: "inert" }]),
    page([{ ...fileChange(), workingDirectory: "Q:\\fixture" }]), page([{ ...entry, kind: "somethingElse" }])];
  for (const result of invalid) {
    const states: PermissionReviewState[] = []; let resolutions = 0;
    const scope = f.reviewer(async () => result, async () => { resolutions++; throw Error("forbidden"); })
      .forSelection(request, f.controller().signal, value => states.push(value));
    await f.wait(scope.refresh()); await f.wait(scope.decide(entry, "allow_once"));
    assert.equal(resolutions, 0);
    assert.deepEqual(states.at(-1), { kind: "error", code: "invalid_response", reloadRequired: false });
  }
}));

test("a file change is reviewed like a command, by the root it asks to write under", () => Fixture.run(async f => {
  const entry = fileChange();
  const states: PermissionReviewState[] = []; const handles: unknown[] = [];
  const scope = f.reviewer(async () => page([entry]),
      async requested => { handles.push(requested.handle); return { status: "resolved", hostEpoch: epoch, handle: requested.handle }; })
    .forSelection(request, f.controller().signal, value => states.push(value));
  await f.wait(scope.refresh());
  const listed = ready(states).entries;
  assert.equal(listed.length, 1);
  assert.deepEqual([listed[0]!.kind, listed[0]!.grantRoot, listed[0]!.command], ["fileChange", "Q:\\fixture", null]);
  await f.wait(scope.decide(listed[0]!, "deny"));
  assert.equal(handles.length, 1, "a file change reaches the host like a command does");
}));

test("Allow once Deny Cancel forward exact handles and require fresh review after a result", () => Fixture.run(async f => {
  for (const decision of ["allow_once", "deny", "cancel"] as const) {
    const states: PermissionReviewState[] = []; let calls = 0;
    const scope = f.reviewer(async () => page(), async (actual, options) => {
      calls++; assert.deepEqual(actual, { expectedHostEpoch: epoch, handle: command().handle, decision });
      assert.equal(options.timeoutMilliseconds, 8_000);
      return { status: decision === "deny" ? "rejected" : "resolved", hostEpoch: epoch, handle: actual.handle };
    }).forSelection(request, f.controller().signal, value => states.push(value));
    await f.wait(scope.refresh());
    const entry = ready(states).entries[0];
    await f.wait(scope.decide(command(), decision)); assert.equal(calls, 0); // Forged/copy row is not the displayed review.
    await f.wait(scope.decide(entry, decision)); await f.wait(scope.decide(entry, decision));
    assert.equal(calls, 1);
    assert.deepEqual(states.at(-1), { kind: "result", code: decision === "deny" ? "rejected" : "resolved" });
    assert.deepEqual(scope.observeDecision(), { origin: origin(decision), state: decision === "deny" ? "rejected" : "resolved", code: null });
    await f.wait(scope.refresh()); await f.wait(scope.decide(entry, decision)); assert.equal(calls, 1);
  }
}));

test("refresh removes old review authority and double clicks cannot race a pending resolution", () => Fixture.run(async f => {
  const pending = f.gate(resolution()); const states: PermissionReviewState[] = [];
  let lists = 0, resolves = 0;
  const scope = f.reviewer(async () => { lists++; return page(); }, () => { resolves++; return pending.promise; })
    .forSelection(request, f.controller().signal, value => states.push(value));
  await f.wait(scope.refresh()); const old = ready(states).entries[0];
  const refresh = f.keep(scope.refresh()); await f.wait(scope.decide(old, "allow_once")); await f.wait(refresh);
  assert.equal(resolves, 0);
  const entry = ready(states).entries[0];
  const work = f.keep(scope.decide(entry, "allow_once"));
  await f.wait(scope.decide(entry, "deny")); await f.wait(scope.refresh());
  assert.equal(resolves, 1); assert.equal(lists, 2);
  // Fixture.run's finally releases this gate and cancels all selections even if an earlier assertion fails.
  pending.resolve({ status: "resolved", hostEpoch: epoch, handle: entry.handle }); await f.wait(work);
  assert.deepEqual(states.at(-1), { kind: "result", code: "resolved" });
}));

test("permission response identity mismatch or transport failure is uncertain and never retried", () => Fixture.run(async f => {
  for (const outcome of ["wrong_attempt", "wrong_run", "wrong_operation", "wrong_attachment", "wrong_session", "wrong_runtime", "wrong_interaction", "failure", "uncertain"]) {
    const states: PermissionReviewState[] = []; let calls = 0, lists = 0;
    const reviewer = f.reviewer(async () => { lists++; return page(); }, async req => {
      calls++;
      if (outcome === "failure") throw f.failure("private transport");
      const handle = { ...req.handle };
      if (outcome === "wrong_attempt") handle.attemptId = runtime;
      if (outcome === "wrong_run") handle.runId = "run";
      if (outcome === "wrong_operation") handle.operationId = runtime;
      if (outcome === "wrong_attachment") handle.attachmentGeneration = "1";
      if (outcome === "wrong_session") handle.sessionId = "other";
      if (outcome === "wrong_runtime") handle.runtimeInstanceId = epoch;
      if (outcome === "wrong_interaction") handle.interactionId = "other";
      return { status: outcome === "uncertain" ? "uncertain" : "resolved", hostEpoch: epoch, handle };
    });
    const scope = reviewer.forSelection(request, f.controller().signal, value => states.push(value));
    await f.wait(scope.refresh()); const entry = ready(states).entries[0];
    await f.wait(scope.decide(entry, "allow_once")); await f.wait(scope.refresh()); await f.wait(scope.decide(entry, "allow_once"));
    assert.deepEqual(reviewer.readOriginal()?.state, "error");
    assert.equal(reviewer.readOriginal()?.code, "uncertain");
    assert.equal(scope.observeDecision()?.state, "error");
    assert.equal(scope.observeDecision()?.code, "uncertain");
    const fresh = reviewer.forSelection(request, f.controller().signal, value => states.push(value));
    assert.equal(fresh.observeDecision()?.code, "uncertain");
    await f.wait(fresh.refresh()); await f.wait(fresh.decide(entry, "deny"));
    assert.equal(calls, 1);
    assert.equal(lists, 1);
    assert.deepEqual(states.at(-1), { kind: "error", code: "uncertain", reloadRequired: true });
    assert.equal(JSON.stringify(states).includes("private"), false);
  }
}));

test("read-only permission snapshot retains command and terminal original without acknowledging its review gate", () => Fixture.run(async f => {
  const pending = f.gate(resolution()); const states: PermissionReviewState[] = [];
  let lists = 0, resolves = 0, notices = 0;
  const reviewer = f.reviewer(async () => { lists++; return page(); }, () => { resolves++; return pending.promise; });
  reviewer.subscribe(() => { notices++; });
  const old = reviewer.forSelection(request, f.controller().signal, value => states.push(value));
  await f.wait(old.refresh()); const entry = ready(states).entries[0];
  const work = f.keep(old.decide(entry, "allow_once"));
  assert.equal(reviewer.readOriginal()?.state, "pending");
  assert.equal(reviewer.readOriginal()?.command, entry.command);
  assert.equal(reviewer.readOriginal()?.workingDirectory, entry.workingDirectory);
  const revision = notices;
  pending.resolve(resolution()); await f.wait(work);
  assert.equal(notices, revision + 1);
  assert.equal(reviewer.readOriginal()?.state, "resolved");
  const next = reviewer.forSelection(request, f.controller().signal, value => states.push(value));
  await f.wait(next.refresh()); await f.wait(next.decide(entry, "deny"));
  assert.equal(lists, 1); assert.equal(resolves, 1);
  assert.equal(reviewer.readOriginal()?.state, "resolved");
  // Only explicit observation is allowed to unlock fresh review; archived projection does not call it.
  assert.equal(next.observeDecision()?.state, "resolved");
  await f.wait(next.refresh()); assert.equal(lists, 2);
}));

// Replaces historical "detach during decision keeps uncertainty across selections and discards late responses":
// obsolete publication is still suppressed, but an exact original reply is retained rather than discarded.
test("detach during decision retains exact late responses and keeps genuine uncertainty locked", () => Fixture.run(async f => {
  for (const outcome of ["resolved", "rejected", "failure"]) {
    const failure = outcome === "failure";
    const pending = f.gate(resolution()); const states: PermissionReviewState[] = [];
    const controller = f.controller();
    let decisionSignal: AbortSignal | undefined;
    const reviewer = f.reviewer(async () => page(), (_, options) => {
      decisionSignal = options.signal;
      assert.notEqual(options.signal, controller.signal);
      assert.equal(options.timeoutMilliseconds, 8_000);
      return pending.promise;
    });
    const old = reviewer.forSelection(request, controller.signal, value => states.push(value));
    await f.wait(old.refresh()); const entry = ready(states).entries[0];
    const work = f.keep(old.decide(entry, "allow_once")); controller.abort();
    const fresh = reviewer.forSelection(request, f.controller().signal, value => states.push(value));
    assert.equal(decisionSignal?.aborted, false);
    assert.equal(old.observeDecision(), null); // An obsolete closure cannot acknowledge the record.
    assert.deepEqual(fresh.observeDecision(), { origin: origin(), state: "pending", code: null });
    await f.wait(fresh.refresh()); const count = states.length;
    if (failure) pending.reject(f.failure("private")); else pending.resolve(resolution(outcome));
    await f.wait(work);
    assert.equal(states.length, count);
    assert.deepEqual(fresh.observeDecision(), { origin: origin(), state: failure ? "error" : outcome, code: failure ? "uncertain" : null });
    if (failure) {
      await f.wait(fresh.refresh());
      assert.deepEqual(states.at(-1), { kind: "error", code: "uncertain", reloadRequired: true });
    }
  }
}));

// Replaces historical "unsettled resolution cannot revive refresh or old review actions after selection uncertainty".
// Keep both abort/no-abort branches and held original wrapper; pending observation is not acknowledgment.
test("unsettled resolution cannot revive refresh or old review actions after selection loss", () => Fixture.run(async f => {
  for (const abortSelection of [false, true]) {
    const pending = f.gate(resolution()); const states: PermissionReviewState[] = [];
    const controller = f.controller();
    let lists = 0, resolves = 0, settled = false;
    const reviewer = f.reviewer(async () => { lists++; return page(); }, async () => {
      resolves++;
      const result = await pending.promise;
      settled = true;
      return result;
    });
    const old = reviewer.forSelection(request, controller.signal, value => states.push(value));
    await f.wait(old.refresh()); const entry = ready(states).entries[0];
    const work = f.keep(old.decide(entry, "allow_once"));
    await f.wait(old.refresh()); await f.wait(old.decide(entry, "deny"));
    assert.equal(lists, 1); assert.equal(resolves, 1);
    assert.deepEqual(states.at(-1), { kind: "resolving" });
    if (abortSelection) controller.abort();
    const fresh = reviewer.forSelection(request, f.controller().signal, value => states.push(value));
    assert.equal(fresh.observeDecision()?.state, "pending");
    assert.equal(fresh.observeDecision()?.state, "pending");
    await f.wait(fresh.refresh()); await f.wait(fresh.refresh());
    await f.wait(fresh.decide(entry, "cancel")); await f.wait(old.decide(entry, "allow_once"));
    assert.equal(settled, false); // Observe the noncooperative transport without waiting for it or using a timer.
    assert.equal(lists, 1); assert.equal(resolves, 1);
    assert.deepEqual(states.at(-1), { kind: "resolving" });
    // "Unsettled" is an assertion above, not abandoned work. Fixture.run also joins on assertion failure.
    pending.resolve(resolution()); controller.abort(); await f.wait(work);
    const remounted = reviewer.forSelection(request, f.controller().signal, value => states.push(value));
    await f.wait(remounted.refresh()); // Neither mount nor the earlier pending observations acknowledged success.
    assert.equal(lists, 1);
    assert.equal(remounted.observeDecision()?.state, "resolved");
    await f.wait(remounted.refresh());
    assert.equal(lists, 2);
    await f.wait(remounted.decide(entry, "allow_once"));
    assert.equal(resolves, 1);
  }
}));

test("permission resolution stale host latches reload instead of accepting an old success", () => Fixture.run(async f => {
  const states: PermissionReviewState[] = [];
  const scope = f.reviewer(async () => page(), async req => ({ status: "resolved", hostEpoch: otherEpoch, handle: req.handle }))
    .forSelection(request, f.controller().signal, value => states.push(value));
  await f.wait(scope.refresh()); await f.wait(scope.decide(ready(states).entries[0], "allow_once"));
  assert.deepEqual(states.at(-1), { kind: "error", code: "stale_epoch", reloadRequired: true });
  assert.equal(scope.observeDecision()?.code, "stale_epoch");
}));

test("disabled empty and read failure states never imply execution or completion", () => Fixture.run(async f => {
  for (const status of ["disabled", "empty", "failure"]) {
    const states: PermissionReviewState[] = [];
    const scope = f.reviewer(async () => {
      if (status === "failure") throw f.failure("private");
      return status === "disabled" ? { ...page([]), status: "disabled" } : page([]);
    }, forbidden).forSelection(request, f.controller().signal, value => states.push(value));
    await f.wait(scope.refresh());
    if (status === "empty") assert.equal(ready(states).entries.length, 0);
    else assert.deepEqual(states.at(-1), { kind: "error", code: status === "failure" ? "read_failed" : "disabled", reloadRequired: false });
  }
}));

test("decision capture is frozen before callbacks and same-turn competitors cannot replace it", () => Fixture.run(async f => {
  for (const runId of [null, "provider-run"]) {
    const entry = { ...command(), handle: { ...command().handle, runId } };
    const pending = f.gate({ ...resolution(), handle: entry.handle });
    const selectionRequest: { expectedHostEpoch: string; sessionId: string } = { ...request };
    const states: PermissionReviewState[] = [];
    const reentrant: Promise<void>[] = [];
    let calls = 0;
    let displayed: SessionPermissionCommand | undefined;
    const reviewer = f.reviewer(async () => page([entry]), (actual, options) => {
      calls++;
      assert.deepEqual(actual, origin("allow_once", entry));
      assert.equal(Object.isFrozen(actual), true);
      assert.equal(Object.isFrozen(actual.handle), true);
      assert.equal(options.signal.aborted, false);
      return pending.promise;
    });
    let scope: ReturnType<typeof reviewer.forSelection>;
    scope = reviewer.forSelection(selectionRequest, f.controller().signal, value => {
      states.push(value);
      if (value.kind === "resolving" && displayed) {
        reentrant.push(f.keep(scope.decide(displayed, "deny")));
        reentrant.push(f.keep(scope.refresh()));
        assert.equal(scope.observeDecision()?.state, "pending");
      }
    });
    await f.wait(scope.refresh()); displayed = ready(states).entries[0];
    // A caller-owned request is not authority after selection capture.
    selectionRequest.expectedHostEpoch = otherEpoch; selectionRequest.sessionId = "mutated";
    const work = f.keep(scope.decide(displayed, "allow_once"));
    const competitor = f.keep(scope.decide(displayed, "cancel"));
    await f.wait(competitor);
    for (const task of reentrant) await f.wait(task);
    assert.equal(calls, 1);
    pending.resolve({ ...resolution(), handle: entry.handle }); await f.wait(work);
    const observed = scope.observeDecision();
    assert.deepEqual(observed, { origin: origin("allow_once", entry), state: "resolved", code: null });
    assert.equal(Object.isFrozen(observed), true);
  }
}));

test("local observation attributes the original decision and terminal publication alone does not allow replacement", () => Fixture.run(async f => {
  const states: PermissionReviewState[] = [];
  let lists = 0, resolves = 0;
  const reviewer = f.reviewer(async req => {
    lists++;
    const entry = command();
    return { ...page([{ ...entry, handle: { ...entry.handle, sessionId: req.sessionId } }]), sessionId: req.sessionId };
  }, async req => { resolves++; return { ...resolution(), handle: req.handle }; });
  const scope = reviewer.forSelection(request, f.controller().signal, value => states.push(value));
  assert.equal(scope.observeDecision(), null); assert.equal(lists, 0); assert.equal(resolves, 0);
  await f.wait(scope.refresh()); const old = ready(states).entries[0];
  await f.wait(scope.decide(old, "deny"));
  assert.deepEqual(states.at(-1), { kind: "result", code: "resolved" });
  await f.wait(scope.refresh()); await f.wait(scope.decide(old, "allow_once"));
  assert.equal(lists, 1); assert.equal(resolves, 1); // Live publication is NOT terminal acknowledgment.
  const other = reviewer.forSelection({ ...request, sessionId: "other" }, f.controller().signal, value => states.push(value));
  await f.wait(other.refresh()); assert.equal(lists, 1); // Mount is not observation either.
  assert.deepEqual(other.observeDecision(), { origin: origin("deny"), state: "resolved", code: null });
  assert.deepEqual(other.observeDecision(), { origin: origin("deny"), state: "resolved", code: null });
  assert.equal(lists, 1); assert.equal(resolves, 1); // Observations are synchronous and local only.
  await f.wait(other.decide(old, "allow_once")); assert.equal(resolves, 1);
  await f.wait(other.refresh()); const current = ready(states).entries[0];
  await f.wait(other.decide(old, "cancel")); await f.wait(scope.decide(old, "cancel"));
  assert.equal(resolves, 1);
  await f.wait(other.decide(current, "cancel"));
  assert.equal(resolves, 2);
  assert.deepEqual(other.observeDecision(), { origin: { ...origin("cancel"), handle: current.handle }, state: "resolved", code: null });
}));

test("malformed or nonterminal late decision replies never settle or become valid epoch evidence", () => Fixture.run(async f => {
  const malformed: unknown[] = [null, {}, { ...resolution(), status: "unknown" },
    { ...resolution(), hostEpoch: "not-an-epoch" }, { ...resolution(), hostEpoch: epoch.toUpperCase() },
    { ...resolution(), handle: null }, { ...resolution(), handle: { ...command().handle, attemptId: "bad" } },
    { ...resolution(), handle: { ...command().handle, attachmentGeneration: 1 } },
    { ...resolution(), status: "unknown", hostEpoch: otherEpoch },
    resolution("disabled"), resolution("invalid_request"), resolution("stale_epoch"),
    { ...resolution(), hostEpoch: otherEpoch, handle: { ...command().handle, sessionId: "wrong" } }];
  for (const value of malformed) {
    // Deliberately malformed transport boundary, not a cast used to satisfy production DTO construction.
    const pending = f.gate(resolution());
    const states: PermissionReviewState[] = [];
    let lists = 0, resolves = 0;
    const reviewer = f.reviewer(async () => { lists++; return page(); }, () => { resolves++; return pending.promise; });
    const old = reviewer.forSelection(request, f.controller().signal, state => states.push(state));
    await f.wait(old.refresh()); const entry = ready(states).entries[0];
    const work = f.keep(old.decide(entry, "allow_once"));
    const fresh = reviewer.forSelection(request, f.controller().signal, state => states.push(state));
    const count = states.length;
    pending.resolve(value as SessionPermissionResolution); await f.wait(work);
    assert.equal(states.length, count);
    assert.deepEqual(fresh.observeDecision(), { origin: origin(), state: "error", code: "uncertain" });
    await f.wait(fresh.refresh()); await f.wait(fresh.decide(entry, "deny"));
    assert.equal(fresh.observeDecision()?.code, "uncertain");
    assert.equal(lists, 1); assert.equal(resolves, 1);
  }
}));

test("valid late epoch invalidation survives obsolete presentation and repeated observation", () => Fixture.run(async f => {
  for (const status of ["resolved", "rejected", "stale_epoch"]) {
    const pending = f.gate(resolution()); const controller = f.controller();
    const states: PermissionReviewState[] = [];
    let lists = 0, resolves = 0;
    const reviewer = f.reviewer(async () => { lists++; return page(); }, () => { resolves++; return pending.promise; });
    const old = reviewer.forSelection(request, controller.signal, value => states.push(value));
    await f.wait(old.refresh()); const entry = ready(states).entries[0];
    const work = f.keep(old.decide(entry, "allow_once")); controller.abort();
    const fresh = reviewer.forSelection(request, f.controller().signal, value => states.push(value));
    const count = states.length;
    pending.resolve({ ...resolution(status), hostEpoch: otherEpoch }); await f.wait(work);
    assert.equal(states.length, count);
    assert.equal(fresh.observeDecision()?.code, "stale_epoch");
    assert.equal(fresh.observeDecision()?.code, "stale_epoch");
    await f.wait(fresh.refresh()); await f.wait(fresh.decide(entry, "cancel"));
    assert.equal(lists, 1); assert.equal(resolves, 1);
  }
}));

test("selection epoch replacement never rebases the record or clears invalidation with a late exact success", () => Fixture.run(async f => {
  const pending = f.gate(resolution()); const states: PermissionReviewState[] = [];
  let lists = 0, resolves = 0;
  const reviewer = f.reviewer(async () => { lists++; return page(); }, req => {
    resolves++; assert.deepEqual(req, origin()); return pending.promise;
  });
  const old = reviewer.forSelection(request, f.controller().signal, value => states.push(value));
  await f.wait(old.refresh()); const entry = ready(states).entries[0];
  const work = f.keep(old.decide(entry, "allow_once"));
  const replacement = reviewer.forSelection({ ...request, expectedHostEpoch: otherEpoch }, f.controller().signal, value => states.push(value));
  assert.deepEqual(replacement.observeDecision(), { origin: origin(), state: "error", code: "stale_epoch" });
  pending.resolve(resolution()); await f.wait(work);
  assert.equal(replacement.observeDecision()?.code, "stale_epoch");
  await f.wait(replacement.refresh()); await f.wait(replacement.decide(entry, "allow_once"));
  const restoredEpoch = reviewer.forSelection(request, f.controller().signal, value => states.push(value));
  assert.equal(restoredEpoch.observeDecision()?.code, "stale_epoch");
  await f.wait(restoredEpoch.refresh());
  assert.equal(lists, 1); assert.equal(resolves, 1);
}));

test("a new reviewer after renderer reload only performs explicit pending reads and restores no decision", () => Fixture.run(async f => {
  const states: PermissionReviewState[] = [];
  let lists = 0, resolves = 0;
  const list = async () => { lists++; return page([]); };
  const original = f.reviewer(async () => page(), async () => { resolves++; throw f.failure("lost response"); });
  const old = original.forSelection(request, f.controller().signal, value => states.push(value));
  await f.wait(old.refresh()); await f.wait(old.decide(ready(states).entries[0], "allow_once"));
  assert.equal(old.observeDecision()?.code, "uncertain");
  const reload = f.reviewer(list, forbidden).forSelection(request, f.controller().signal, value => states.push(value));
  assert.equal(reload.observeDecision(), null); assert.equal(lists, 0); assert.equal(resolves, 1);
  await f.wait(reload.refresh()); assert.equal(lists, 1); assert.equal(ready(states).entries.length, 0);
  assert.equal(reload.observeDecision(), null);
  assert.equal(old.observeDecision()?.code, "uncertain"); // Empty pending reads did not reconcile the old record.
  const restarted = f.reviewer(async () => ({ ...page([]), hostEpoch: otherEpoch }), forbidden)
    .forSelection({ ...request, expectedHostEpoch: otherEpoch }, f.controller().signal, value => states.push(value));
  assert.equal(restarted.observeDecision(), null);
  await f.wait(restarted.decide(command(), "allow_once")); assert.equal(resolves, 1);
}));

test("a shown entry is checked without changing what is shown, and the check says nothing it cannot know", () => Fixture.run(async f => {
  const reads: SessionPermissionsPage[] = [page(), page(), page([]), { ...page(), status: "disabled" }];
  let calls = 0; const states: PermissionReviewState[] = [];
  const reviewer = f.reviewer(async () => reads[calls++]);
  const controller = f.controller();
  const selection = reviewer.forSelection(request, controller.signal, value => states.push(value));
  await f.wait(selection.refresh());
  const shown = ready(states).entries[0];
  const published = states.length;

  assert.equal(await f.wait(selection.waits(shown)), true, "It still waits");
  assert.equal(await f.wait(selection.waits(shown)), false, "Answered elsewhere: it is gone");
  assert.equal(await f.wait(selection.waits(shown)), null, "A refused read says nothing");
  assert.equal(states.length, published, "A check publishes nothing");
  assert.equal(ready(states).entries[0], shown, "and keeps the entry that can be decided");
  assert.equal(await f.wait(selection.waits(command())), null, "An entry that is not shown is not checked");
  assert.equal(calls, 4);

  controller.abort();
  assert.equal(await f.wait(selection.waits(shown)), null, "Another selection says nothing");
  assert.equal(calls, 4, "and reads nothing");
}));
