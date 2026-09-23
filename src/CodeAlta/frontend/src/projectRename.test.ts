import assert from "node:assert/strict";
import test from "node:test";
import type { WorkspaceSnapshot } from "#neoastra";
import { createMutationCapability } from "./sessionOperations";
import { createProjectRename, projectNameVisible, projectRenameMessage, projectRenameSelectionCurrent } from "./projectRename";

const epoch = "11111111-1111-4111-8111-111111111111";
const other = "22222222-2222-4222-8222-222222222222";
const path = "C:\\fixture\\project";
const source = "C:\\fixture\\catalog\\projects\\fixture.md";
const revision = "A".repeat(64);
const readReply = (status: string, extra = {}) => ({ status, hostEpoch: epoch, projectId: "p1", projectPath: path,
  sourcePath: status === "ok" ? source : null, revision: status === "ok" ? revision : null,
  displayName: status === "ok" ? "First" : null, ...extra });
const writeReply = (status: string, extra = {}) => ({ status, hostEpoch: epoch, projectId: "p1", projectPath: path,
  sourcePath: source, revision, displayName: status === "ok" ? "New" : null, ...extra });
const snapshot: WorkspaceSnapshot = { configured: true, projects: [{ id: "p1", path, name: "New", archived: false }],
  sessions: [], projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false };

test("preflight freezes source/revision before edit, exact wire response and refreshed ID/path/name gate confirmation", async () => {
  const sent: unknown[] = [];
  const api = createProjectRename(async request => { sent.push(request); return readReply("ok"); },
    async request => { sent.push(request); return writeReply("ok"); });
  const capability = createMutationCapability(epoch);
  const preflight = await api.preflight(epoch, "p1", path, capability);
  assert.equal(preflight.kind, "ready");
  if (preflight.kind !== "ready") return;
  const result = await api.rename(preflight.target, "New", capability);
  assert.equal(result.kind, "renamed");
  assert.deepEqual(sent, [{ expectedHostEpoch: epoch, projectId: "p1", projectPath: path },
    { expectedHostEpoch: epoch, projectId: "p1", projectPath: path, sourcePath: source, revision, displayName: "New" }]);
  assert.equal(projectNameVisible(snapshot, preflight.target, "New"), true);
  for (const project of [{ ...snapshot.projects[0], id: "p2" }, { ...snapshot.projects[0], path: "C:\\other" },
    { ...snapshot.projects[0], name: "Old" }, { ...snapshot.projects[0], archived: true }])
    assert.equal(projectNameVisible({ ...snapshot, projects: [project] }, preflight.target, "New"), false);
  assert.equal(projectRenameSelectionCurrent(preflight.target, epoch, "p1"), true);
  assert.equal(projectRenameSelectionCurrent(preflight.target, epoch, "p2"), false);
  assert.equal(projectRenameSelectionCurrent(preflight.target, other, "p1"), false);
});

test("catalog-only, bad preflight, stale host, conflicts and uncertain write do not retry", async () => {
  let reads = 0;
  let writes = 0;
  const capability = createMutationCapability(epoch);
  const api = createProjectRename(async () => { reads++; return readReply("ok"); }, async () => { writes++; return writeReply("conflict"); });
  assert.deepEqual(await api.preflight(epoch, "p1", path, undefined), { kind: "error", code: "unconfigured" });
  assert.equal(reads, 0);
  const observed = await api.preflight(epoch, "p1", path, capability);
  if (observed.kind !== "ready") throw Error("Expected preflight");
  for (const name of ["", "  ", " padded", "bad\n", "\ud800", "x".repeat(257)])
    assert.deepEqual(await api.rename(observed.target, name, capability), { kind: "error", code: "invalid_scope" });
  assert.equal(writes, 0);
  assert.deepEqual(await api.rename(observed.target, "New", capability), { kind: "error", code: "conflict" });
  assert.equal(writes, 1);
  const lost = createProjectRename(async () => readReply("ok"), async () => { throw Error("post-commit disconnect"); });
  const uncertain = await lost.rename(observed.target, "New", capability);
  assert.deepEqual(uncertain, { kind: "error", code: "rename_unconfirmed" });
  assert.match(projectRenameMessage(uncertain.code), /no retry/i);
  const wrong = createProjectRename(async () => readReply("ok", { projectPath: "wrong" }), async () => writeReply("ok"));
  assert.deepEqual(await wrong.preflight(epoch, "p1", path, capability), { kind: "error", code: "read_failure" });
  const stale = createProjectRename(async () => readReply("stale_epoch", { hostEpoch: other }), async () => writeReply("ok"));
  assert.deepEqual(await stale.preflight(epoch, "p1", path, capability), { kind: "error", code: "stale_epoch" });
  assert.equal(capability.canMutate(), false);
});

test("switch-in-flight and duplicate admission fence callbacks and mismatched write response is uncertain", async () => {
  let finish!: (value: ReturnType<typeof writeReply>) => void;
  const api = createProjectRename(async () => readReply("ok"), async () => new Promise(resolve => { finish = resolve; }));
  const capability = createMutationCapability(epoch);
  const observed = await api.preflight(epoch, "p1", path, capability);
  if (observed.kind !== "ready") throw Error("Expected preflight");
  const pending = api.rename(observed.target, "New", capability);
  assert.deepEqual(await api.rename(observed.target, "Other", capability), { kind: "error", code: "busy" });
  assert.equal(projectRenameSelectionCurrent(observed.target, epoch, "p2"), false);
  finish(writeReply("ok", { revision: "B".repeat(64) }));
  assert.deepEqual(await pending, { kind: "error", code: "rename_unconfirmed" });
});
