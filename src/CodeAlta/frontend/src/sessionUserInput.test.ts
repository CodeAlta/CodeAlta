import assert from "node:assert/strict";
import test from "node:test";
import { createUserInputReviewer, inputHandle, inputPage } from "./sessionUserInput";

const epoch = "11111111-1111-4111-8111-111111111111";
const handle = { operationId: epoch, runtimeInstanceId: epoch, attachmentGeneration: "1", sessionId: "session", runId: null, interactionId: "interaction", attemptId: epoch };
const page = { status: "ok", hostEpoch: epoch, sessionId: "session", entries: [{ handle, providerId: "inert", prompts: [{ id: "q", question: "Literal <script>", header: null, options: [], allowFreeform: true }] }], hasMore: false };

test("input is manual and retains original decision across selection/remount", async () => {
  let release!: (value: unknown) => void;
  const original = new Promise<unknown>(resolve => { release = resolve; });
  let calls = 0;
  const owner = createUserInputReviewer(async () => page, () => { calls++; return original; }, () => { calls++; return original; });
  const controller = new AbortController(); const later = new AbortController();
  const work: Promise<unknown>[] = [original]; let primary: unknown;
  try {
    const a = owner.forSelection(epoch, "session", controller.signal, () => {}, () => {});
    assert.equal(calls, 0); await keep(work, () => a.refresh());
    const submit = keep(work, () => a.resolve(handle, [{ promptId: "q", value: "literal\r\n😀" }]));
    assert.equal(a.cancel(handle), submit);
    controller.abort(); const b = owner.forSelection(epoch, "session", later.signal, () => {}, () => {});
    assert.equal(b.cancel(handle), submit);
    release({ status: "resolved", hostEpoch: epoch, handle }); await join(submit, owner);
    assert.equal(calls, 1); assert.equal(owner.observeOriginal()?.kind, "terminal");
    assert.equal(owner.acknowledge(), true);
  } catch (error) { primary = error; throw error; }
  finally { controller.abort(); later.abort(); release({ status: "rejected", hostEpoch: epoch, handle }); await finish(work, owner, primary); }
});

test("late epoch invalidates before obsolete presentation and uncertainty cannot replay", async () => {
  let release!: (value: unknown) => void; const original = new Promise<unknown>(resolve => { release = resolve; });
  let revoked = 0; let calls = 0;
  const owner = createUserInputReviewer(async () => page, () => { calls++; return original; }, async () => { throw new Error("No cancel"); });
  const controller = new AbortController(); const work: Promise<unknown>[] = [original]; let primary: unknown;
  try {
    const view = owner.forSelection(epoch, "session", controller.signal, () => {}, () => { revoked++; });
    await keep(work, () => view.refresh()); const action = keep(work, () => view.resolve(handle, [{ promptId: "q", value: "" }]));
    controller.abort(); release({ status: "stale_epoch", hostEpoch: epoch, handle }); await join(action, owner);
    assert.equal(revoked, 1); assert.equal(calls, 1); assert.equal(owner.acknowledge(), false);
  } catch (error) { primary = error; throw error; }
  finally { controller.abort(); release(null); await finish(work, owner, primary); }
});

test("late list epoch revokes after selection change; no obsolete page is published", async () => {
  let release!: (value: unknown) => void; const original = new Promise<unknown>(resolve => { release = resolve; });
  const owner = createUserInputReviewer(() => original, async () => null, async () => null);
  const old = new AbortController(); const later = new AbortController(); const work: Promise<unknown>[] = [original]; let primary: unknown; let revoked = 0;
  try {
    const a = owner.forSelection(epoch, "session", old.signal, () => {}, () => { revoked++; });
    const read = keep(work, () => a.refresh()); old.abort();
    const b = owner.forSelection(epoch, "other", later.signal, () => {}, () => {});
    release({ ...page, status: "stale_epoch" }); await join(read, owner);
    assert.equal(revoked, 1); assert.equal(owner.blocked(), true); assert.equal(b.page(), undefined);
  } catch (error) { primary = error; throw error; }
  finally { old.abort(); later.abort(); release(null); await finish(work, owner, primary); }
});

test("terminal decisions require local observation then acknowledgment; uncertainty persists", async () => {
  for (const status of ["resolved", "rejected", "cancelled", "uncertain", "malformed"]) {
    let calls = 0; const decide = async () => { calls++; return { status, hostEpoch: epoch, handle }; };
    const owner = createUserInputReviewer(async () => page, decide, decide);
    const controller = new AbortController(); const work: Promise<unknown>[] = []; let primary: unknown;
    try {
      const view = owner.forSelection(epoch, "session", controller.signal, () => {}, () => {});
      await keep(work, () => view.refresh());
      await keep(work, () => status === "cancelled" ? view.cancel(handle) : view.resolve(handle, [{ promptId: "q", value: "" }]));
      assert.equal(owner.acknowledge(), false); assert.equal(calls, 1);
      owner.observeOriginal(); assert.equal(calls, 1);
      assert.equal(owner.acknowledge(), ["resolved", "rejected", "cancelled"].includes(status));
      assert.equal(owner.blocked(), ["uncertain", "malformed"].includes(status));
      await keep(work, () => view.cancel(handle));
      await keep(work, () => view.resolve(handle, [{ promptId: "q", value: "not a new attempt" }]));
      assert.equal(calls, 1, "acknowledgment must not restore retained-page authority; uncertainty must not replay");
    } catch (error) { primary = error; throw error; }
    finally { controller.abort(); await finish(work, owner, primary); }
  }
});

test("terminal acknowledgment invalidates retained pages, including pages fetched during actions and remounts", async () => {
  for (const action of ["resolve", "cancel"] as const)
  for (const rejected of [false, true])
  for (const selection of ["same", "refreshed-during-action", "remounted-during-action"] as const) {
    const work: Promise<unknown>[] = []; const decision = deferred<unknown>(work);
    const controller = new AbortController(); const later = new AbortController(); let primary: unknown;
    const status = rejected ? "rejected" : action === "resolve" ? "resolved" : "cancelled";
    const nextHandle = { ...handle, attemptId: "22222222-2222-4222-8222-222222222222" };
    let listed = page; let reads = 0; const requests: unknown[] = [];
    const decide = (request: unknown) => {
      requests.push(request);
      return requests.length === 1 ? decision.promise : Promise.resolve({ status: "cancelled", hostEpoch: epoch, handle: nextHandle });
    };
    const owner = createUserInputReviewer(async () => { reads++; return listed; }, decide, decide);
    try {
      const first = owner.forSelection(epoch, "session", controller.signal, () => {}, () => {});
      await join(keep(work, () => first.refresh()), owner);
      const submittedHandle = { ...handle }; const answers = [{ promptId: "q", value: "original literal\r\n😀" }];
      const original = keep(work, () => action === "resolve" ? first.resolve(submittedHandle, answers) : first.cancel(submittedHandle));
      submittedHandle.interactionId = "mutated caller handle"; answers[0]!.value = "mutated caller answer";
      let current = first;
      if (selection === "remounted-during-action") {
        controller.abort(); current = owner.forSelection(epoch, "session", later.signal, () => {}, () => {});
      }
      if (selection !== "same") await join(keep(work, () => current.refresh()), owner);
      assert.equal(current.cancel(handle), original, "pending exclusion and original waiter survive remount/refresh");
      decision.release({ status, hostEpoch: epoch, handle }); await join(original, owner);
      assert.deepEqual(requests[0], action === "resolve"
        ? { expectedHostEpoch: epoch, handle, answers: [{ promptId: "q", value: "original literal\r\n😀" }] }
        : { expectedHostEpoch: epoch, handle });
      assert.equal(owner.acknowledge(), false);
      assert.equal(owner.observeOriginal()?.status, status); assert.equal(owner.acknowledge(), true);
      assert.equal(current.page(), undefined, "consumed authority is not presented again after acknowledgment");
      await join(keep(work, () => current.resolve(handle, [{ promptId: "q", value: "stale" }])), owner);
      await join(keep(work, () => current.cancel(handle)), owner);
      assert.equal(requests.length, 1, "frontend must exclude both actions, not rely on backend rejection");
      assert.equal(reads, selection === "same" ? 1 : 2, "acknowledgment does not auto-refresh");
      listed = { ...page, entries: [{ ...page.entries[0]!, handle: nextHandle }] };
      await join(keep(work, () => current.refresh()), owner);
      assert.equal(current.page()?.entries[0]?.handle.attemptId, nextHandle.attemptId);
      await join(keep(work, () => current.cancel(handle)), owner); assert.equal(requests.length, 1);
      await join(keep(work, () => current.cancel(nextHandle)), owner);
      assert.equal(requests.length, 2, "explicit fresh list enables the new attempt with the reused interaction ID");
    } catch (error) { primary = error; throw error; }
    finally { controller.abort(); later.abort(); decision.release(null); await finish(work, owner, primary); }
  }
});

test("pre-action and during-action lists arriving after acknowledgment cannot republish authority and still observe epoch", async () => {
  for (const action of ["resolve", "cancel"] as const)
  for (const rejected of [false, true])
  for (const timing of ["before", "during"] as const)
  for (const remount of [false, true])
  for (const staleEpoch of [false, true]) {
    const work: Promise<unknown>[] = []; const decision = deferred<unknown>(work); const response = deferred<unknown>(work);
    const entered = deferred<void>(work); const controller = new AbortController(); const later = new AbortController(); let primary: unknown;
    let reads = 0; let calls = 0; let invalidations = 0;
    const decide = () => { calls++; return decision.promise; };
    const owner = createUserInputReviewer(() => {
      if (++reads === 1) return Promise.resolve(page);
      entered.release(); return response.promise;
    }, decide, decide);
    try {
      const first = owner.forSelection(epoch, "session", controller.signal, () => {}, () => { invalidations++; });
      await join(keep(work, () => first.refresh()), owner);
      let read: Promise<void> | undefined;
      if (timing === "before") { read = keep(work, () => first.refresh()); await join(entered.promise, owner); }
      const original = keep(work, () => action === "resolve" ? first.resolve(handle, [{ promptId: "q", value: "literal" }]) : first.cancel(handle));
      let view = first;
      if (remount) { controller.abort(); view = owner.forSelection(epoch, "session", later.signal, () => {}, () => { invalidations++; }); }
      if (timing === "during") { read = keep(work, () => view.refresh()); await join(entered.promise, owner); }
      decision.release({ status: rejected ? "rejected" : action === "resolve" ? "resolved" : "cancelled", hostEpoch: epoch, handle });
      await join(original, owner); assert.equal(owner.observeOriginal()?.kind, "terminal"); assert.equal(owner.acknowledge(), true);
      assert.ok(read); response.release(staleEpoch ? { ...page, status: "stale_epoch" } : page); await join(read, owner);
      assert.equal(view.page(), undefined); assert.equal(first.page(), undefined);
      await join(keep(work, () => view.resolve(handle, [{ promptId: "q", value: "stale" }])), owner);
      await join(keep(work, () => view.cancel(handle)), owner);
      assert.equal(calls, 1); assert.equal(reads, 2);
      assert.equal(invalidations, staleEpoch ? 1 : 0, "epoch is observed even for action-invalidated list replies");
      assert.equal(owner.blocked(), staleEpoch);
    } catch (error) { primary = error; throw error; }
    finally { controller.abort(); later.abort(); entered.release(); decision.release(null); response.release(null); await finish(work, owner, primary); }
  }
});

test("reload only explicitly lists pending forms; deep copies and unsupported payloads fail closed", async () => {
  assert.equal(inputPage(page, "session")?.entries[0]?.prompts[0]?.question, "Literal <script>");
  for (const generation of [1, "0", "01", "1.0", "9007199254740992"]) assert.equal(inputHandle({ ...handle, attachmentGeneration: generation }), false);
  for (const prompt of [{ ...page.entries[0]!.prompts[0]!, isSecret: true }, { id: "q", question: "\ud800", header: null, options: [], allowFreeform: true },
    { ...page.entries[0]!.prompts[0]!, allowFreeform: false }, { ...page.entries[0]!.prompts[0]!, options: [{ label: "x", description: null }, { label: "x", description: null }] }])
    assert.equal(inputPage({ ...page, entries: [{ ...page.entries[0], prompts: [prompt] }] }, "session"), undefined);
  const mutable = structuredClone(page); const frozen = inputPage(mutable, "session")!;
  mutable.entries[0]!.prompts[0]!.question = "changed"; assert.equal(frozen.entries[0]!.prompts[0]!.question, "Literal <script>");
  assert.equal(Object.isFrozen(frozen.entries[0]!.prompts), true);
  let calls = 0; const owner = createUserInputReviewer(async () => { calls++; return page; }, async () => null, async () => null);
  const controller = new AbortController(); const work: Promise<unknown>[] = []; let primary: unknown;
  try {
    const view = owner.forSelection(epoch, "session", controller.signal, () => {}, () => {});
    assert.equal(calls, 0); assert.equal(owner.observeOriginal(), undefined);
    await keep(work, () => view.refresh()); assert.equal(calls, 1);
    await keep(work, () => view.resolve(handle, [])); assert.equal(owner.original(), undefined);
    await keep(work, () => view.resolve(handle, [{ promptId: "q", value: "a" }, { promptId: "q", value: "b" }])); assert.equal(owner.original(), undefined);
  } catch (error) { primary = error; throw error; }
  finally { controller.abort(); await finish(work, owner, primary); }
});

test("modal answer payloads keep literal bytes, empty versus missing and existing bounds", async () => {
  const requests: unknown[] = [];
  const owner = createUserInputReviewer(async () => page, async request => {
    requests.push(request); return { status: "resolved", hostEpoch: epoch, handle };
  }, async () => { throw new Error("No cancellation expected"); });
  const controller = new AbortController();
  const view = owner.forSelection(epoch, "session", controller.signal, () => {}, () => {});
  try {
    await view.refresh();
    for (const answers of [[], [{ promptId: "q", value: "x".repeat(2049) }], [{ promptId: "q", value: "\0" }]]) {
      await view.resolve(handle, answers); assert.equal(requests.length, 0);
    }
    for (const value of ["", "  literal\r\n日本語 😀  ", "x".repeat(2048)]) {
      await view.refresh(); await view.resolve(handle, [{ promptId: "q", value }]);
      assert.deepEqual(requests.at(-1), { expectedHostEpoch: epoch, handle, answers: [{ promptId: "q", value }] });
      assert.equal(owner.acknowledge(), false); owner.observeOriginal(); assert.equal(owner.acknowledge(), true);
    }
    assert.equal(requests.length, 3);
  } finally { controller.abort(); }
});

function deferred<T>(work: Promise<unknown>[]) {
  let release!: (value: T) => void;
  const promise = new Promise<T>(resolve => { release = resolve; }); work.push(promise);
  return { promise, release };
}

function keep<T>(work: Promise<unknown>[], start: () => Promise<T>): Promise<T> {
  let resolve!: (value: Promise<T>) => void; let reject!: (reason: unknown) => void;
  const observer = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); work.push(observer);
  try { const original = start(); resolve(original); return original; } catch (error) { reject(error); return observer; }
}
async function join<T>(original: Promise<T>, owner: unknown): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  const deadline = new Promise<never>((_, reject) => { timer = setTimeout(() => reject(Object.assign(new Error("Input deadline; originals retained"), { original, owner })), 5000); });
  try { return await Promise.race([original, deadline]); } finally { if (timer !== undefined) clearTimeout(timer); }
}
async function finish(work: Promise<unknown>[], owner: unknown, primary: unknown) {
  try { const results = await join(Promise.allSettled(work), owner); const failures = results.filter(value => value.status === "rejected"); if (failures.length) throw new AggregateError(failures, "Original failures"); }
  catch (error) { throw new AggregateError(primary === undefined ? [error] : [primary, error], "Input cleanup failed"); }
}
