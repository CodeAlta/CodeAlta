import assert from "node:assert/strict";
import test, { afterEach } from "node:test";
import type { WorkspacePreparedWorktreeResponse } from "#neoastra";
import { createPreparedWorktrees, progressPercent, type PreparedWorktreesApi } from "./preparedWorktrees";

const epoch = "11111111-1111-4111-8111-111111111111";
const project = { id: "project-1", path: "C:\\test-owned\\project" };
const answer = (status: string, more: Partial<WorkspacePreparedWorktreeResponse> = {}): WorkspacePreparedWorktreeResponse => ({ status, hostEpoch: epoch, steps: [],
  projectId: project.id, baseBranch: null, branch: null, reason: null, message: null, warning: null, ...more });
const tick = () => new Promise(resolve => setTimeout(resolve, 15));
// Every store of a test stops reading when the test ends, whatever its worktrees are left as.
const stores: { dispose: () => void }[] = [];
afterEach(() => { for (const store of stores.splice(0)) store.dispose(); });

function fixture(prepare: (id: string) => WorkspacePreparedWorktreeResponse = () => answer("preparing")) {
  const states = new Map<string, WorkspacePreparedWorktreeResponse>();
  const calls: string[] = [];
  let ids = 0;
  const api: PreparedWorktreesApi = {
    prepare: async request => { calls.push(`prepare ${request.requestId} ${request.baseBranch}`); const reply = prepare(request.requestId); states.set(request.requestId, reply); return reply; },
    read: async request => states.get(request.requestId) ?? answer("unknown"),
    discard: async request => { calls.push(`discard ${request.requestId}`); return answer("discarded"); },
  };
  const store = createPreparedWorktrees(api, () => `id-${++ids}`, 5);
  stores.push(store);
  return { store, states, calls };
}

test("a worktree is made once for a project, followed until it is ready, and named to its session", async () => {
  const { store, states, calls } = fixture();
  let changes = 0;
  store.subscribe(() => changes++);
  store.ensure(epoch, project, null);
  store.ensure(epoch, project, null);
  assert.equal(store.get(project.id)?.state, "preparing");
  assert.equal(store.preparing(project.id, null), true);
  assert.equal(store.ready(project.id, null), null, "a session does not take a worktree git still makes");
  await tick();
  // What git says while it checks the files out is read again and again.
  const checkout = { kind: "checkout", state: "running", detail: "Updating files:  45% (450/1000)", elapsedMilliseconds: 1200, message: null };
  states.set("id-1", answer("preparing", { steps: [checkout], branch: "alta/quiet-heron-k2m4" }));
  await tick();
  assert.deepEqual(store.get(project.id)?.steps, [checkout]);
  states.set("id-1", answer("ready", { branch: "alta/quiet-heron-k2m4" }));
  await tick();
  assert.equal(store.get(project.id)?.state, "ready");
  assert.equal(store.get(project.id)?.branch, "alta/quiet-heron-k2m4");
  assert.equal(store.ready(project.id, null), "id-1");
  assert.equal(store.ready(project.id, "main"), null, "a worktree of another branch is not taken");
  assert.deepEqual(calls, ["prepare id-1 null"]);
  assert.ok(changes >= 3);

  // Still free after a refused creation: it stays; taken by the session: it is forgotten.
  await store.settle(project.id, "id-1");
  assert.equal(store.ready(project.id, null), "id-1");
  states.set("id-1", answer("used"));
  await store.settle(project.id, "id-1");
  assert.equal(store.get(project.id), undefined);
});

test("another branch replaces the worktree, and a worktree no longer wanted is discarded", async () => {
  const { store, calls } = fixture();
  store.ensure(epoch, project, null);
  store.ensure(epoch, project, "main");
  assert.equal(store.get(project.id)?.base, "main");
  store.ensure(epoch, { id: "project-2", path: "C:\\test-owned\\other" }, null);
  store.keepOnly(new Set(["project-2"]));
  assert.equal(store.get(project.id), undefined);
  store.discard("project-2");
  assert.equal(store.get("project-2"), undefined);
  await tick();
  assert.deepEqual(calls.filter(call => call.startsWith("discard")), ["discard id-1", "discard id-2", "discard id-3"]);
});

test("a failure stays until it is tried again or closed, and a refusal is never asked again in a loop", async () => {
  const { store, states, calls } = fixture(id => answer(id === "id-1" ? "project_missing" : "preparing"));
  store.ensure(epoch, project, null);
  await tick();
  assert.equal(store.get(project.id)?.state, "failed");
  assert.equal(store.get(project.id)?.reason, "project_missing");
  store.ensure(epoch, project, null);
  assert.equal(calls.length, 1, "the same branch is not asked again by itself");

  store.dismiss(project.id);
  assert.equal(store.get(project.id)?.state, "dismissed");
  assert.equal(store.preparing(project.id, null), false, "the prompt no longer waits: its session makes its own worktree");
  store.ensure(epoch, project, null);
  assert.equal(calls.length, 1);

  store.retry(epoch, project);
  await tick();
  assert.equal(calls.length, 2);
  states.set("id-2", answer("failed", { reason: "timeout", message: "git did not answer" }));
  await tick();
  assert.deepEqual([store.get(project.id)?.state, store.get(project.id)?.reason, store.get(project.id)?.message], ["failed", "timeout", "git did not answer"]);
});

test("a host that throws, or has no such call, fails the worktree and never the page", async () => {
  const broken = { prepare: undefined, read: undefined, discard: () => { throw new Error("gone"); } } as unknown as PreparedWorktreesApi;
  const store = createPreparedWorktrees(broken, () => "id-1", 5);
  stores.push(store);
  assert.doesNotThrow(() => store.ensure(epoch, project, null));
  await tick();
  assert.deepEqual([store.get(project.id)?.state, store.get(project.id)?.reason], ["failed", "failed"]);
  assert.doesNotThrow(() => store.discard(project.id));
  await tick();
});

test("another host forgets the worktrees of the last one", () => {
  const { store } = fixture();
  store.ensure(epoch, project, null);
  store.ensure("22222222-2222-4222-8222-222222222222", { id: "project-2", path: "C:\\test-owned\\other" }, null);
  assert.equal(store.get(project.id), undefined);
  assert.equal(store.get("project-2")?.state, "preparing");
});

test("how far git is, from the progress line it wrote", () => {
  assert.equal(progressPercent("Updating files:  45% (450/1000)"), 45);
  assert.equal(progressPercent("Cloning into 'lib'..."), null);
  assert.equal(progressPercent(null), null);
  assert.equal(progressPercent("Receiving objects: 100% (12/12), done."), 100);
});
