import test from "node:test";
import assert from "node:assert/strict";
import { createProjectDeletion, projectDeleteCurrent, type ProjectDeleteTarget } from "./projectDeletion";
import type { WorkspaceDeleteProjectRequest, WorkspaceDeleteProjectResponse } from "#neoastra";

const target: ProjectDeleteTarget = { epoch: "epoch", id: "p", path: "/p", name: "Project", archived: true, generation: 1 };
const reply = (request: WorkspaceDeleteProjectRequest, status = "ok"): WorkspaceDeleteProjectResponse => ({ status,
  hostEpoch: request.expectedHostEpoch, projectId: request.projectId, projectPath: request.projectPath });

test("deletion confirms exact archived and unarchived projects, not a visible session list", async () => {
  const calls: WorkspaceDeleteProjectRequest[] = [];
  const owner = createProjectDeletion(async request => { calls.push(request); return reply(request); });
  for (const archived of [true, false]) {
    const scope = { ...target, archived };
    assert.equal(await owner.confirm(scope, scope), "confirmed");
    assert.deepEqual(calls.at(-1), { expectedHostEpoch: target.epoch, projectId: target.id, projectPath: target.path,
      confirmedName: target.name, expectedArchived: archived, confirmed: true });
    assert.equal(Object.isFrozen(calls.at(-1)), true);
  }
  assert.equal(owner.locked, false);
});

test("changed scope or navigation ABA does not send a delete", async () => {
  let calls = 0;
  const owner = createProjectDeletion(async request => { calls++; return reply(request); });
  for (const current of [null, { ...target, epoch: "other" }, { ...target, id: "other" }, { ...target, path: "/other" },
    { ...target, name: "Renamed" }, { ...target, archived: false }, { ...target, generation: 3 }]) {
    assert.equal(projectDeleteCurrent(target, current), false);
    assert.equal(await owner.confirm(target, current), null);
  }
  assert.equal(calls, 0);
});

test("held original excludes duplicates; lost and partial replies stay locked across selection changes", async () => {
  let finish!: (value: WorkspaceDeleteProjectResponse) => void;
  let calls = 0;
  const owner = createProjectDeletion(() => { calls++; return new Promise(resolve => { finish = resolve; }); });
  const original = owner.confirm(target, target);
  assert.equal(owner.locked, true);
  assert.equal(await owner.confirm(target, target), null);
  finish({ status: "delete_unconfirmed", hostEpoch: target.epoch, projectId: target.id, projectPath: target.path });
  assert.equal(await original, "uncertain");
  assert.equal(owner.uncertain, true);
  const other = { ...target, id: "other" };
  assert.equal(await owner.confirm(other, other), null);
  assert.equal(calls, 1);
});

test("definite refusals unlock; mismatched success and transport failure do not", async () => {
  for (const status of ["busy", "closed", "scope_missing", "has_children", "session_in_use", "confirmation_required"]) {
    const owner = createProjectDeletion(async request => reply(request, status));
    assert.equal(await owner.confirm(target, target), "refused");
    assert.equal(owner.locked, false);
  }
  for (const rpc of [async (request: WorkspaceDeleteProjectRequest) => ({ ...reply(request), projectId: "other" }),
    async (request: WorkspaceDeleteProjectRequest) => reply(request, "stale_epoch"),
    async () => { throw new Error("response lost"); }]) {
    const owner = createProjectDeletion(rpc);
    assert.equal(await owner.confirm(target, target), "uncertain");
    assert.equal(owner.locked, true);
  }
});
