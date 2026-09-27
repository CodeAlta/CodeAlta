import assert from "node:assert/strict";
import test from "node:test";
import { createSkillsInspection } from "./skillsInspection";
import { createMutationCapability } from "./sessionOperations";
import type { SkillsScanRequest, SkillsScanResponse } from "#neoastra";

const epoch = "11111111-1111-4111-8111-111111111111";
const target = { expectedHostEpoch: epoch, sessionId: "one", createdAt: "2026-01-01T00:00:00Z", scope: "global", projectId: null, projectPath: null, rootKind: "user_alta" };
function capability() { return createMutationCapability(epoch); }
function reply(request: SkillsScanRequest): SkillsScanResponse { return { status: "ok", hostEpoch: epoch, request, traversalStatus: "complete", diagnostics: "None", entriesVisited: 1, directoriesOpened: 1, metadataBytesRead: 20, responseOmitted: 0,
  candidates: [{ id: "0", relativePath: "test/SKILL.md", status: "parsed", diagnostic: "none", name: "test", description: "description" }] }; }

test("explicit only; one original survives dismissal and same-value ABA without publishing rows", async () => {
  let resolve!: (value: SkillsScanResponse) => void;
  const calls: SkillsScanRequest[] = [];
  const owner = createSkillsInspection(request => { calls.push(request); return new Promise(done => { resolve = done; }); });
  const cap = capability();
  assert.equal(calls.length, 0);
  const read = owner.scan(target, () => true, cap);
  assert.equal(calls.length, 1);
  owner.invalidate();
  await owner.scan(target, () => true, cap);
  assert.equal(calls.length, 1);
  resolve(reply(calls[0])); await read;
  assert.equal(owner.getSnapshot().rows.length, 0);
  assert.equal(owner.getSnapshot().busy, false);
});

test("validate correlation before original capability evidence, including late result", async () => {
  let resolve!: (value: SkillsScanResponse) => void;
  let request!: SkillsScanRequest;
  const owner = createSkillsInspection(value => { request = value; return new Promise(done => { resolve = done; }); });
  const cap = capability();
  let read = owner.scan(target, () => true, cap);
  resolve({ ...reply(request), hostEpoch: "22222222-2222-4222-8222-222222222222", request: { ...request, sessionId: "other" } }); await read;
  assert.equal(cap.canMutate(), true);
  read = owner.scan(target, () => true, cap); owner.invalidate();
  resolve({ ...reply(request), status: "stale_epoch", hostEpoch: "22222222-2222-4222-8222-222222222222", candidates: [], traversalStatus: null,
    diagnostics: "none", entriesVisited: 0, directoriesOpened: 0, metadataBytesRead: 0 }); await read;
  assert.equal(cap.canMutate(), false);
  assert.equal(owner.getSnapshot().rows.length, 0);
});

test("malformed projection refused, empty is raw-only, transport uncertainty holds original", async () => {
  let mode = "bad";
  const owner = createSkillsInspection(async request => {
    if (mode === "error") throw Error("private");
    return { ...reply(request), candidates: mode === "bad" ? [{ ...reply(request).candidates[0], relativePath: "../../secret" }] : [] };
  });
  await owner.scan(target, () => true, capability());
  assert.match(owner.getSnapshot().message, /invalid/i);
  mode = "empty"; await owner.scan(target, () => true, capability());
  assert.equal(owner.getSnapshot().rows.length, 0);
  assert.match(owner.getSnapshot().message, /raw/i);
  mode = "error"; await owner.scan(target, () => true, capability());
  assert.equal(owner.getSnapshot().busy, true);
  assert.match(owner.getSnapshot().message, /unknown/i);
  const original = owner.pendingRequest();
  assert.equal(original?.sessionId, target.sessionId);
  assert.equal(original?.expectedHostEpoch, epoch);
  owner.invalidate();
  assert.equal(owner.pendingRequest(), original);
  assert.ok(Object.isFrozen(original));
});

test("subscriber reentrancy cancels before dispatch and faults cannot strand ownership", async () => {
  let calls = 0;
  const owner = createSkillsInspection(async request => { calls++; return reply(request); });
  const unsubscribe = owner.subscribe(() => { if (owner.getSnapshot().source) owner.invalidate(); });
  await owner.scan(target, () => true, capability());
  assert.equal(calls, 0);
  assert.equal(owner.getSnapshot().busy, false);
  unsubscribe();
  owner.subscribe(() => { throw Error("view failure"); });
  await owner.scan(target, () => true, capability());
  assert.equal(calls, 1);
  assert.equal(owner.getSnapshot().busy, false);
  assert.equal(owner.getSnapshot().rows.length, 1);
});

test("root and input ABA fences and invalid diagnostics cannot promote host evidence", async () => {
  let resolve!: (value: SkillsScanResponse) => void;
  let request!: SkillsScanRequest;
  const owner = createSkillsInspection(value => { request = value; return new Promise(done => { resolve = done; }); });
  const cap = capability();
  let current = true;
  let pending = owner.scan(target, () => current, cap);
  current = false; owner.invalidate(); current = true;
  resolve(reply(request)); await pending;
  assert.equal(owner.getSnapshot().rows.length, 0);
  pending = owner.scan(target, () => true, cap);
  resolve({ ...reply(request), hostEpoch: "22222222-2222-4222-8222-222222222222", diagnostics: "arbitrary private diagnostic" });
  await pending;
  assert.equal(cap.canMutate(), true);
  assert.equal(owner.getSnapshot().rows.length, 0);
});
