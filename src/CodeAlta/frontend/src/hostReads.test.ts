import assert from "node:assert/strict";
import test from "node:test";
import type { ModelCatalogProvidersResponse, SessionPermissionResolution, SessionPermissionsPage, SessionReceiptPage } from "#neoastra";
import { createHostReads } from "./hostReads";

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(done => { resolve = done; });
  return { promise, resolve };
}

const epoch = "12345678-1234-1234-1234-123456789abc";
const handle = { operationId: epoch, runtimeInstanceId: epoch, attachmentGeneration: "1", sessionId: "child", runId: null, interactionId: "ask", attemptId: epoch };

test("the panes share the providers while fresh and the receipts while a read waits", async () => {
  let time = 0;
  const calls: string[] = [];
  let receipts = deferred<SessionReceiptPage>();
  const reads = createHostReads({
    providers: async request => { calls.push(`providers:${request.expectedEpoch}`); return { epoch, providers: [], status: "ok" } as unknown as ModelCatalogProvidersResponse; },
    receipts: request => { calls.push(`receipts:${request.offset}`); return receipts.promise; },
    permissions: async () => { throw new Error("not read"); },
    resolvePermission: async () => { throw new Error("not resolved"); },
  }, () => time);
  await reads.providers({ expectedEpoch: epoch });
  time = 4_999;
  await reads.providers({ expectedEpoch: epoch });
  assert.deepEqual(calls, [`providers:${epoch}`], "the providers read five seconds ago serve every pane");
  time = 5_000;
  await reads.providers({ expectedEpoch: epoch });
  assert.equal(calls.length, 2);

  const first = reads.receipts({ expectedEpoch: epoch, offset: 0 });
  const second = reads.receipts({ expectedEpoch: epoch, offset: 0 });
  assert.equal(calls.filter(call => call === "receipts:0").length, 1, "two panes that read at once share the read");
  receipts.resolve({ status: "ok", epoch, rows: [], next: null } as unknown as SessionReceiptPage);
  assert.equal(await first, await second);
  receipts = deferred<SessionReceiptPage>();
  void reads.receipts({ expectedEpoch: epoch, offset: 0 });
  assert.equal(calls.filter(call => call === "receipts:0").length, 2, "an answer is not kept: the next read is a new one");
});

test("a decision makes the next read of the permissions of its session a new one", async () => {
  const lists: string[] = [];
  let listed = deferred<SessionPermissionsPage>();
  const resolved = deferred<SessionPermissionResolution>();
  const reads = createHostReads({
    providers: async () => { throw new Error("not read"); },
    receipts: async () => { throw new Error("not read"); },
    permissions: request => { lists.push(request.sessionId); return listed.promise; },
    resolvePermission: () => resolved.promise,
  });
  const request = { expectedHostEpoch: epoch, sessionId: "child" };
  const before = reads.permissions(request);
  assert.equal(await Promise.race([reads.permissions(request).then(() => "answered"), Promise.resolve("waits")]), "waits");
  assert.equal(lists.length, 1, "the pane and the sub-session panel above another pane share the read that waits");
  const decision = reads.resolvePermission({ expectedHostEpoch: epoch, handle, decision: "deny" }, {});
  const older = listed;
  listed = deferred<SessionPermissionsPage>();
  void reads.permissions(request);
  assert.equal(lists.length, 2, "a read that began before the decision may list the request still: it is not joined");
  resolved.resolve({ status: "resolved", hostEpoch: epoch, handle } as unknown as SessionPermissionResolution);
  await decision;
  void reads.permissions(request);
  assert.equal(lists.length, 3, "after the answer the next read is new again");
  older.resolve({ status: "ok" } as unknown as SessionPermissionsPage);
  await before;
});
