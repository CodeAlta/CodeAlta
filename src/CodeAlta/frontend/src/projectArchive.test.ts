import test from "node:test";
import assert from "node:assert/strict";
import { archiveScopeCurrent, createProjectArchive, type ArchiveScope } from "./projectArchive";
import type { WorkspaceArchiveProjectRequest, WorkspaceArchiveProjectResponse } from "#neoastra";

const scope: ArchiveScope = { epoch: "11111111-1111-4111-8111-111111111111", id: "p", path: "/p", archived: false, generation: 1 };
const response = (request: WorkspaceArchiveProjectRequest): WorkspaceArchiveProjectResponse => ({ status: request.confirmed ? "ok" : "confirmation_required",
  hostEpoch: request.expectedHostEpoch, projectId: request.projectId, projectPath: request.projectPath,
  sourcePath: "/catalog/p.md", revision: "A".repeat(64), archived: request.confirmed ? request.archived : request.expectedArchived });

test("archive and explicit unarchive freeze exact intent and retain definite outcomes", async () => {
  const calls: WorkspaceArchiveProjectRequest[] = [];
  const owner = createProjectArchive(async request => { calls.push(request); return response(request); });
  for (const archived of [false, true]) {
    const current = { ...scope, archived };
    const target = await owner.prepare(current); assert.notEqual(typeof target, "string"); if (typeof target === "string") return;
    assert.equal(Object.isFrozen(target), true);
    assert.equal((await owner.confirm(target, current))?.state, "confirmed");
    assert.equal(calls.at(-1)?.archived, !archived);
    assert.equal(calls.at(-1)?.sourcePath, target.source);
  }
  assert.equal(owner.records.length, 2);
});

test("held original blocks duplicate clicks and unknown results remain locked across reads", async () => {
  let settle!: (response: WorkspaceArchiveProjectResponse) => void;
  let calls = 0;
  const owner = createProjectArchive(request => { calls++; return request.confirmed ? new Promise(resolve => { settle = resolve; }) : Promise.resolve(response(request)); });
  const target = await owner.prepare(scope); if (typeof target === "string") assert.fail(target);
  const original = owner.confirm(target, scope);
  assert.equal(owner.records[0].state, "pending");
  assert.equal(await owner.confirm(target, scope), null);
  assert.equal(typeof await owner.prepare({ ...scope, id: "other" }), "string");
  settle({ ...response({ expectedHostEpoch: scope.epoch, projectId: scope.id, projectPath: scope.path,
    expectedArchived: false, archived: true, confirmed: true, sourcePath: target.source, revision: target.revision }), status: "archive_unconfirmed" });
  assert.equal((await original)?.state, "uncertain");
  assert.equal(await owner.confirm(target, scope), null);
  assert.equal(typeof await owner.prepare(scope), "string");
  assert.equal(calls, 2);
  assert.equal(owner.records[0].target, target);
});

test("project host source state and dialog ABA refuse stale captures, catalog-only cannot confirm", async () => {
  let writes = 0;
  const owner = createProjectArchive(async request => { if (request.confirmed) writes++; return response(request); });
  const target = await owner.prepare(scope); if (typeof target === "string") assert.fail(target);
  for (const changed of [null, { ...scope, epoch: "other" }, { ...scope, id: "other" }, { ...scope, path: "/moved" },
    { ...scope, archived: true }, { ...scope, generation: 3 }]) {
    assert.equal(archiveScopeCurrent(target, changed), false);
    assert.equal(await owner.confirm(target, changed), null);
  }
  assert.equal(writes, 0);
});

test("conflicts are definite refusals; bad success evidence and lost replies are uncertain", async () => {
  for (const mode of ["conflict", "wrong-source", "throw"]) {
    const owner = createProjectArchive(async request => {
      if (!request.confirmed) return response(request);
      if (mode === "throw") throw new Error("response lost after write");
      return mode === "conflict" ? { ...response(request), status: mode } : { ...response(request), sourcePath: "/other" };
    });
    const target = await owner.prepare(scope); if (typeof target === "string") assert.fail(target);
    assert.equal((await owner.confirm(target, scope))?.state, mode === "conflict" ? "refused" : "uncertain");
    assert.equal(owner.locked, mode !== "conflict");
  }
});
