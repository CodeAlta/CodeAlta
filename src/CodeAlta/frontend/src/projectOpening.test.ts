import assert from "node:assert/strict";
import test from "node:test";
import { createMutationCapability } from "./sessionOperations";
import { canImportCheckedFolder, createProjectOpening, projectOpeningMessage } from "./projectOpening";

const epoch = "11111111-1111-4111-8111-111111111111";
const other = "22222222-2222-4222-8222-222222222222";
const path = "C:\\tasks\\folder";
const response = (status: string, requestedPath: string | null = path, projectPath: string | null = path,
  projectId: string | null = null, hostEpoch: string | null = epoch) => ({ status, hostEpoch, requestedPath, projectPath, projectId });

test("import control and handler both require a checked preview, permission and no outstanding request", () => {
  const ready = { requestedPath: path, path };
  assert.equal(canImportCheckedFolder(undefined, true, false, true), false);
  assert.equal(canImportCheckedFolder(ready, false, false, true), false);
  assert.equal(canImportCheckedFolder(ready, true, true, true), false);
  assert.equal(canImportCheckedFolder(ready, true, false, false), false);
  assert.equal(canImportCheckedFolder(ready, true, false, true), true);
});

test("an absolute folder is checked before explicit import; no writes or automatic retries", async () => {
  const calls: Array<{ confirmed: boolean; directoryPath: string; expectedHostEpoch: string }> = [];
  const open = createProjectOpening(async (request, options) => {
    assert.equal(options.timeoutMilliseconds, 10000);
    calls.push(request);
    return request.confirmed ? response("ok", path, path, "persisted-id") : response("confirmation_required");
  });
  const capability = createMutationCapability(epoch);
  const ready = await open.preview(epoch, path, capability);
  assert.deepEqual(ready, { kind: "ready", requestedPath: path, path });
  assert.deepEqual(calls.map(call => call.confirmed), [false]);
  if (ready.kind !== "ready") throw new Error("missing preview");
  assert.deepEqual(await open.import(epoch, ready, capability), { kind: "imported", path, id: "persisted-id" });
  assert.deepEqual(calls.map(call => call.confirmed), [false, true]);
  assert.deepEqual(calls.map(call => call.directoryPath), [path, path]);
  assert.deepEqual(calls.map(call => call.expectedHostEpoch), [epoch, epoch]);
});

test("bad input and catalog-only mode do not call the bridge", async () => {
  let calls = 0; const open = createProjectOpening(async () => { calls++; return response("ok"); });
  const capability = createMutationCapability(epoch);
  for (const candidate of ["", "  ", path + "\0", " " + path, "\ud800", "x".repeat(4097)])
    assert.deepEqual(await open.preview(epoch, candidate, capability), { kind: "error", code: "invalid_request" });
  assert.deepEqual(await open.preview(undefined, path, undefined), { kind: "error", code: "unconfigured" });
  assert.equal(calls, 0);
});

test("a filesystem root is previewable and never imports without an explicit second request", async () => {
  const root = "C:\\";
  let calls = 0;
  const open = createProjectOpening(async request => {
    calls++;
    assert.equal(request.directoryPath, root);
    assert.equal(request.confirmed, false);
    return response("confirmation_required", root, root);
  });
  assert.deepEqual(await open.preview(epoch, root, createMutationCapability(epoch)),
    { kind: "ready", requestedPath: root, path: root });
  assert.equal(calls, 1);
});

test("refusal, malformed replies and host change never authorize an import", async () => {
  const capability = createMutationCapability(epoch);
  const refusals = [response("missing_directory", path, null), response("invalid_request", null, null),
    response("confirmation_required", "C:\\different", path), response("confirmation_required", path, null)];
  let count = 0;
  const open = createProjectOpening(async () => refusals[count++]);
  assert.deepEqual(await open.preview(epoch, path, capability), { kind: "error", code: "missing_directory" });
  assert.deepEqual(await open.preview(epoch, path, capability), { kind: "error", code: "invalid_request" });
  assert.deepEqual(await open.preview(epoch, path, capability), { kind: "error", code: "invalid_response" });
  assert.deepEqual(await open.preview(epoch, path, capability), { kind: "error", code: "invalid_response" });
  assert.equal(count, 4);
  const stale = createProjectOpening(async () => response("stale_epoch", null, null, null, other));
  assert.deepEqual(await stale.preview(epoch, path, capability), { kind: "error", code: "stale_epoch" });
  assert.equal(capability.canMutate(), false);
  assert.deepEqual(await stale.preview(epoch, path, capability), { kind: "error", code: "unconfigured" });
});

test("lost, mismatched and concurrent import responses retain uncertainty without retrying", async () => {
  const capability = createMutationCapability(epoch);
  let resolve!: (value: ReturnType<typeof response>) => void;
  let calls = 0; const open = createProjectOpening(async () => { calls++; return new Promise(resolveOriginal => { resolve = resolveOriginal; }); });
  const ready = { requestedPath: path, path };
  const original = open.import(epoch, ready, capability);
  assert.deepEqual(await open.import(epoch, ready, capability), { kind: "error", code: "busy" });
  resolve(response("ok", path, "C:\\other", "persisted-id"));
  assert.deepEqual(await original, { kind: "error", code: "import_unconfirmed" });
  assert.equal(calls, 1);
  const lost = createProjectOpening(async () => { throw new Error("bridge timeout after commit"); });
  assert.deepEqual(await lost.import(epoch, ready, capability), { kind: "error", code: "import_unconfirmed" });
  assert.match(projectOpeningMessage("import_unconfirmed"), /no automatic retry/i);
});

test("app-owned confirmed import evidence survives observers and blocks remounted requests until exact verification", async () => {
  const capability = createMutationCapability(epoch);
  const ready = { requestedPath: path, path };
  let resolve!: (reply: ReturnType<typeof response>) => void;
  let calls = 0; let notifications = 0;
  const opening = createProjectOpening(async () => { calls++; return new Promise(done => { resolve = done; }); });
  const unsubscribe = opening.subscribe(() => notifications++);
  const first = opening.import(epoch, ready, capability);
  assert.deepEqual(opening.getSnapshot(), { kind: "pending", epoch, requestedPath: path, path });
  assert.deepEqual(await opening.import(other, { requestedPath: "C:\\other", path: "C:\\other" }, capability), { kind: "error", code: "busy" });
  assert.deepEqual(await opening.preview(epoch, path, capability), { kind: "error", code: "busy" });
  resolve(response("ok", path, path, "persisted-id"));
  assert.deepEqual(await first, { kind: "imported", path, id: "persisted-id" });
  assert.deepEqual(opening.getSnapshot(), { kind: "imported", epoch, requestedPath: path, path, projectId: "persisted-id" });
  assert.equal(opening.confirmOpened(other, path, "persisted-id"), false);
  assert.equal(opening.confirmOpened(epoch, "C:\\other", "persisted-id"), false);
  assert.equal(opening.confirmOpened(epoch, path, "other-id"), false);
  assert.equal(calls, 1);
  assert.equal(opening.confirmOpened(epoch, path, "persisted-id"), true);
  assert.equal(opening.getSnapshot(), null);
  assert.equal(notifications, 3);
  unsubscribe();
});

test("uncertain import retains original epoch and paths; only a proved refusal unlocks", async () => {
  const ready = { requestedPath: path, path: "C:\\normalized" };
  let calls = 0;
  const uncertain = createProjectOpening(async () => { calls++; throw new Error("response lost"); });
  assert.deepEqual(await uncertain.import(epoch, ready, createMutationCapability(epoch)), { kind: "error", code: "import_unconfirmed" });
  assert.deepEqual(uncertain.getSnapshot(), { kind: "uncertain", epoch, ...ready });
  assert.deepEqual(await uncertain.import(epoch, ready, createMutationCapability(epoch)), { kind: "error", code: "busy" });
  assert.equal(uncertain.confirmOpened(epoch, ready.path, "guessed-id"), false);
  assert.equal(calls, 1);
  const refused = createProjectOpening(async () => response("missing_directory", path, null));
  assert.deepEqual(await refused.import(epoch, ready, createMutationCapability(epoch)), { kind: "error", code: "missing_directory" });
  assert.equal(refused.getSnapshot(), null);
});
