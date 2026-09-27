import assert from "node:assert/strict";
import test from "node:test";
import type { WorkspaceProject, WorkspaceSnapshot } from "#neoastra";
import { projectRowAccess, projectRowCurrent, type ProjectRowContext } from "./projectRowActionAccess";

const project: WorkspaceProject = { id: "p", path: "/p", name: "Project", archived: false };
function context(): ProjectRowContext {
  const snapshot: WorkspaceSnapshot = { configured: true, projects: [project], sessions: [], projectsTruncated: false,
    sessionsTruncated: false, displayTextTruncated: false };
  return { snapshot, projectId: "p", sessionId: "s", hostEpoch: "epoch", hostAvailable: true, refreshVersion: 1,
    refreshReady: true, active: true, generation: 1, modalGeneration: 1, canMutate: true, locked: false };
}

test("project menu permits read-only other rows but never retargets mutation", () => {
  const current = context();
  assert.deepEqual(projectRowAccess(project, current), { open: true, details: true, rename: true, archive: true });
  for (const changed of [{ projectId: "other" }, { canMutate: false }, { hostAvailable: false }, { locked: true }]) {
    assert.deepEqual(projectRowAccess(project, { ...current, ...changed }), { open: true, details: true, rename: false, archive: false });
  }
  const archived = { ...project, archived: true };
  assert.deepEqual(projectRowAccess(archived, { ...current, snapshot: { ...current.snapshot!, projects: [archived] } }),
    { open: true, details: true, rename: false, archive: true });
});

test("missing, duplicate, changed and unready catalogs grant no project action", () => {
  const current = context();
  for (const projects of [[], [project, project], [project, { ...project, id: "other" }],
    [{ ...project, path: "/changed" }], [{ ...project, name: "changed" }], [{ ...project, archived: true }]]) {
    assert.equal(projectRowAccess(project, { ...current, snapshot: { ...current.snapshot!, projects } }).open, false);
  }
  assert.equal(projectRowAccess(project, { ...current, refreshReady: false }).details, false);
});

test("catalog, scope, epoch, refresh and modal generations fence equality after ABA", () => {
  const captured = context();
  assert.equal(projectRowCurrent(project, captured, captured), true);
  for (const changed of [{ snapshot: { ...captured.snapshot! } }, { generation: 3 }, { modalGeneration: 3 },
    { refreshVersion: 3 }, { hostEpoch: "other" }, { hostAvailable: false }, { projectId: "other" },
    { sessionId: "other" }, { active: false }, { refreshReady: false }, { canMutate: false }, { locked: true }]) {
    assert.equal(projectRowCurrent(project, captured, { ...captured, ...changed }), false, JSON.stringify(changed));
  }
});
