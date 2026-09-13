import assert from "node:assert/strict";
import test from "node:test";
import { setTimeout as deadlineTimer, clearTimeout as clearDeadlineTimer } from "node:timers";
import { askWireHandle, askWireRequest, captureAskAction, createAskActions, parseAskPage, validAskHandle } from "./sessionAsks";
import { createMutationCapability } from "./sessionOperations";

const epoch = "11111111-1111-4111-8111-111111111111";
const handle = Object.freeze({ operationId: epoch, runtimeInstanceId: epoch, attachmentGeneration: "1",
  providerId: "fixture", sessionId: "session", runId: "run", askId: epoch, responseGeneration: "0" });

test("action captures nested original values and rejects invalid generation", () => {
  const answers = [{ questionIndex: 0, selectedChoiceIndexes: [] as number[], freeformText: "answer" }];
  const action = captureAskAction(epoch, handle, answers, epoch);
  answers[0].freeformText = "changed";
  assert.equal(action.action.answers[0].freeformText, "answer");
  assert.ok(Object.isFrozen(action.action.handle));
  assert.throws(() => captureAskAction(epoch, { ...handle, responseGeneration: "-1" }, [], epoch));
});

test("original waiter survives view change; uncertain transport and observation stay separate", async () => {
  let release!: (value: unknown) => void;
  const original = new Promise<unknown>(resolve => { release = resolve; });
  let entered!: () => void;
  const ready = new Promise<void>(resolve => { entered = resolve; });
  const invoke = () => { entered(); return original; };
  const owner = createAskActions(invoke, invoke);
  const action = captureAskAction(epoch, handle, [], epoch);
  const work = owner.submit("cancel", action, () => true);
  try {
    await join(ready, owner);
    owner.invalidate(epoch);
    assert.equal(owner.get(epoch)?.transport, "uncertain");
    release({ status: "ok", hostEpoch: epoch, disposition: { actionId: epoch, handle, status: "cancelled", runId: null } });
    await join(work, owner);
    assert.equal(owner.get(epoch)?.transport, "uncertain");
    owner.observe(epoch, { actionId: epoch, handle, status: "cancelled", runId: null });
    assert.equal(owner.get(epoch)?.transport, "uncertain");
    assert.equal(owner.get(epoch)?.observed?.status, "cancelled");
  } finally { release({}); await join(work, owner); }
});

test("eight-second deadline permanently latches uncertainty despite late success and explicit observation", async context => {
  context.mock.timers.enable({ apis: ["setTimeout"] });
  let release!: (value: unknown) => void;
  let entered!: () => void;
  const ready = new Promise<void>(resolve => { entered = resolve; });
  const original = new Promise<unknown>(resolve => { release = resolve; });
  let calls = 0;
  const invoke = () => { calls++; entered(); return original; };
  const owner = createAskActions(invoke, invoke);
  const action = captureAskAction(epoch, handle, [], epoch);
  const work = owner.submit("cancel", action, () => true);
  let observation: Promise<void> | undefined;
  const reply = { status: "ok", hostEpoch: epoch, disposition: { actionId: epoch, handle, status: "cancelled", runId: null } };
  try {
    await join(ready, owner);
    context.mock.timers.tick(8000);
    assert.equal(owner.get(epoch)?.transport, "uncertain");
    release(reply);
    await join(work, owner);
    assert.equal(owner.get(epoch)?.transport, "uncertain");
    assert.equal(owner.get(epoch)?.result, null);
    assert.equal(owner.submit("cancel", action, () => true), work);
    assert.equal(calls, 1);
    observation = owner.observeRemote(epoch, async () => reply);
    await join(observation, owner);
    assert.equal(owner.get(epoch)?.observed?.status, "cancelled");
    assert.equal(owner.get(epoch)?.transport, "uncertain");
    assert.equal(owner.blocked(handle), true);
  } finally {
    release({});
    try { await join(Promise.all([work, ...(observation ? [observation] : [])]), owner); }
    finally { context.mock.timers.reset(); }
  }
});

test("reload recovers only same-epoch backend facts, not missing-action acknowledgment", () => {
  const wire = { status: "ok", hostEpoch: epoch, sessionId: "session", head: null, latest: null, hasMore: true };
  assert.equal(parseAskPage(wire, epoch, "session").hasMore, true);
  assert.throws(() => parseAskPage(wire, "22222222-2222-4222-8222-222222222222", "session"));
  assert.throws(() => parseAskPage(wire, epoch, "other"));
  const owner = createAskActions(async () => { throw Error("No dispatch"); }, async () => { throw Error("No dispatch"); });
  assert.equal(owner.get(epoch), undefined);
  assert.throws(() => captureAskAction(epoch, handle, [{ questionIndex: 0, freeformText: "\ud800" }], epoch));
  assert.throws(() => captureAskAction(epoch, handle, [{ questionIndex: 0, freeformText: "x".repeat(8193) }], epoch));
});

test("explicit observation can read later evidence without retrying the original action", async () => {
  const reply = (status: string) => ({ status: "ok", hostEpoch: epoch, disposition: { actionId: epoch, handle, status, runId: null } });
  let calls = 0;
  const owner = createAskActions(async () => { calls++; return reply("cancelled"); }, async () => { calls++; return reply("cancelled"); });
  const work = owner.submit("cancel", captureAskAction(epoch, handle, [], epoch), () => true);
  const observations: Promise<void>[] = [];
  try {
    await join(work, owner);
    observations.push(owner.observeRemote(epoch, async () => reply("submitting")));
    await join(observations[0], owner);
    assert.equal(owner.get(epoch)?.observed?.status, "submitting");
    observations.push(owner.observeRemote(epoch, async () => reply("cancelled")));
    await join(observations[1], owner);
    assert.equal(owner.get(epoch)?.observed?.status, "cancelled");
    assert.equal(calls, 1);
  } finally { await join(Promise.all([work, ...observations]), owner); }
});

test("same-turn competing actions are excluded by the owner before either transport starts", async () => {
  for (const competing of ["answer", "cancel"] as const) {
    let release!: (value: unknown) => void;
    let entered!: () => void;
    const original = new Promise<unknown>(resolve => { release = resolve; });
    const ready = new Promise<void>(resolve => { entered = resolve; });
    let answers = 0; let cancels = 0;
    const owner = createAskActions(() => { answers++; entered(); return original; }, () => { cancels++; return original; });
    const first = captureAskAction(epoch, handle, [], epoch);
    const otherId = "22222222-2222-4222-8222-222222222222";
    const work = owner.submit("answer", first, () => true);
    const competingWork = owner.submit(competing, captureAskAction(epoch, handle, [], otherId), () => true);
    let primary: unknown;
    try {
      assert.equal(owner.submit("answer", first, () => true), work);
      assert.equal(owner.get(otherId), undefined);
      await join(ready, owner);
      assert.equal(answers, 1);
      assert.equal(cancels, 0);
      release({ status: "ok", hostEpoch: epoch, disposition: { actionId: epoch, handle, status: "not_admitted", runId: null } });
      await join(work, owner);
      const fresh = { ...handle, responseGeneration: "1" };
      assert.equal(owner.blocked(fresh), true);
      owner.observe(epoch, { actionId: epoch, handle, status: "not_admitted", runId: null });
      assert.equal(owner.blocked(handle), true);
      assert.equal(owner.blocked(fresh), false);
    } catch (error) { primary = error; throw error; }
    finally { release({}); entered(); await finish([original, ready, work, competingWork], owner, primary); }
  }
});

test("late stale observation revokes the shared capability and all pending originals", async () => {
  for (const stale of [true, false]) {
    const capability = createMutationCapability(epoch);
    const revoke = () => { capability.observe({ status: "stale_epoch", epoch }); };
    let release!: (value: unknown) => void; let releaseRead!: (value: unknown) => void; let entered!: () => void;
    const original = new Promise<unknown>(resolve => { release = resolve; });
    const read = new Promise<unknown>(resolve => { releaseRead = resolve; });
    const ready = new Promise<void>(resolve => { entered = resolve; });
    const owner = createAskActions(() => original, () => original);
    const secondId = "22222222-2222-4222-8222-222222222222";
    const secondHandle = { ...handle, askId: secondId, sessionId: "other" };
    const a = owner.submit("cancel", captureAskAction(epoch, handle, [], epoch), () => capability.canMutate(), revoke);
    const b = owner.submit("cancel", captureAskAction(epoch, secondHandle, [], secondId), () => capability.canMutate(), revoke);
    const observation = owner.observeRemote(epoch, () => { entered(); return read; }, revoke);
    let primary: unknown;
    try {
      await join(ready, owner);
      assert.equal(capability.canMutate(), true);
      assert.equal(owner.get(epoch)?.transport, "pending");
      assert.equal(owner.get(secondId)?.transport, "pending");
      releaseRead({ status: stale ? "stale_epoch" : "ok", hostEpoch: stale ? epoch : secondId, disposition: null });
      await join(observation, owner);
      assert.equal(capability.canMutate(), false);
      assert.equal(owner.get(epoch)?.transport, "uncertain");
      assert.equal(owner.get(secondId)?.transport, "uncertain");
      assert.equal(owner.get(epoch)?.observed, null);
      release({ status: "ok", hostEpoch: epoch, disposition: { actionId: epoch, handle, status: "cancelled", runId: null } });
      await join(Promise.all([a, b]), owner);
      assert.equal(owner.get(epoch)?.transport, "uncertain");
      assert.equal(owner.get(epoch)?.result, null);
      owner.observe(epoch, { actionId: epoch, handle, status: "cancelled", runId: null });
      assert.equal(owner.get(epoch)?.observed?.status, "cancelled");
      assert.equal(owner.get(epoch)?.transport, "uncertain");
    } catch (error) { primary = error; throw error; }
    finally { release({}); releaseRead({}); entered(); await finish([original, read, ready, a, b, observation], owner, primary); }
  }
});

test("fresh generation dispatch requires explicit observation while exact-ID reuse stays original", async () => {
  let calls = 0;
  const owner = createAskActions(async request => {
    calls++;
    return { status: "ok", hostEpoch: epoch, disposition: { actionId: request.action.actionId,
      handle: request.action.handle, status: "not_admitted", runId: null } };
  }, async () => { throw new Error("No cancel transport expected"); });
  const first = captureAskAction(epoch, handle, [], epoch);
  const secondId = "22222222-2222-4222-8222-222222222222";
  const fresh = captureAskAction(epoch, { ...handle, responseGeneration: "1" }, [], secondId);
  const original = owner.submit("answer", first, () => true);
  const work: Promise<unknown>[] = [original];
  let primary: unknown;
  try {
    await join(original, owner);
    work.push(owner.submit("answer", fresh, () => true));
    assert.equal(owner.get(secondId), undefined);
    assert.equal(calls, 1);
    const observation = owner.observeRemote(epoch, async () => ({ status: "ok", hostEpoch: epoch,
      disposition: { actionId: epoch, handle, status: "not_admitted", runId: null } }));
    work.push(observation);
    await join(observation, owner);
    const next = owner.submit("answer", fresh, () => true);
    work.push(next);
    await join(next, owner);
    assert.equal(calls, 2);
    assert.equal(owner.get(secondId)?.request.action.handle.responseGeneration, "1");
    assert.equal(owner.submit("cancel", first, () => true), original);
  } catch (error) { primary = error; throw error; }
  finally { await finish(work, owner, primary); }
});

test("cancelled list presentation still processes late epoch evidence through the panel helper", async () => {
  const capability = createMutationCapability(epoch);
  const revoke = () => { capability.observe({ status: "stale_epoch", epoch }); };
  const controller = new AbortController();
  let release!: (value: unknown) => void;
  let releaseAction!: (value: unknown) => void;
  const original = new Promise<unknown>(resolve => { release = resolve; });
  const action = new Promise<unknown>(resolve => { releaseAction = resolve; });
  const owner = createAskActions(() => action, () => action);
  const work = owner.submit("cancel", captureAskAction(epoch, handle, [], epoch), () => capability.canMutate(), revoke);
  let presentation = 0;
  const observer = original.then(value => {
    const page = owner.readPage(value, epoch, "session", () => !controller.signal.aborted, revoke);
    if (page) presentation++;
  });
  let primary: unknown;
  try {
    controller.abort();
    release({ status: "stale_epoch", hostEpoch: "22222222-2222-4222-8222-222222222222", sessionId: "session", head: null, latest: null, hasMore: false });
    await join(observer, owner);
    assert.equal(presentation, 0);
    assert.equal(capability.canMutate(), false);
    assert.equal(owner.get(epoch)?.transport, "uncertain");
    assert.equal(owner.blocked(handle), true);
  } catch (error) { primary = error; throw error; }
  finally { controller.abort(); release({}); releaseAction({}); await finish([original, action, observer, work], owner, primary); }
});

test("ask GUIDs reject final newlines in epoch action and every handle GUID", () => {
  assert.throws(() => captureAskAction(epoch + "\n", handle, [], epoch));
  assert.throws(() => captureAskAction(epoch, handle, [], epoch + "\n"));
  const page = { status: "ok", hostEpoch: epoch, sessionId: "session", head: null, latest: null, hasMore: false };
  assert.throws(() => parseAskPage({ ...page, hostEpoch: epoch + "\n" }, epoch + "\n", "session"));
  assert.throws(() => parseAskPage({ ...page, latest: { actionId: epoch + "\n", handle, status: "cancelled", runId: null } }, epoch, "session"));
  for (const name of ["operationId", "runtimeInstanceId", "askId"] as const) {
    const invalid = { ...handle, [name]: epoch + "\n" };
    assert.equal(validAskHandle(invalid), false);
    assert.throws(() => captureAskAction(epoch, invalid, [], epoch));
  }
});

test("ask identity and required text use bounded dotnet whitespace parity", () => {
  const page = (question: string, title = question) => ({ status: "ok", hostEpoch: epoch, sessionId: "session",
    head: { handle, state: "pending", request: { questions: [{ title, question, description: null, choices: [], freeform: { title: null, placeholder: null } }] } }, latest: null, hasMore: false });
  assert.equal(validAskHandle({ ...handle, providerId: "\ufeff", sessionId: "\ufeffsession\ufeff", runId: "\ufeff" }), true);
  assert.equal(parseAskPage(page("\ufeff"), epoch, "session").head?.request.questions[0].question, "\ufeff");
  assert.equal(parseAskPage(page("\u0085\ufeff\u0085"), epoch, "session").head?.request.questions.length, 1);
  for (const whitespace of ["\t", "\n", "\v", "\f", "\r", " ", "\u0085", "\u00a0", "\u1680", "\u2000", "\u2001", "\u2002", "\u2003", "\u2004", "\u2005", "\u2006", "\u2007", "\u2008", "\u2009", "\u200a", "\u2028", "\u2029", "\u202f", "\u205f", "\u3000"]) {
    assert.equal(validAskHandle({ ...handle, providerId: whitespace + "fixture" }), false);
    assert.equal(validAskHandle({ ...handle, runId: "run" + whitespace }), false);
    assert.throws(() => parseAskPage(page(whitespace), epoch, "session"));
    assert.throws(() => parseAskPage(page("question", whitespace), epoch, "session"));
  }
});

test("retained and wire handles project scalars without retaining unknown nested data", () => {
  const unknown = { nested: { text: "original" } };
  const extended = { ...handle, unknown };
  const action = captureAskAction(epoch, extended, [], epoch);
  const page = parseAskPage({ status: "ok", hostEpoch: epoch, sessionId: "session", hasMore: false,
    head: { handle: extended, state: "pending", request: { questions: [{ title: "title", question: "question", description: null, choices: [], freeform: { title: null, placeholder: null } }] } },
    latest: { actionId: epoch, handle: extended, status: "cancelled", runId: null } }, epoch, "session");
  const wire = askWireRequest({ ...action, action: { ...action.action, handle: extended } });
  const observationWire = askWireHandle(extended);
  unknown.nested.text = "changed";
  for (const value of [action.action.handle, page.head!.handle, page.latest!.handle, wire.action.handle, observationWire]) {
    assert.deepEqual(value, handle);
    assert.equal("unknown" in value, false);
  }
  assert.ok(Object.isFrozen(page.head!.handle));
  assert.ok(Object.isFrozen(page.latest!.handle));
  assert.ok(Object.isFrozen(action.action.handle));
});

test("generation wire strings preserve canonical boundaries without numeric coercion", () => {
  for (const attachmentGeneration of ["1", "9007199254740991"]) {
    for (const responseGeneration of ["0", "256"]) {
      const boundary = { ...handle, attachmentGeneration, responseGeneration };
      assert.equal(validAskHandle(boundary), true);
      const captured = captureAskAction(epoch, boundary, [], epoch);
      assert.deepEqual(askWireRequest(captured).action.handle, boundary);
      assert.equal(captured.action.handle.attachmentGeneration, attachmentGeneration);
      assert.equal(captured.action.handle.responseGeneration, responseGeneration);
    }
  }
  const invalid: readonly unknown[] = [null, undefined, 0, 1, 256, 9007199254740991, true, [], {},
    "", " ", " 1", "1 ", "+1", "-1", "1e0", "1.0", "01", "00", "1\n", "1\r\n", "1\0", "\u00851", "1\ufeff", "\u0661",
    "9007199254740992", "9223372036854775808", "9999999999999999999999999999999999999999"];
  for (const value of invalid) {
    assert.equal(validAskHandle({ ...handle, attachmentGeneration: value }), false);
    assert.equal(validAskHandle({ ...handle, responseGeneration: value }), false);
  }
  assert.equal(validAskHandle({ ...handle, attachmentGeneration: "0" }), false);
  assert.equal(validAskHandle({ ...handle, responseGeneration: "257" }), false);
  assert.equal(validAskHandle({ ...handle, responseGeneration: "9007199254740991" }), false);
  const missingAttachment = { operationId: epoch, runtimeInstanceId: epoch, providerId: "fixture", sessionId: "session", runId: "run", askId: epoch, responseGeneration: "0" };
  const missingResponse = { operationId: epoch, runtimeInstanceId: epoch, providerId: "fixture", sessionId: "session", runId: "run", askId: epoch, attachmentGeneration: "1" };
  assert.equal(validAskHandle(missingAttachment), false);
  assert.equal(validAskHandle(missingResponse), false);
});

// Start all original joins before observing the cleanup deadline. A failed join retains the owner
// and original promise graph; preserve the primary assertion/error if cleanup also fails.
async function finish(originals: readonly Promise<unknown>[], owner: unknown, primary: unknown): Promise<void> {
  try {
    const settled = await join(Promise.allSettled(originals), owner);
    const failures = settled.filter((value): value is PromiseRejectedResult => value.status === "rejected").map(value => value.reason);
    if (failures.length) throw new AggregateError(failures, "Original ask fixture work failed");
  } catch (error) { throw new AggregateError(primary === undefined ? [error] : [primary, error], "Ask fixture cleanup failed"); }
}

// These named timer references are captured before the test-local global clock mock. This is an
// independent failure deadline, never a sleep, cancellation, or proof an original has terminated.
async function join<T>(original: Promise<T>, owner: unknown): Promise<T> {
  let timer: ReturnType<typeof deadlineTimer> | undefined;
  const deadline = new Promise<never>((_, reject) => {
    timer = deadlineTimer(() => reject(Object.assign(new Error("Ask fixture deadline; original and owner retained"), { original, owner })), 5000);
  });
  const observation = Promise.race([original, deadline]);
  try { return await observation; }
  finally { if (timer !== undefined) clearDeadlineTimer(timer); }
}
