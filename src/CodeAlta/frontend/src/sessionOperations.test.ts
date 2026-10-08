import assert from "node:assert/strict";
import test from "node:test";
import { captureSubmission, captureSubmissionAbort, createMutationCapability, createOwnedSubmissions, noOutgoing, outgoingKey, refreshSubmissions } from "./sessionOperations";
import type { SessionAdmission, SessionAbortRequest, SessionReceiptPage, SessionReceiptView, SessionSendRequest } from "#neoastra";

const operation = "abcdefab-1234-5678-9abc-abcdefabcdef";

test("capability commits denial before notifying, isolates subscriber faults and never re-enables", () => {
  const capability = createMutationCapability("epoch");
  const fault = new Error("subscriber failure");
  const seen: boolean[] = [];
  const removeFault = capability.subscribe(() => { seen.push(capability.canMutate()); throw fault; });
  const removeGood = capability.subscribe(() => seen.push(capability.canSubmit({ expectedEpoch: "epoch" })));
  const removed = capability.subscribe(() => assert.fail("Unsubscribed listener called"));
  removed();
  assert.equal(capability.observe({ status: "ok", epoch: "epoch" }), true);
  assert.deepEqual(seen, []);
  assert.equal(capability.observe({ status: "stale_epoch", epoch: "other" }), false);
  assert.deepEqual(seen, [false, false]);
  assert.equal(capability.notificationFailure(), fault);
  assert.equal(capability.observe({ status: "ok", epoch: "epoch" }), false);
  assert.equal(capability.observe({ status: "stale_runtime", epoch: null }), false);
  assert.deepEqual(seen, [false, false]);
  removeFault(); removeGood();
});
test("an outgoing echo carries the images of its prompt, and only the newest eight keep them", async () => {
  const capability = createMutationCapability("epoch");
  const store = createOwnedSubmissions(async () => { throw new Error("lost"); }, async () => { throw new Error("unused"); });
  const send = (sessionId: string): SessionSendRequest => ({ expectedEpoch: "epoch", clientRequestId: "key-" + sessionId, sessionId, text: "",
    selection: { providerKey: "provider", modelId: "model", reasoningEffort: null, agentPromptId: "prompt" }, references: null,
    images: [{ title: "Shot", mediaType: "image/png", base64: "AAAA" }] });
  for (let index = 0; index < 9; index++) await store.submit(send("session-" + index), new AbortController().signal, capability, () => {});
  const [newest] = store.outgoing("epoch", "session-8");
  assert.deepEqual(newest.images, [{ title: "Shot", mediaType: "image/png", url: "data:image/png;base64,AAAA" }]);
  assert.equal(newest.state, "uncertain");
  assert.equal(store.outgoing("epoch", "session-1")[0].images?.length, 1);
  // The oldest echo is still shown, with the count of its images only.
  assert.equal(store.outgoing("epoch", "session-0")[0].images, undefined);
  assert.equal(store.outgoing("epoch", "session-0")[0].imageCount, 1);
});
test("a prompt leaving the queue shows its echo only once the host took it", async () => {
  const capability = createMutationCapability("epoch");
  let answer: SessionAdmission = { status: "busy", epoch: "epoch", receipt: null };
  const store = createOwnedSubmissions(async () => answer, async () => { throw new Error("unused"); });
  const send = (key: string): SessionSendRequest => ({ expectedEpoch: "epoch", clientRequestId: key, sessionId: "session", text: "queued",
    selection: null, references: null, images: null });
  const results: string[] = [];
  // The session was busy after all: nothing is shown, and nothing is retained that would block the next try.
  await store.submit(send("first"), new AbortController().signal, capability, result => results.push(result.status), "whenAccepted");
  assert.deepEqual([results, store.outgoing("epoch", "session").length, store.pending("session")], [["busy"], 0, undefined]);
  answer = { status: "accepted", epoch: "epoch", receipt: { clientRequestId: "second", sessionId: "session", operationId: operation,
    targetOperationId: null, kind: "Send", state: "pending", outcome: null, code: null, runId: null, queueInsertion: null } };
  await store.submit(send("second"), new AbortController().signal, capability, result => results.push(result.status), "whenAccepted");
  assert.deepEqual(store.outgoing("epoch", "session").map(echo => [echo.text, echo.state]), [["queued", "accepted"]]);
  assert.equal(store.outgoing("epoch", "session")[0].key, outgoingKey(send("second")));
  // A Send from the composer keeps its echo when it is refused: that prompt stays the user's to see.
  answer = { status: "invalid_request", epoch: "epoch", receipt: null };
  await store.submit(send("third"), new AbortController().signal, capability, () => {});
  assert.deepEqual(store.outgoing("epoch", "session").map(echo => echo.state), ["accepted", "failed"]);
  store.acknowledgeOutgoing([outgoingKey(send("third"))]);
  assert.equal(store.outgoing("epoch", "session").length, 1);
});
const control = "abcdefab-1234-5678-9abc-abcdefabcdee";
function row(): SessionReceiptView {
  return { clientRequestId: "key", sessionId: "session", operationId: operation, targetOperationId: null, kind: "Send",
    state: "pending", outcome: null, code: null, runId: null, queueInsertion: null };
}
function page(...rows: SessionReceiptView[]): SessionReceiptPage { return { status: "ok", epoch: "epoch", rows, next: null }; }
function request() { return captureSubmission("epoch", "session", "  exact\ntext  ", "key")!; }
function abortIntent() { const value = page(row()); return captureSubmissionAbort("epoch", "session", value, value.rows[0], "abort")!; }
function abortRow(): SessionReceiptView { return { ...row(), kind: "Abort", clientRequestId: "abort", operationId: control, targetOperationId: operation }; }
function accepted(receipt = row()): SessionAdmission { return { status: "accepted", epoch: "epoch", receipt }; }

// No filesystem, bridge, providers or timers used as scheduling gates. A deadline only fails observation.
// The failure cause retains the entire fixture, including uncertain originals and their observers.
class Fixture {
  readonly failures: unknown[] = [];
  readonly work = new Map<Promise<unknown>, Promise<void>>();
  readonly controllers: AbortController[] = [];
  readonly releases: (() => void)[] = [];
  closing = false;
  keep<T>(task: Promise<T>, expectedFailure?: unknown): Promise<T> {
    if (!this.work.has(task)) this.work.set(task, task.then(() => {}, error => {
      if (expectedFailure === undefined || error !== expectedFailure) this.failures.push(error);
    }));
    return task;
  }
  selection() {
    const controller = new AbortController(); this.controllers.push(controller);
    if (this.closing) controller.abort();
    return controller;
  }
  gate<T>(cleanupValue: T) {
    let resolve!: (value: T) => void;
    const original = this.keep(new Promise<T>(done => { resolve = done; }));
    const release = () => resolve(cleanupValue);
    this.releases.push(release); if (this.closing) release();
    return { original, resolve };
  }
  lost<T>(): Promise<T> {
    const error = new Error("scripted transport loss");
    return this.keep(Promise.reject<T>(error), error);
  }
  async wait<T>(original: Promise<T>): Promise<T> {
    this.keep(original);
    let timer: ReturnType<typeof setTimeout> | undefined;
    try {
      return await Promise.race([original, new Promise<never>((_, reject) => {
        timer = setTimeout(() => {
          const error = new Error("Permanent fixture timeout; termination unconfirmed", { cause: this });
          this.failures.push(error); reject(error);
        }, 5_000);
      })]);
    } finally { clearTimeout(timer); }
  }
  static async run(body: (f: Fixture) => Promise<void>) {
    const f = new Fixture();
    const original = f.keep(body(f));
    try { await f.wait(original); } catch (error) { f.failures.push(error); }
    finally {
      f.closing = true;
      // Initiate independent selection cancellation and release gates before joining dependent work.
      for (const controller of f.controllers) controller.abort();
      for (const release of f.releases) release();
      try {
        // Join the original body before taking the final snapshot; late registrations self-release.
        await f.wait(f.work.get(original)!);
        await f.wait(Promise.all([...f.work.values()]).then(() => {}));
      } catch (error) { f.failures.push(error); }
    }
    if (f.failures.length) throw new AggregateError(f.failures, "Fixture failed; originals retained", { cause: f });
  }
}

test("uncertain retry preserves epoch key session exact text and immutable bounds", () => Fixture.run(async f => {
  const capability = createMutationCapability("epoch"); const signal = f.selection().signal;
  const seen: SessionSendRequest[] = [];
  const store = createOwnedSubmissions(value => { seen.push(value); return f.lost(); }, () => f.lost());
  const captured = request();
  assert.ok(Object.isFrozen(captured));
  assert.deepEqual(captured, { expectedEpoch: "epoch", sessionId: "session", text: "  exact\ntext  ", clientRequestId: "key", selection: null, references: null, images: null });
  assert.ok(captureSubmission("e".repeat(64), "s".repeat(256), "😀".repeat(16384), "k".repeat(256)));
  for (const text of ["", " \n", "\u0085", "\ud800", "\udc00", "x".repeat(32769)])
    assert.equal(captureSubmission("epoch", "session", text, "key"), null);
  for (const id of ["", " padded", "padded ", "\ud800", "x".repeat(257)]) {
    assert.equal(captureSubmission("epoch", id, "text", "key"), null);
    assert.equal(captureSubmission("epoch", "session", "text", id), null);
  }
  assert.equal(captureSubmission("e".repeat(65), "session", "text", "key"), null);
  await f.wait(store.submit(captured, signal, capability, () => {}));
  const retained = store.pending("session")!.request;
  await f.wait(store.submit({ ...retained, text: "changed", clientRequestId: "new" }, signal, capability, () => {}));
  await f.wait(store.submit(captureSubmission("epoch", "session", retained.text, "key")!, signal, capability, () => {}));
  assert.equal(seen.length, 1);
  await f.wait(store.submit(retained, signal, capability, () => {}));
  assert.equal(seen.length, 2); assert.equal(seen[0], seen[1]); assert.ok(Object.isFrozen(retained));
  const mutable = { ...request() };
  const copyStore = createOwnedSubmissions(value => { seen.push(value); return f.lost(); }, () => f.lost());
  const work = f.keep(copyStore.submit(mutable, signal, capability, () => {}));
  mutable.text = "later mutation"; mutable.clientRequestId = "later key";
  await f.wait(work); assert.equal(seen[2].text, captured.text); assert.equal(seen[2].clientRequestId, captured.clientRequestId);
}));

test("stale selection and unmount suppress callbacks but valid late invalidation is permanent", () => Fixture.run(async f => {
  for (const status of ["stale_epoch", "stale_runtime", "accepted"]) {
    const capability = createMutationCapability("epoch"); const selection = f.selection();
    const admission = { status, epoch: status === "stale_runtime" ? null : "new", receipt: status === "accepted" ? row() : null };
    const gate = f.gate<SessionAdmission>(admission);
    const published: unknown[] = [];
    const store = createOwnedSubmissions(() => gate.original, () => f.lost());
    const work = f.keep(store.submit(request(), selection.signal, capability, value => published.push(value)));
    selection.abort(); gate.resolve(admission);
    await f.wait(work); await f.wait(gate.original);
    assert.deepEqual(published, []); assert.equal(capability.canMutate(), false);
    capability.observe({ status: "ok", epoch: "epoch" }); assert.equal(capability.canMutate(), false);
    assert.ok(store.pending("session"));
  }
  const capability = createMutationCapability("epoch"); const selection = f.selection();
  const gate = f.gate<SessionAdmission>(accepted(abortRow())); const published: unknown[] = [];
  const store = createOwnedSubmissions(() => f.lost(), () => gate.original);
  const work = f.keep(store.abort(abortIntent(), selection.signal, capability, value => published.push(value)));
  selection.abort(); gate.resolve({ status: "stale_epoch", epoch: "new", receipt: null });
  await f.wait(work); assert.equal(capability.canMutate(), false); assert.deepEqual(published, []);
  assert.ok(store.abortPending(operation));
  // Also retain and observe a rejected original transport after cancellation, not only successful gates.
  const late = f.selection(); const failureStore = createOwnedSubmissions(() => f.lost(), () => f.lost());
  const failurePublished: SessionAdmission[] = [];
  const failure = f.keep(failureStore.submit(request(), late.signal, createMutationCapability("epoch"), value => failurePublished.push(value)));
  late.abort(); await f.wait(failure); assert.deepEqual(failurePublished, []); assert.ok(failureStore.pending("session"));
}));

test("epoch mismatch never resends or rebases retained intent", () => Fixture.run(async f => {
  const capability = createMutationCapability("epoch"); const signal = f.selection().signal;
  let calls = 0; const published: unknown[] = [];
  const store = createOwnedSubmissions(() => { calls++; return f.keep(Promise.resolve({ status: "stale_epoch", epoch: "new", receipt: null })); }, () => f.lost());
  await f.wait(store.submit(request(), signal, capability, value => published.push(value)));
  const retained = store.pending("session")!.request;
  await f.wait(store.submit(retained, signal, capability, () => {}));
  capability.observe({ status: "ok", epoch: "epoch" });
  const remount = { store, capability };
  await f.wait(remount.store.submit(retained, signal, remount.capability, () => {}));
  await f.wait(store.submit({ ...retained, expectedEpoch: "new" }, signal, createMutationCapability("new"), () => {}));
  assert.equal(calls, 1); assert.equal(published.length, 1);
  assert.equal(store.pending("session")!.request, retained); assert.equal(retained.expectedEpoch, "epoch");
}));

test("receipt recovery is exact manual observation and exposes no prompt text", () => Fixture.run(async f => {
  const capability = createMutationCapability("epoch"); const signal = f.selection().signal;
  const store = createOwnedSubmissions(() => f.lost(), () => f.lost());
  await f.wait(store.submit(request(), signal, capability, () => {}));
  for (const wrong of [{ ...row(), clientRequestId: "wrong" }, { ...row(), sessionId: "SESSION" },
    { ...row(), kind: "Steer" }, { ...abortRow(), clientRequestId: "key" }, { ...row(), targetOperationId: control }]) {
    assert.deepEqual(store.reconcile("session", page(wrong), capability), { sendRecovered: false, abortsRecovered: 0 });
    assert.ok(store.pending("session"));
  }
  assert.deepEqual(store.reconcile("other", page(row()), capability), { sendRecovered: false, abortsRecovered: 0 });
  assert.deepEqual(store.reconcile("session", { ...page(row()), epoch: "other" }, createMutationCapability("other")),
    { sendRecovered: false, abortsRecovered: 0 });
  const recovered = page({ ...row(), state: "terminal", outcome: "Completed", code: null, runId: "opaque run ID" });
  const published: SessionReceiptPage[] = [];
  await f.wait(refreshSubmissions(() => f.keep(Promise.resolve(recovered)), "epoch", 0, signal, value => published.push(value), capability));
  assert.deepEqual(published, [recovered]); assert.equal(JSON.stringify(published).includes("text"), false);
  assert.deepEqual(store.reconcile("session", recovered, capability), { sendRecovered: true, abortsRecovered: 0 });
  assert.equal(store.pending("session"), undefined);
}));

test("refresh is explicit and never submits and Abort-only recovery preserves unrelated draft", () => Fixture.run(async f => {
  const capability = createMutationCapability("epoch"); const signal = f.selection().signal;
  let sends = 0; let aborts = 0; let reads = 0;
  const store = createOwnedSubmissions(() => { sends++; return f.lost(); }, () => { aborts++; return f.lost(); });
  assert.equal(reads + sends + aborts, 0);
  await f.wait(store.abort(abortIntent(), signal, capability, () => {}));
  let composer = "unrelated new text";
  const consume = (value: SessionReceiptPage) => {
    const recovered = store.reconcile("session", value, capability);
    if (recovered.sendRecovered) composer = ""; // Same production result consumed by the panel.
    return recovered;
  };
  await f.wait(refreshSubmissions(() => { reads++; return f.keep(Promise.resolve(page(abortRow()))); }, "epoch", 0, signal, value => {
    assert.deepEqual(consume(value), { sendRecovered: false, abortsRecovered: 1 });
  }, capability));
  assert.equal(composer, "unrelated new text"); assert.equal(sends, 0); assert.equal(aborts, 1); assert.equal(reads, 1);
  await f.wait(store.submit(request(), signal, capability, () => {}));
  await f.wait(store.abort(abortIntent(), signal, capability, () => {}));
  assert.deepEqual(consume(page(row(), abortRow())), { sendRecovered: true, abortsRecovered: 1 });
  assert.equal(composer, ""); composer = "later draft";
  assert.deepEqual(consume(page(row(), abortRow())), { sendRecovered: false, abortsRecovered: 0 });
  assert.equal(composer, "later draft");
  const late = f.selection(); const gate = f.gate<SessionReceiptPage>({ status: "stale_epoch", epoch: "new", rows: [], next: null });
  const read = f.keep(refreshSubmissions(() => gate.original, "epoch", 0, late.signal, () => assert.fail("obsolete read published"), capability));
  late.abort(); gate.resolve({ status: "stale_epoch", epoch: "new", rows: [], next: null }); await f.wait(read);
  await f.wait(store.submit(request(), signal, capability, () => assert.fail("invalid epoch published")));
  assert.equal(capability.canMutate(), false); assert.equal(sends, 1);
}));

test("same-turn Send duplicates and remount retain the original live waiter latch", () => Fixture.run(async f => {
  const capability = createMutationCapability("epoch"); const old = f.selection(); const next = f.selection();
  const gate = f.gate<SessionAdmission>(accepted()); let calls = 0; const published: unknown[] = [];
  const store = createOwnedSubmissions(() => { calls++; return gate.original; }, () => f.lost());
  const original = f.keep(store.submit(request(), old.signal, capability, value => published.push(value)));
  const retained = store.pending("session")!;
  assert.equal(retained.inFlight, true); assert.ok(Object.isFrozen(retained));
  const duplicate = f.keep(store.submit(retained.request, old.signal, capability, () => assert.fail("duplicate published")));
  const recaptured = f.keep(store.submit({ ...request(), clientRequestId: "second-click" }, old.signal, capability, () => assert.fail("recapture published")));
  assert.equal(calls, 1); // Both second clicks were invoked before any yield, as in a stale render closure.
  await f.wait(duplicate); await f.wait(recaptured);
  old.abort(); const remount = { store, signal: next.signal };
  await f.wait(remount.store.submit(retained.request, remount.signal, capability, () => assert.fail("remount bypassed latch")));
  assert.equal(calls, 1); assert.equal(store.pending("session")!.inFlight, true);
  gate.resolve(accepted()); await f.wait(original); await f.wait(gate.original);
  assert.equal(store.pending("session")!.inFlight, false); assert.deepEqual(published, []);
  assert.deepEqual(store.reconcile("session", page(row()), capability), { sendRecovered: true, abortsRecovered: 0 });
}));

test("matching receipts cannot reconcile original live Send or Abort waiters", () => Fixture.run(async f => {
  const capability = createMutationCapability("epoch"); const selection = f.selection();
  const sendGate = f.gate<SessionAdmission>(accepted()); const abortGate = f.gate<SessionAdmission>(accepted(abortRow()));
  const store = createOwnedSubmissions(() => sendGate.original, () => abortGate.original);
  const send = f.keep(store.submit(request(), selection.signal, capability, () => {}));
  const abort = f.keep(store.abort(abortIntent(), selection.signal, capability, () => {}));
  const snapshot = page(row(), abortRow());
  assert.deepEqual(store.reconcile("session", snapshot, capability), { sendRecovered: false, abortsRecovered: 0 });
  selection.abort();
  assert.deepEqual(store.reconcile("session", snapshot, capability), { sendRecovered: false, abortsRecovered: 0 });
  sendGate.resolve(accepted()); abortGate.resolve(accepted(abortRow())); await f.wait(send); await f.wait(abort);
  assert.deepEqual(store.reconcile("session", snapshot, capability), { sendRecovered: true, abortsRecovered: 1 });
}));

test("Abort retains original Send operation key and session across uncertainty and explicit retry", () => Fixture.run(async f => {
  const capability = createMutationCapability("epoch"); const old = f.selection(); const next = f.selection();
  const gate = f.gate<SessionAdmission>({ status: "admission_failed", epoch: "epoch", receipt: null });
  const seen: SessionAbortRequest[] = [];
  const store = createOwnedSubmissions(() => f.lost(), value => { seen.push(value); return seen.length === 1 ? gate.original : f.lost(); });
  const intent = abortIntent(); assert.ok(Object.isFrozen(intent)); assert.ok(Object.isFrozen(intent.request));
  const original = f.keep(store.abort(intent, old.signal, capability, () => assert.fail("obsolete Abort published")));
  const retained = store.abortPending(operation)!.intent;
  await f.wait(store.abort(retained, old.signal, capability, () => assert.fail("duplicate Abort published")));
  old.abort();
  await f.wait(store.abort(retained, next.signal, capability, () => assert.fail("remount bypassed Abort latch")));
  gate.resolve({ status: "admission_failed", epoch: "epoch", receipt: null }); await f.wait(original);
  await f.wait(store.abort({ ...retained, sessionId: "other" }, next.signal, capability, () => assert.fail("retarget published")));
  await f.wait(store.abort({ ...retained, request: { ...retained.request, clientRequestId: "new-key" } }, next.signal, capability, () => assert.fail("changed key published")));
  await f.wait(store.abort(abortIntent(), next.signal, capability, () => assert.fail("recapture published")));
  await f.wait(store.abort(retained, next.signal, capability, () => {}));
  assert.equal(seen.length, 2); assert.equal(seen[0], seen[1]);
  assert.deepEqual(seen[0], { expectedEpoch: "epoch", clientRequestId: "abort", targetOperationId: operation });
  assert.equal(store.aborts("other").length, 0); assert.equal(store.aborts("session")[0].intent.sessionId, "session");
  assert.ok(Object.isFrozen(store.aborts("session"))); assert.ok(Object.isFrozen(store.abortPending(operation)));
  for (const wrong of [{ ...abortRow(), targetOperationId: control }, { ...abortRow(), sessionId: "other" },
    { ...abortRow(), clientRequestId: "wrong" }, { ...abortRow(), kind: "AbortRun" }])
    assert.deepEqual(store.reconcile("session", page(wrong), capability), { sendRecovered: false, abortsRecovered: 0 });
  assert.deepEqual(store.reconcile("session", page(abortRow()), capability), { sendRecovered: false, abortsRecovered: 1 });
}));

test("malformed admissions and mixed receipt pages fail closed while legacy nullable fields remain valid", () => Fixture.run(async f => {
  const capability = createMutationCapability("epoch"); const signal = f.selection().signal;
  let response: SessionAdmission = accepted(); const published: SessionAdmission[] = [];
  const store = createOwnedSubmissions(() => f.keep(Promise.resolve(response)), () => f.keep(Promise.resolve(response)));
  const invalidRows: SessionReceiptView[] = [
    { ...row(), operationId: operation.toUpperCase() }, { ...row(), operationId: "00000000-0000-0000-0000-000000000000" },
    { ...row(), clientRequestId: "\ud800" }, { ...row(), state: "unknown" }, { ...row(), outcome: "unknown" },
    { ...row(), code: "x".repeat(65) }, { ...row(), runId: "\udc00" },
    { ...row(), queueInsertion: { state: "pending", accepted: null, code: null } },
  ];
  const invalidPages: SessionReceiptPage[] = [
    { ...page(row()), rows: null as unknown as SessionReceiptView[] }, page(...Array.from({ length: 65 }, row)), page(row(), row()),
    page(row(), { ...row(), operationId: control }), { ...page(row()), next: 1 }, { ...page(row()), next: 64 },
    { ...page(row()), rows: Array<SessionReceiptView>(1) },
    ...invalidRows.map(value => page(value)),
  ];
  for (const invalid of [null, { status: "accepted", epoch: "epoch" }, { status: "stale_epoch", epoch: 5, receipt: null },
    { status: "accepted", epoch: "\ud800", receipt: row() }, ...invalidRows.map(receipt => accepted(receipt)),
    accepted({ ...row(), sessionId: "other" }), accepted({ ...row(), kind: "Steer" }), accepted({ ...row(), targetOperationId: control }),
    { status: "replay", epoch: "epoch", receipt: null }, { status: "busy", epoch: "epoch", receipt: row() },
    { status: "admission_failed", epoch: "epoch", receipt: null }, { status: "wire_limit", epoch: "epoch", receipt: null }]) {
    response = invalid as SessionAdmission;
    await f.wait(store.submit(store.pending("session")?.request ?? request(), signal, capability, value => published.push(value)));
    assert.ok(store.pending("session")); assert.equal(published.at(-1)?.status, "uncertain"); assert.equal(capability.canMutate(), true);
  }
  for (const invalid of invalidPages) {
    assert.deepEqual(store.reconcile("session", invalid, capability), { sendRecovered: false, abortsRecovered: 0 });
    assert.equal(captureSubmissionAbort("epoch", "session", invalid, invalid.rows?.[0] ?? row(), "abort"), null);
  }
  const readResults: SessionReceiptPage[] = [];
  await f.wait(refreshSubmissions(() => f.keep(Promise.resolve(invalidPages[0])), "epoch", 0, signal, value => readResults.push(value), capability));
  assert.deepEqual(readResults, [{ status: "invalid_response", epoch: "epoch", rows: [], next: null }]);
  for (const wrong of [{ ...row(), sessionId: "SESSION" }, { ...row(), state: "terminal" }, { ...row(), targetOperationId: control },
    { ...row(), kind: "AbortRun" }, { ...row(), kind: "Queue", queueInsertion: { state: "pending", accepted: null, code: null } }]) {
    const value = page(wrong); assert.equal(captureSubmissionAbort("epoch", "session", value, wrong, "abort"), null);
  }
  const valid = page(row()); assert.equal(captureSubmissionAbort("epoch", "session", valid, { ...valid.rows[0] }, "abort"), null);
  assert.equal(captureSubmissionAbort("old", "session", valid, valid.rows[0], "abort"), null);
  const full: SessionReceiptPage = { ...page(...Array.from({ length: 64 }, (_, index) => index === 0 ? row()
    : { ...row(), kind: "Steer", clientRequestId: `key${index}`, operationId: `00000000-0000-0000-0000-${index.toString(16).padStart(12, "0")}` })), next: 64 };
  assert.ok(captureSubmissionAbort("epoch", "session", full, full.rows[0], "abort"));
  assert.equal(captureSubmissionAbort("epoch", "session", { ...full, next: 320 }, full.rows[0], "abort"), null);
  const mixed = page(row(), { ...abortRow(), kind: "AbortRun" },
    { ...row(), kind: "Queue", operationId: "abcdefab-1234-5678-9abc-abcdefabcded", clientRequestId: "queue",
      queueInsertion: { state: "terminal", accepted: false, code: "queue_target_unavailable" } });
  assert.ok(captureSubmissionAbort("epoch", "session", mixed, mixed.rows[0], "abort"));
  assert.deepEqual(store.reconcile("session", mixed, capability), { sendRecovered: true, abortsRecovered: 0 });
  // Legacy wire validation permits nullable outcome/code/run even in terminal rows. Do not apply Queue phases.
  response = accepted({ ...row(), state: "terminal" });
  await f.wait(store.submit(request(), signal, capability, value => published.push(value)));
  assert.equal(store.pending("session"), undefined); assert.equal(published.at(-1)?.status, "accepted");
  for (const wrong of [null, { ...abortRow(), targetOperationId: control }, { ...abortRow(), sessionId: "other" },
    { ...abortRow(), clientRequestId: "wrong" }, { ...abortRow(), kind: "CancelQueue" }]) {
    response = accepted(wrong as SessionReceiptView);
    await f.wait(store.abort(store.abortPending(operation)?.intent ?? abortIntent(), signal, capability, value => published.push(value)));
    assert.ok(store.abortPending(operation)); assert.equal(published.at(-1)?.status, "uncertain");
  }
  response = { status: "replay", epoch: "epoch", receipt: { ...abortRow(), state: "terminal", outcome: "Completed", code: null, runId: "opaque" } };
  await f.wait(store.abort(store.abortPending(operation)!.intent, signal, capability, () => {}));
  assert.equal(store.abortPending(operation), undefined);
}));

test("combined retention bound precancellation and coherent definite refusals preserve distinct intents", () => Fixture.run(async f => {
  const capability = createMutationCapability("epoch"); const signal = f.selection().signal; const cancelled = f.selection(); cancelled.abort();
  let calls = 0; let response: SessionAdmission = { status: "admission_failed", epoch: "epoch", receipt: null };
  const invoke = () => { calls++; return f.keep(Promise.resolve(response)); };
  const store = createOwnedSubmissions(invoke, invoke);
  await f.wait(store.submit(request(), cancelled.signal, capability, () => assert.fail("pre-cancel published")));
  await f.wait(store.abort(abortIntent(), cancelled.signal, capability, () => assert.fail("pre-cancel published")));
  const read = () => { calls++; return f.keep(Promise.resolve(page())); };
  await f.wait(refreshSubmissions(read, "epoch", 0, cancelled.signal, () => assert.fail("pre-cancel read published"), capability));
  await f.wait(refreshSubmissions(read, "epoch", 1, signal, () => assert.fail("invalid cursor published"), capability));
  await f.wait(store.submit({ ...request(), text: "\ud800" }, signal, capability, () => assert.fail("invalid Send published")));
  await f.wait(store.abort({ ...abortIntent(), request: { ...abortIntent().request, targetOperationId: "bad" } }, signal, capability, () => assert.fail("invalid Abort published")));
  assert.equal(calls, 0);
  for (let index = 0; index < 255; index++)
    await f.wait(store.submit(captureSubmission("epoch", `s${index}`, "text", `key${index}`)!, signal, capability, () => {}));
  await f.wait(store.abort(abortIntent(), signal, capability, () => {}));
  const published: SessionAdmission[] = [];
  await f.wait(store.submit(request(), signal, capability, value => published.push(value)));
  const different = page({ ...row(), operationId: control });
  await f.wait(store.abort(captureSubmissionAbort("epoch", "session", different, different.rows[0], "other-abort")!, signal, capability, value => published.push(value)));
  assert.equal(calls, 256); assert.deepEqual(published.map(value => value.status), ["capacity", "capacity"]);
  const retained = store.pending("s0")!.request;
  await f.wait(store.submit(retained, signal, capability, () => {})); assert.equal(calls, 257);
  // A refusal carrying a receipt is incoherent, not proof of non-admission.
  response = { status: "closed", epoch: "epoch", receipt: row() };
  await f.wait(store.submit(retained, signal, capability, () => {})); assert.ok(store.pending("s0"));
  response = { status: "unknowntarget", epoch: "epoch", receipt: abortRow() };
  await f.wait(store.abort(store.abortPending(operation)!.intent, signal, capability, () => {})); assert.ok(store.abortPending(operation));
  // "expired": the host ran this key earlier and no longer keeps its receipt. It is not uncertainty either.
  for (const status of ["conflict", "expired", "busy", "capacity", "closed", "invalid_request"]) {
    response = { status, epoch: "epoch", receipt: null };
    await f.wait(store.submit(store.pending("s0")?.request ?? retained, signal, capability, () => {}));
    assert.equal(store.pending("s0"), undefined);
    await f.wait(store.abort(store.abortPending(operation)?.intent ?? abortIntent(), signal, capability, () => {}));
    assert.equal(store.abortPending(operation), undefined);
  }
  response = { status: "unknowntarget", epoch: "epoch", receipt: null };
  await f.wait(store.abort(abortIntent(), signal, capability, () => {})); assert.equal(store.abortPending(operation), undefined);
  await f.wait(store.submit(retained, signal, capability, () => {})); assert.ok(store.pending("s0"));
}));
test("the echoes of a session are the same array until one of them changes", async () => {
  const capability = createMutationCapability("epoch");
  const answer: SessionAdmission = { status: "invalid_request", epoch: "epoch", receipt: null };
  const store = createOwnedSubmissions(async () => answer, async () => { throw new Error("unused"); });
  const send = (key: string, sessionId = "session"): SessionSendRequest => ({ expectedEpoch: "epoch", clientRequestId: key, sessionId, text: key,
    selection: null, references: null, images: null });
  // Nothing sent: one shared value, whatever the session, so a memoized timeline has nothing to render again.
  assert.equal(store.outgoing("epoch", "session"), noOutgoing);
  assert.equal(store.outgoing("epoch", "other"), noOutgoing);
  await store.submit(send("first"), new AbortController().signal, capability, () => {});
  const shown = store.outgoing("epoch", "session");
  assert.deepEqual(shown.map(echo => echo.state), ["failed"]);
  assert.equal(store.outgoing("epoch", "SESSION"), shown);
  // The echo of another session leaves this array alone.
  await store.submit(send("second", "other"), new AbortController().signal, capability, () => {});
  assert.equal(store.outgoing("epoch", "session"), shown);
  await store.submit(send("third"), new AbortController().signal, capability, () => {});
  const next = store.outgoing("epoch", "session");
  assert.notEqual(next, shown);
  assert.equal(next.length, 2);
  store.acknowledgeOutgoing(next.map(echo => echo.key));
  assert.equal(store.outgoing("epoch", "session"), noOutgoing);
});
