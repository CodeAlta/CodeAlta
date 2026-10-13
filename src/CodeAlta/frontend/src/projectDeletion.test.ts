import test from "node:test";
import assert from "node:assert/strict";
import { createProjectDeletion, projectDeleteCurrent, type ProjectDeleteTarget } from "./projectDeletion";
import type { WorkspaceDeleteProjectRequest, WorkspaceDeleteProjectResponse } from "#neoastra";

const target: ProjectDeleteTarget = { epoch: "epoch", id: "p", path: "/p", name: "Project", archived: true, generation: 1 };
const reply = (request: WorkspaceDeleteProjectRequest, status = "ok"): WorkspaceDeleteProjectResponse => ({ status,
  hostEpoch: request.expectedHostEpoch, projectId: request.projectId, projectPath: request.projectPath });

test("deletion names the exact project, archived or not", async () => {
  const calls: WorkspaceDeleteProjectRequest[] = [];
  const owner = createProjectDeletion(async request => { calls.push(request); return reply(request); });
  for (const archived of [true, false]) {
    const scope = { ...target, archived };
    assert.equal(await owner.confirm(scope, scope), "deleted");
    assert.deepEqual(calls.at(-1), { expectedHostEpoch: target.epoch, projectId: target.id, projectPath: target.path,
      confirmedName: target.name, expectedArchived: archived, confirmed: true });
    assert.equal(Object.isFrozen(calls.at(-1)), true);
  }
  assert.equal(owner.locked, false);
});

test("a project that changed since it was asked about is not deleted", async () => {
  let calls = 0;
  const owner = createProjectDeletion(async request => { calls++; return reply(request); });
  for (const current of [null, { ...target, epoch: "other" }, { ...target, id: "other" }, { ...target, path: "/other" },
    { ...target, name: "Renamed" }, { ...target, archived: false }, { ...target, generation: 3 }]) {
    assert.equal(projectDeleteCurrent(target, current), false);
    assert.equal(await owner.confirm(target, current), null);
  }
  assert.equal(calls, 0);
});

test("one deletion at a time, and a failed one can be asked again", async () => {
  let finish!: (value: WorkspaceDeleteProjectResponse) => void;
  let calls = 0;
  const owner = createProjectDeletion(() => { calls++; return new Promise(resolve => { finish = resolve; }); });
  const original = owner.confirm(target, target);
  assert.equal(owner.locked, true);
  assert.equal(await owner.confirm(target, target), null);
  finish({ status: "delete_unconfirmed", hostEpoch: target.epoch, projectId: target.id, projectPath: target.path });
  assert.equal(await original, "failed");
  assert.equal(owner.locked, false);
  const again = owner.confirm(target, target);
  finish({ status: "ok", hostEpoch: target.epoch, projectId: target.id, projectPath: target.path });
  assert.equal(await again, "deleted");
  assert.equal(calls, 2);
});

test("each refusal says why", async () => {
  const outcomes = { busy: "inUse", session_in_use: "inUse", has_children: "children", scope_missing: "changed", invalid_scope: "changed",
    closed: "failed", confirmation_required: "failed", delete_unconfirmed: "failed", stale_epoch: "failed" };
  for (const [status, outcome] of Object.entries(outcomes)) {
    const owner = createProjectDeletion(async request => reply(request, status));
    assert.equal(await owner.confirm(target, target), outcome, status);
    assert.equal(owner.locked, false);
  }
  for (const rpc of [async (request: WorkspaceDeleteProjectRequest) => ({ ...reply(request), projectId: "other" }),
    async () => { throw new Error("response lost"); }]) {
    const owner = createProjectDeletion(rpc);
    assert.equal(await owner.confirm(target, target), "failed");
    assert.equal(owner.locked, false);
  }
});
