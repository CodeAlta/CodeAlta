import { test } from "node:test";
import assert from "node:assert/strict";
import { createPromptCreation } from "./promptCreation";
import { createMutationCapability } from "./sessionOperations";
import type { PromptCreateRequest, PromptCreateResponse } from "#neoastra";

const epoch = "11111111-1111-4111-8111-111111111111";
const target = { expectedHostEpoch: epoch, sessionId: "session", createdAt: "2026-09-27T00:00:00+00:00", scope: "global", projectId: null, projectPath: null };
test("explicit scope, review and shadow acknowledgement precede exact immutable publication", async () => {
  let calls = 0;
  const owner = createPromptCreation(async request => { calls++; return { status: "created", hostEpoch: epoch, request }; });
  const capability = createMutationCapability(epoch);
  const capture = { target, capability, current: () => true };
  owner.start(capture);
  owner.update({ promptId: "example", name: "Example", body: "original" });
  assert.equal(owner.review(capture), false);
  owner.update({ rootKind: "user_alta", mode: "replace", understoodShadowing: true });
  assert.equal(owner.review(capture), true);
  assert.equal(calls, 0);
  await owner.confirm();
  assert.equal(owner.getSnapshot().phase, "created");
  assert.equal(owner.getSnapshot().original?.body, "original");
  assert.equal(calls, 1);
});

test("transport ambiguity retains original text and permanently prevents retry or discard", async () => {
  const owner = createPromptCreation(async () => { throw new Error("transport"); });
  const capture = { target, capability: createMutationCapability(epoch), current: () => true };
  owner.start(capture);
  owner.update({ promptId: "example", name: "Example", body: "original", rootKind: "user_alta", mode: "replace", understoodShadowing: true });
  owner.review(capture);
  await owner.confirm();
  owner.discard(); owner.update({ body: "replacement" }); await owner.confirm();
  assert.equal(owner.getSnapshot().phase, "uncertain");
  assert.equal(owner.getSnapshot().original?.body, "original");
});

test("same-value input and navigation ABA require a fresh review before dispatch", async () => {
  let calls = 0; let generation = 0;
  const owner = createPromptCreation(async request => { calls++; return { status: "created", hostEpoch: epoch, request }; });
  const capability = createMutationCapability(epoch);
  const capture = () => { const version = generation; return { target, capability, current: () => version === generation }; };
  owner.start(capture());
  owner.update({ promptId: "example", name: "Example", body: "original", rootKind: "user_alta", mode: "replace", understoodShadowing: true });
  owner.review(capture()); generation++;
  await owner.confirm(); assert.equal(calls, 0);
  owner.update({ body: "changed" }); owner.update({ body: "original" });
  await owner.confirm(); assert.equal(calls, 0);
  owner.review(capture()); await owner.confirm(); assert.equal(calls, 1);
});

test("original-host late success stays confirmed after navigation and capability replacement/invalidation", async () => {
  let release!: (value: PromptCreateResponse) => void; let original!: PromptCreateRequest;
  let active = true;
  const owner = createPromptCreation(request => { original = request; return new Promise(resolve => { release = resolve; }); });
  const capability = createMutationCapability(epoch); const replacement = createMutationCapability(epoch);
  const capture = { target, capability, current: () => active };
  owner.start(capture);
  owner.update({ promptId: "example", name: "Example", body: "original", rootKind: "user_alta", mode: "replace", understoodShadowing: true });
  owner.review(capture);
  owner.subscribe(() => { throw new Error("view fault"); });
  const work = owner.confirm(); active = false;
  capability.observe({ status: "stale_epoch", epoch: "22222222-2222-4222-8222-222222222222" });
  owner.discard(); owner.start(capture); owner.update({ body: "other" });
  assert.equal(owner.getSnapshot().phase, "pending");
  release({ status: "created", hostEpoch: epoch, request: original });
  await work;
  assert.equal(capability.canMutate(), false); assert.equal(replacement.canMutate(), true);
  assert.equal(owner.getSnapshot().phase, "created"); assert.equal(owner.getSnapshot().original, original);
});

for (const status of ["created", "conflict", "refused", "busy", "closed", "uncertain"]) {
  test(`changed-host ${status} invalidates only original authority, never settles or unlocks original publication`, async () => {
    let release!: (value: PromptCreateResponse) => void; let original!: PromptCreateRequest; let calls = 0;
    const owner = createPromptCreation(request => { calls++; original = request; return new Promise(resolve => { release = resolve; }); });
    const capability = createMutationCapability(epoch);
    const replacement = createMutationCapability(epoch);
    const fresh = { target, capability: replacement, current: () => true };
    const capture = { target, capability, current: () => true };
    owner.start(capture);
    owner.update({ promptId: "example", name: "Example", body: "original", rootKind: "user_alta", mode: "replace", understoodShadowing: true });
    owner.review(capture);
    const work = owner.confirm();
    // Both capability and view subscribers try to admit a successor using replacement authority.
    // They must observe an already-blocking state; their faults cannot change the evidence.
    capability.subscribe(() => { owner.next(fresh); owner.discard(); throw new Error("capability observer"); });
    owner.subscribe(() => { owner.next(fresh); owner.discard(); throw new Error("view observer"); });
    release({ status, hostEpoch: "22222222-2222-4222-8222-222222222222", request: original });
    await work;
    assert.equal(capability.canMutate(), false); assert.equal(replacement.canMutate(), true);
    assert.equal(owner.getSnapshot().phase, "uncertain");
    owner.next(fresh); owner.discard(); owner.start(fresh); owner.update({ body: "replacement" });
    assert.equal(owner.review(fresh), false); await owner.confirm();
    assert.equal(owner.getSnapshot().phase, "uncertain");
    assert.equal(owner.getSnapshot().original, original);
    assert.equal(owner.getSnapshot().draft.body, "original");
    assert.equal(owner.getSnapshot().outcomes.length, 0); assert.equal(calls, 1);
  });
}

test("correlated explicit stale_epoch remains a definite refusal from a changed host", async () => {
  const capability = createMutationCapability(epoch);
  const capture = { target, capability, current: () => true };
  const owner = createPromptCreation(async request => ({ status: "stale_epoch", hostEpoch: "22222222-2222-4222-8222-222222222222", request }));
  owner.start(capture); owner.update({ promptId: "example", name: "Example", body: "original", rootKind: "user_alta", mode: "replace", understoodShadowing: true });
  owner.review(capture); await owner.confirm();
  assert.equal(owner.getSnapshot().phase, "refused"); assert.equal(capability.canMutate(), false);
  assert.equal(owner.getSnapshot().original?.body, "original");
});

for (const invalid of ["unknown-status", "invalid-epoch", "missing-echo", "foreign-echo"]) {
  test(`${invalid} cannot promote changed-host evidence or unlock the original`, async () => {
    const capability = createMutationCapability(epoch);
    const capture = { target, capability, current: () => true };
    const owner = createPromptCreation(async request => ({ status: invalid === "unknown-status" ? "unsupported" : "created",
      hostEpoch: invalid === "invalid-epoch" ? "not-an-epoch" : "22222222-2222-4222-8222-222222222222",
      request: invalid === "missing-echo" ? null : invalid === "foreign-echo" ? { ...request, requestId: "foreign" } : request }));
    owner.start(capture); owner.update({ promptId: "example", name: "Example", body: "original", rootKind: "user_alta", mode: "replace", understoodShadowing: true });
    owner.review(capture); await owner.confirm();
    owner.next(capture); owner.discard(); await owner.confirm();
    assert.equal(owner.getSnapshot().phase, "uncertain"); assert.equal(capability.canMutate(), true);
    assert.equal(owner.getSnapshot().original?.body, "original");
  });
}

test("malformed outcomes are uncertain and cannot invalidate shared authority", async () => {
  const capability = createMutationCapability(epoch);
  const capture = { target, capability, current: () => true };
  const owner = createPromptCreation(async request => ({ status: "stale_epoch", hostEpoch: "22222222-2222-4222-8222-222222222222", request: { ...request, body: "foreign" } }));
  owner.start(capture); owner.update({ promptId: "example", name: "Example", body: "original", rootKind: "user_alta", mode: "replace", understoodShadowing: true });
  owner.review(capture); await owner.confirm();
  assert.equal(owner.getSnapshot().phase, "uncertain"); assert.equal(capability.canMutate(), true);
});

test("explicit new drafts retain eight bounded originals without evicting outcomes", async () => {
  const capture = { target, capability: createMutationCapability(epoch), current: () => true };
  const owner = createPromptCreation(async request => ({ status: "created", hostEpoch: epoch, request }));
  owner.start(capture);
  for (let index = 0; index < 8; index++) {
    owner.update({ promptId: `example-${index}`, name: "Example", body: `original-${index}`, rootKind: "user_alta", mode: "replace", understoodShadowing: true });
    owner.review(capture); await owner.confirm(); owner.next(capture);
  }
  assert.equal(owner.getSnapshot().phase, "created");
  assert.equal(owner.getSnapshot().outcomes.length, 7);
  assert.equal(owner.getSnapshot().outcomes[0].original.body, "original-0");
  assert.equal(owner.getSnapshot().original?.body, "original-7");
});
