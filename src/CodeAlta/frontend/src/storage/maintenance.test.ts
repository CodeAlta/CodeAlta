import assert from "node:assert/strict";
import { setImmediate } from "node:timers/promises";
import { test } from "node:test";
import type { StorageMaintenance, StorageRetentionMaintenance } from "#neoastra";
import { disconnectedMaintenance, followMaintenance, maintenanceNotice, retentionProgress, scratchpadReason, type MaintenanceApi, type MaintenanceState } from "./maintenance";

const scratchpad = (state: string): StorageMaintenance => ({ state, removedFiles: "9223372036854775807", removedDirectories: "2",
  processedFiles: "9223372036854775807", processedDirectories: "2", skippedEntries: "0", reasons: [], startedAt: null, completedAt: null });
const retention = (state: string, reason: string | null = null): StorageRetentionMaintenance => ({ state, examined: 1, deleted: 0, skipped: 1, reason, total: 0, freedBytes: "0" });

test("maintenance independently follows both replay streams and keeps wire counts exact", async () => {
  const snapshots: MaintenanceState[] = [];
  const signals: AbortSignal[] = [];
  const api: MaintenanceApi = {
    watchMaintenance: async (request, options) => {
      assert.equal(request.expectedEpoch, "epoch"); signals.push(options!.signal!);
      return (async function* () { yield scratchpad("running"); yield scratchpad("completed"); })();
    },
    watchRetention: async (request, options) => {
      assert.equal(request.expectedEpoch, "epoch"); signals.push(options!.signal!);
      return (async function* () { yield retention("skipped", "external_admission_unqualified"); })();
    },
  };
  const stop = followMaintenance(api, "epoch", value => snapshots.push(value));
  await setImmediate();
  assert.equal(snapshots.at(-1)?.scratchpad?.state, "completed");
  assert.equal(snapshots.at(-1)?.scratchpad?.removedFiles, "9223372036854775807");
  assert.equal(snapshots.at(-1)?.retention?.reason, "external_admission_unqualified");
  stop(); stop();
  assert.equal(signals.length, 2);
  assert.ok(signals.every(signal => signal.aborted));
});

test("late replies from disposed streams cannot publish into a new epoch", async () => {
  let resolveFirst!: (value: AsyncIterable<StorageMaintenance>) => void;
  let resolveSecond!: (value: AsyncIterable<StorageRetentionMaintenance>) => void;
  const first = new Promise<AsyncIterable<StorageMaintenance>>(resolve => { resolveFirst = resolve; });
  const second = new Promise<AsyncIterable<StorageRetentionMaintenance>>(resolve => { resolveSecond = resolve; });
  const snapshots: MaintenanceState[] = [];
  const stop = followMaintenance({ watchMaintenance: () => first, watchRetention: () => second }, "old", value => snapshots.push(value));
  stop();
  resolveFirst((async function* () { yield scratchpad("completed"); })());
  resolveSecond((async function* () { yield retention("completed"); })());
  await setImmediate();
  assert.deepEqual(snapshots, []);
});

test("a failed or prematurely closed stream reports unavailable without losing the other outcome", async () => {
  const snapshots: MaintenanceState[] = [];
  const stop = followMaintenance({
    watchMaintenance: async () => (async function* () { yield scratchpad("completed"); })(),
    watchRetention: async () => (async function* () { yield retention("running"); throw new Error("private transport details"); })(),
  }, "epoch", value => snapshots.push(value));
  await setImmediate();
  assert.equal(snapshots.at(-1)?.retention?.state, "unavailable");
  assert.equal(snapshots.at(-1)?.scratchpad?.state, "completed");
  assert.ok(!JSON.stringify(snapshots).includes("private transport details"));
  stop();

  const empty: MaintenanceState[] = [];
  const stopEmpty = followMaintenance({
    watchMaintenance: async () => (async function* () {})(),
    watchRetention: async () => (async function* () { yield retention("not_started"); })(),
  }, "epoch", value => empty.push(value));
  await setImmediate();
  assert.equal(empty.at(-1)?.scratchpad?.state, "unavailable");
  assert.equal(empty.at(-1)?.retention?.state, "unavailable");
  stopEmpty();
});

test("a transport failure after a final result preserves the known outcome and its counts", async () => {
  for (const state of ["completed", "partial", "skipped", "canceled", "failed"]) {
    const snapshots: MaintenanceState[] = [];
    const stop = followMaintenance({
      watchMaintenance: async () => (async function* () { yield scratchpad(state); throw new Error("disconnected later"); })(),
      watchRetention: async () => (async function* () { yield retention(state, "archive_unavailable"); throw new Error("disconnected later"); })(),
    }, "epoch", value => snapshots.push(value));
    await setImmediate();
    assert.equal(snapshots.at(-1)?.scratchpad?.state, state);
    assert.equal(snapshots.at(-1)?.scratchpad?.removedFiles, "9223372036854775807");
    assert.equal(snapshots.at(-1)?.retention?.state, state);
    assert.equal(snapshots.at(-1)?.retention?.reason, "archive_unavailable");
    stop();
  }
});

test("same-epoch reconnection keeps final outcomes even if a replay fails or arrives late", async () => {
  const initial = { scratchpad: scratchpad("completed"), retention: retention("skipped", "archive_unavailable") };
  const snapshots: MaintenanceState[] = [];
  const stop = followMaintenance({
    watchMaintenance: async () => { throw new Error("still disconnected"); },
    watchRetention: async () => (async function* () { yield retention("running"); throw new Error("late replay"); })(),
  }, "epoch", value => snapshots.push(value), initial);
  await setImmediate();
  assert.deepEqual(snapshots, [], "No transient result can replace a known final outcome for this single startup pass.");
  assert.deepEqual(disconnectedMaintenance(initial), initial);
  stop();
});

test("liveness loss marks unfinished maintenance unavailable without fabricating completion or losing counts", () => {
  const initial = { scratchpad: scratchpad("running"), retention: retention("not_started") };
  const disconnected = disconnectedMaintenance(initial);
  assert.equal(disconnected.scratchpad?.state, "unavailable");
  assert.equal(disconnected.scratchpad?.removedFiles, "9223372036854775807");
  assert.equal(disconnected.retention?.state, "unavailable");
  assert.equal(initial.scratchpad.state, "running", "Previously published snapshots remain immutable.");
});

test("status messages never describe unqualified, disabled or unknown maintenance as success", () => {
  for (const reason of ["external_admission_unqualified", "desktop_admission_unqualified", "protection_unavailable"])
    assert.equal(maintenanceNotice("retention", retention("skipped", reason))?.key, "Session cleanup is not available yet.");
  assert.equal(maintenanceNotice("retention", retention("skipped", "disabled"))?.key, "Session cleanup is off.");
  assert.equal(maintenanceNotice("retention", retention("skipped", "archive_unavailable"))?.key, "Saved Statistics are unavailable.");
  assert.equal(maintenanceNotice("retention", retention("skipped", "invalid_cutoff"))?.key, "Check the retention duration.");
  assert.equal(maintenanceNotice("retention", retention("skipped", "unknown-private"))?.key, "Session cleanup was skipped.");
  assert.equal(maintenanceNotice("retention", retention("unknown"))?.intent, "warning");
  assert.equal(maintenanceNotice("scratchpad", scratchpad("canceled"))?.key, "Scratchpad cleanup was canceled.");
  assert.equal(maintenanceNotice("scratchpad", scratchpad("notstarted")), undefined);
  assert.equal(maintenanceNotice("retention", retention("not_started")), undefined);
  assert.equal(scratchpadReason(["invalid-configuration"]), "Check the storage configuration.");
  assert.equal(scratchpadReason(["unexpected-private"]), "Some scratchpad items were kept.");
});

test("session cleanup progress counts sessions and freed space once the pass has listed them", () => {
  assert.equal(retentionProgress("en", { examined: 0, total: 0, deleted: 0, freedBytes: "0" }), undefined, "Nothing listed yet.");
  assert.equal(retentionProgress("en", { examined: 3, deleted: 0 }), undefined, "An older host sends no total.");
  const progress = retentionProgress("en", { examined: 1234, total: 2048, deleted: 7, freedBytes: String(3 * 1024 * 1024) });
  assert.ok(progress?.startsWith("1,234 of 2,048 sessions checked, 7 removed, "), progress);
  assert.ok(progress?.includes("3"), progress);
  assert.ok(progress?.endsWith(" freed."), progress);
});
