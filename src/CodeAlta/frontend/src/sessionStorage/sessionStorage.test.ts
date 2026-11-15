import assert from "node:assert/strict";
import test from "node:test";
import type { WorkspaceSessionStorageRequest, WorkspaceSessionStorageResponse } from "#neoastra";
import { createSessionStorageHost } from "./sessionStorage";

const epoch = "11111111-1111-4111-8111-111111111111";
const created = "2026-11-14T23:30:00-10:00";
const answer = (status: string, more: Partial<WorkspaceSessionStorageResponse> = {}): WorkspaceSessionStorageResponse =>
  ({ status, hostEpoch: epoch, sessionId: "s1", folder: "/home/me/.alta/sessions/2026/11/15", file: "/home/me/.alta/sessions/2026/11/15/s1.jsonl", canReveal: true, ...more });

test("the folder of a session is read and shown with the session and the day it was created", async () => {
  const requests: WorkspaceSessionStorageRequest[] = [];
  const invoke = async (request: WorkspaceSessionStorageRequest) => { requests.push(request); return answer("ok"); };
  const host = createSessionStorageHost(invoke, invoke, epoch, "s1", created)!;
  assert.deepEqual(await host.read(), { folder: "/home/me/.alta/sessions/2026/11/15", canReveal: true });
  assert.equal(await host.reveal(), true);
  assert.deepEqual(requests, [{ expectedHostEpoch: epoch, sessionId: "s1", createdAt: created }, { expectedHostEpoch: epoch, sessionId: "s1", createdAt: created }]);
});

test("no host, no creation time, a refusal, another session or a lost answer tell nothing", async () => {
  const ok = async () => answer("ok");
  assert.equal(createSessionStorageHost(ok, ok, null, "s1", created), null);
  assert.equal(createSessionStorageHost(ok, ok, epoch, "s1", null), null);
  for (const reply of [answer("session_missing", { folder: null, file: null }), answer("failed"), answer("ok", { sessionId: "s2" }),
    answer("ok", { hostEpoch: "22222222-2222-4222-8222-222222222222" }), answer("ok", { folder: null })]) {
    const host = createSessionStorageHost(async () => reply, async () => reply, epoch, "s1", created)!;
    assert.equal(await host.read(), null);
    assert.equal(await host.reveal(), false);
  }
  const lost = createSessionStorageHost(async () => { throw new Error("lost"); }, async () => { throw new Error("lost"); }, epoch, "s1", created)!;
  assert.equal(await lost.read(), null);
  assert.equal(await lost.reveal(), false);
  // A system without a file manager still tells the folder.
  assert.deepEqual(await createSessionStorageHost(async () => answer("ok", { canReveal: false }), ok, epoch, "s1", created)!.read(),
    { folder: "/home/me/.alta/sessions/2026/11/15", canReveal: false });
});
