import assert from "node:assert/strict";
import test from "node:test";
import { createHostLiveness } from "./hostLiveness";
import { reloadsAfterClose, rpcSessionClosed, sessionRecoveryInterval } from "./sessionRecovery";

test("only a session closed by the host reloads the window", () => {
  assert.equal(reloadsAfterClose(rpcSessionClosed, false, 1_000_000, null), true);
  for (const reason of ["navigation", "client_close", "application_shutdown", "view_disposed", "renderer_lost", "unspecified", undefined])
    assert.equal(reloadsAfterClose(reason, false, 1_000_000, null), false, String(reason));
});

test("unsaved edits and a recent automatic reload keep the window as it is", () => {
  assert.equal(reloadsAfterClose(rpcSessionClosed, true, 1_000_000, null), false);
  assert.equal(reloadsAfterClose(rpcSessionClosed, false, 1_000_000, 1_000_000 - sessionRecoveryInterval + 1), false);
  assert.equal(reloadsAfterClose(rpcSessionClosed, false, 1_000_000, 1_000_000 - sessionRecoveryInterval), true);
  // A stored time that is not a number or lies in the future does not block recovery forever.
  assert.equal(reloadsAfterClose(rpcSessionClosed, false, 1_000_000, Number.NaN), true);
  assert.equal(reloadsAfterClose(rpcSessionClosed, false, 1_000_000, 2_000_000), true);
});

test("a lost connection is silent at once and stays so", async () => {
  const liveness = createHostLiveness(async () => "ok");
  let changes = 0;
  liveness.subscribe(() => { changes++; });
  liveness.lost();
  assert.equal(liveness.getSnapshot(), true);
  assert.equal(changes, 1);
  liveness.lost();
  assert.equal(changes, 1);
  await liveness.check();
  assert.equal(liveness.getSnapshot(), true, "A closed connection cannot answer: only a reload recovers.");
});
