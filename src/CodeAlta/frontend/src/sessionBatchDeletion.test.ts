import assert from "node:assert/strict";
import test from "node:test";
import { batchDeleteCandidate, createSessionBatchDeletion, selectVisibleSessions } from "./sessionBatchDeletion";
import type { WorkspaceDeleteSessionRequest, WorkspaceDeleteSessionResponse, WorkspaceSnapshot } from "#neoastra";
import { createMutationCapability } from "./sessionOperations";
const epoch = "00000000-0000-0000-0000-000000000001";
const target = (id: string): WorkspaceDeleteSessionRequest => ({ expectedHostEpoch: epoch, scope: "project", projectId: "p", projectPath: "/p", sessionId: id, confirmedTitle: `Title ${id}` });
const reply = (request: WorkspaceDeleteSessionRequest, status = "ok"): WorkspaceDeleteSessionResponse => ({ ...request, hostEpoch: epoch, status });

test("late correlated changed-host evidence invalidates only the originating shared capability", async () => {
  const original = createMutationCapability(epoch);
  const replacement = createMutationCapability("00000000-0000-0000-0000-000000000002");
  let release!: (value: WorkspaceDeleteSessionResponse) => void;
  let calls = 0;
  const owner = createSessionBatchDeletion(() => { calls++; return new Promise(resolve => { release = resolve; }); });
  owner.review([target("a"), target("b")], () => true, original);
  const work = owner.confirm("DELETE 2"); owner.invalidate();
  original.subscribe(() => { owner.invalidate(); void owner.confirm("DELETE 2"); assert.equal(owner.clearSettled(), false); });
  release({ ...reply(target("a"), "stale_epoch"), hostEpoch: "00000000-0000-0000-0000-000000000002" }); await work;
  assert.equal(original.canSubmit({ expectedEpoch: epoch }), false, "unrelated actions must lose old-host authority");
  assert.equal(replacement.canMutate(), true);
  assert.equal(calls, 1);
  assert.deepEqual(owner.getSnapshot().items.map(row => row.outcome), ["refused", "not-started"]);
});

test("unrelated or malformed evidence stays uncertain and never invalidates shared authority", async () => {
  for (const patch of [{ sessionId: "foreign" }, { projectPath: "/foreign" }, { hostEpoch: "bad" },
    { hostEpoch: "00000000-0000-0000-0000-000000000000" }, { status: "invented" }, { status: "stale_runtime" }]) {
    const capability = createMutationCapability(epoch);
    const owner = createSessionBatchDeletion(async request => ({ ...reply(request, "stale_epoch"), hostEpoch: "00000000-0000-0000-0000-000000000002", ...patch }));
    owner.review([target("a"), target("b")], () => true, capability); await owner.confirm("DELETE 2");
    assert.equal(capability.canMutate(), true);
    assert.deepEqual(owner.getSnapshot().items.map(row => row.outcome), ["uncertain", "not-started"]);
  }
});

test("old-host success remains confirmed after capability invalidation and throwing/reentrant subscribers", async () => {
  const capability = createMutationCapability(epoch);
  let release!: (value: WorkspaceDeleteSessionResponse) => void;
  const owner = createSessionBatchDeletion(() => new Promise(resolve => { release = resolve; }));
  owner.review([target("a"), target("b")], () => true, capability);
  const work = owner.confirm("DELETE 2");
  capability.observe({ status: "stale_epoch", epoch });
  owner.invalidate();
  owner.subscribe(() => { if (owner.getSnapshot().items[0]?.outcome === "deleted") { void owner.confirm("DELETE 2"); throw Error("subscriber fault"); } });
  release(reply(target("a"))); await work;
  assert.deepEqual(owner.getSnapshot().items.map(row => row.outcome), ["deleted", "not-started"]);
  assert.ok(owner.notificationFailure() instanceof Error);
});

test("validated stale epoch or changed-host uncertain success stops before notifying reentrant shared subscribers", async () => {
  for (const changed of [false, true]) {
    const capability = createMutationCapability(epoch), replacement = createMutationCapability(epoch);
    let calls = 0;
    const owner = createSessionBatchDeletion(async request => { calls++; return { ...reply(request, changed ? "ok" : "stale_epoch"),
      hostEpoch: changed ? "00000000-0000-0000-0000-000000000002" : epoch }; });
    capability.subscribe(() => {
      assert.equal(capability.canMutate(), false);
      assert.equal(owner.getSnapshot().items[0].outcome, "pending");
      assert.equal(owner.review([target("other")], () => true, replacement), false);
      void owner.confirm("DELETE 2");
      throw Error("observer fault after invalidation");
    });
    owner.review([target("a"), target("b")], () => true, capability); await owner.confirm("DELETE 2");
    assert.equal(calls, 1);
    assert.equal(replacement.canMutate(), true);
    assert.equal(capability.canMutate(), false);
    assert.ok(capability.notificationFailure() instanceof Error);
    assert.deepEqual(owner.getSnapshot().items.map(row => row.outcome), [changed ? "uncertain" : "refused", "not-started"]);
    if (changed) assert.equal(owner.clearSettled(), false);
  }
  const wrongOwner = createSessionBatchDeletion(async request => reply(request));
  assert.equal(wrongOwner.review([target("a")], () => true, createMutationCapability("different-epoch")), false);
});

test("sequential original survives dismissal; double confirm cannot dispatch and no successor starts", async () => {
  const calls: WorkspaceDeleteSessionRequest[] = [];
  let release!: (value: WorkspaceDeleteSessionResponse) => void;
  const owner = createSessionBatchDeletion(request => { calls.push(request); return new Promise(resolve => { release = resolve; }); });
  assert.equal(owner.review([target("a"), target("b")], () => true, createMutationCapability(epoch)), true);
  const original = owner.confirm("DELETE 2");
  await owner.confirm("DELETE 2");
  assert.equal(calls.length, 1);
  owner.invalidate(); // browser lifetime ended; retained owner remains.
  release(reply(calls[0])); await original;
  assert.deepEqual(owner.getSnapshot().items.map(item => item.outcome), ["deleted", "not-started"]);
  assert.equal(owner.locked(), true);
  assert.equal(owner.clearSettled(), true);
});
test("mixed definite refusals continue; uncertainty stops and permanently blocks local retry", async () => {
  const calls: WorkspaceDeleteSessionRequest[] = [];
  const owner = createSessionBatchDeletion(async request => { calls.push(request); return reply(request, ["has_children", "ok", "delete_unconfirmed"][calls.length - 1]); });
  owner.review(["a", "b", "c", "d"].map(target), () => true, createMutationCapability(epoch));
  await owner.confirm("DELETE 4");
  assert.deepEqual(owner.getSnapshot().items.map(item => item.outcome), ["refused", "deleted", "uncertain", "not-started"]);
  assert.equal(owner.clearSettled(), false);
  assert.equal(owner.review([target("e")], () => true, createMutationCapability(epoch)), false);
});
test("authority ABA and changed review prevent confirmation; frozen titles never retarget", async () => {
  let revision = 1;
  const calls: WorkspaceDeleteSessionRequest[] = [];
  const owner = createSessionBatchDeletion(async request => { calls.push(request); revision++; return reply(request); });
  const a = { ...target("a") }; owner.review([a, target("b")], () => revision === 1, createMutationCapability(epoch));
  a.confirmedTitle = "changed";
  await owner.confirm("DELETE 2");
  assert.equal(calls[0].confirmedTitle, "Title a");
  assert.deepEqual(owner.getSnapshot().items.map(item => item.outcome), ["deleted", "not-started"]);
  owner.clearSettled(); owner.review([target("c")], () => true, createMutationCapability(epoch)); owner.invalidate();
  await owner.confirm("DELETE 1"); assert.equal(calls.length, 1);
});
test("visible selection/invert is capped, excludes hidden selections and does not truncate silently", () => {
  assert.deepEqual(selectVisibleSessions(["hidden", "a"], ["a", "b"], true), ["b"]);
  assert.deepEqual(selectVisibleSessions([], ["a", "b"], false), ["a", "b"]);
  assert.equal(selectVisibleSessions([], Array.from({ length: 33 }, (_, i) => String(i)), false), null);
  assert.equal(selectVisibleSessions([], Array.from({ length: 32 }, (_, i) => String(i)), false)?.length, 32);
});

test("only exact nontruncated nonarchived unique loaded identities become candidates", () => {
  const snapshot: WorkspaceSnapshot = { configured: true, projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false,
    projects: [{ id: "p", path: "/p", name: "Project", archived: false }], sessions: [{ id: "a", title: "short", fullTitle: "Exact title", fullTitleTruncated: false,
      messageCount: null, createdAt: null, updatedAt: "saved", parentSessionId: null, lineageIssue: null, providerKey: null, scopeKind: "project", projectId: "p", workspacePath: "/p" }] };
  const tab = { sessionId: "a", projectId: "p", path: "/p" };
  assert.equal(batchDeleteCandidate(snapshot, tab, epoch)?.confirmedTitle, "Exact title");
  for (const changed of [{ ...snapshot, configured: false }, { ...snapshot, sessionsTruncated: true }, { ...snapshot, projectsTruncated: true },
    { ...snapshot, displayTextTruncated: true }, { ...snapshot, projects: [{ ...snapshot.projects[0], archived: true }] },
    { ...snapshot, projects: [...snapshot.projects, { ...snapshot.projects[0], id: "P" }] },
    { ...snapshot, sessions: [...snapshot.sessions, { ...snapshot.sessions[0], id: "A" }] },
    { ...snapshot, sessions: [{ ...snapshot.sessions[0], fullTitleTruncated: true }] },
    { ...snapshot, sessions: [{ ...snapshot.sessions[0], fullTitle: "\ud800" }] }]) assert.equal(batchDeleteCandidate(changed, tab, epoch), null);
  assert.equal(batchDeleteCandidate(snapshot, { ...tab, path: "/other" }, epoch), null);
  const global = { ...snapshot, sessions: [{ ...snapshot.sessions[0], scopeKind: "global", projectId: null, workspacePath: "/global" }] };
  assert.equal(batchDeleteCandidate(global, { ...tab, projectId: null, path: "/global" }, epoch)?.scope, "global");
});

test("confirmation/cap/scope rejection sends nothing; one original per iteration at most", async () => {
  let calls = 0;
  const owner = createSessionBatchDeletion(async request => { calls++; return reply(request); });
  for (const rows of [[], Array.from({ length: 33 }, (_, i) => target(String(i))), [target("a"), target("A")],
    [target("a"), { ...target("b"), projectPath: "/other" }]]) assert.equal(owner.review(rows, () => true, createMutationCapability(epoch)), false);
  owner.review([target("a")], () => true, createMutationCapability(epoch));
  await owner.confirm("delete 1"); assert.equal(calls, 0);
  // A synchronous navigation during pending publication must still prevent dispatch.
  let stop = () => {};
  stop = owner.subscribe(() => { if (owner.getSnapshot().items.some(item => item.outcome === "pending")) { stop(); owner.invalidate(); } });
  await owner.confirm("DELETE 1"); assert.equal(calls, 0);
  assert.equal(owner.getSnapshot().items[0].outcome, "not-started");
});

test("thrown/foreign/malformed original outcomes stay uncertain; correlated refusals remain definite", async () => {
  for (const mode of ["throw", "foreign", "malformed", "invalid_scope"]) {
    const owner = createSessionBatchDeletion(async request => {
      if (mode === "throw") throw Error("private failure");
      if (mode === "foreign") return { ...reply(request), sessionId: "other" };
      if (mode === "invalid_scope") return { status: mode, hostEpoch: epoch, scope: null, projectId: null, projectPath: null, sessionId: null };
      return reply(request, "unexpected-status");
    });
    owner.review([target("a"), target("b")], () => true, createMutationCapability(epoch)); await owner.confirm("DELETE 2");
    assert.deepEqual(owner.getSnapshot().items.map(row => row.outcome), [mode === "invalid_scope" ? "refused" : "uncertain", "not-started"]);
    assert.equal(owner.clearSettled(), mode === "invalid_scope");
  }
});
