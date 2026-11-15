import assert from "node:assert/strict";
import test from "node:test";
import type { DiskUsageCleanEvent, DiskUsageCleanRequest, DiskUsageLastResponse, DiskUsageScanEvent } from "#neoastra";
import type { DiskUsageApi } from "./diskUsage";
import { measurement } from "./diskUsageFixtures";
import { createDiskUsageHub } from "./diskUsageHub";

// A host whose scans the test plays: each scan is a stream it pushes records into.
function host(last: DiskUsageLastResponse) {
  const scans: { push: (event: DiskUsageScanEvent) => void; end: () => void; aborted: boolean }[] = [];
  const cleans: { request: DiskUsageCleanRequest; answer: (events: DiskUsageCleanEvent[] | null) => void }[] = [];
  let lastAsked = 0;
  const api = {
    last: async () => { lastAsked++; return last; },
    scan: async (_request: unknown, options?: { signal?: AbortSignal }) => {
      const queue: DiskUsageScanEvent[] = [];
      let wake: (() => void) | null = null, ended = false;
      const scan = { push: (event: DiskUsageScanEvent) => { queue.push(event); wake?.(); }, end: () => { ended = true; wake?.(); }, aborted: false };
      options?.signal?.addEventListener("abort", () => { scan.aborted = true; ended = true; wake?.(); });
      scans.push(scan);
      return (async function* () {
        for (;;) {
          while (queue.length) yield queue.shift()!;
          if (ended) return;
          await new Promise<void>(resolve => { wake = resolve; });
        }
      })();
    },
    // A clean answers once the test lets it: each waits in `cleans`.
    clean: async (request: DiskUsageCleanRequest) => {
      const answer = await new Promise<DiskUsageCleanEvent[] | null>(resolve => { cleans.push({ request, answer: resolve }); });
      if (!answer) throw new Error("The host did not answer.");
      return (async function* () { yield* answer; })();
    },
  } as unknown as DiskUsageApi;
  return { api, scans, cleans, set last(value: DiskUsageLastResponse) { last = value; }, get lastAsked() { return lastAsked; } };
}
const settle = () => new Promise(resolve => setTimeout(resolve, 0));
const value = measurement();
const progress: DiskUsageScanEvent = { status: "ok", kind: "progress", progress: { phase: "sessions", current: null, checkoutsDone: 0, checkoutsTotal: 0, files: "1" },
  project: null, done: null, problem: null };

test("two views of the disk usage show one state and follow one measurement", async () => {
  const played = host({ status: "ok", measurement: value, running: false, current: false });
  const releases: (() => void)[] = [];
  const hub = createDiskUsageHub(played.api, run => { releases.push(run); });
  const tab = hub.attach("epoch"), settings = hub.attach("epoch");
  await settle();
  assert.equal(hub.getSnapshot().shown?.view.done?.completedAt, value.completedAt);
  assert.equal(hub.getSnapshot().loading, false);
  hub.measure();
  hub.measure();
  await settle();
  assert.equal(played.scans.length, 1, "a second Measure joins the one that runs");
  played.scans[0].push(progress);
  await settle();
  assert.equal(hub.getSnapshot().scan?.progress?.phase, "sessions");
  // One view goes: the measurement goes on for the other.
  tab();
  releases.splice(0).forEach(run => run());
  assert.equal(played.scans[0].aborted, false);
  // The host keeps the measurement of this run, which the end reads again.
  played.last = { status: "ok", measurement: value, running: false, current: true };
  played.scans[0].push({ ...progress, kind: "done", progress: null, done: { startedAt: value.startedAt, completedAt: value.completedAt, projects: 3, sessionsKnown: true, totalBytes: value.totalBytes, totalPartial: false, complete: true, unreadable: [] } });
  await settle(); await settle();
  assert.deepEqual([hub.getSnapshot().scan, hub.getSnapshot().shown?.current, hub.getSnapshot().ending], [null, true, null]);
  settings();
});

test("a measurement stops when the last view goes, and not when one comes back at once", async () => {
  const played = host({ status: "ok", measurement: value, running: false, current: true });
  const releases: (() => void)[] = [];
  const hub = createDiskUsageHub(played.api, run => { releases.push(run); });
  let detach = hub.attach("epoch");
  await settle();
  hub.measure();
  await settle();
  // A view that is mounted again (React mounts twice in development) keeps the measurement.
  detach(); detach = hub.attach("epoch");
  releases.splice(0).forEach(run => run());
  assert.equal(played.scans[0].aborted, false);
  assert.equal(played.scans.length, 1, "the view that came back joins it");
  detach();
  releases.splice(0).forEach(run => run());
  await settle();
  assert.equal(played.scans[0].aborted, true);
  assert.equal(hub.getSnapshot().ending?.kind, "stopped");
});

test("a view that opens while the host measures joins that measurement", async () => {
  const played = host({ status: "ok", measurement: value, running: true, current: true });
  const hub = createDiskUsageHub(played.api, () => { });
  hub.attach("epoch");
  hub.attach("epoch");
  await settle(); await settle();
  assert.equal(played.scans.length, 1);
  assert.ok(hub.getSnapshot().scan);
});

test("a worktree removed anywhere marks its row and its project in every view", async () => {
  const played = host({ status: "ok", measurement: value, running: false, current: true });
  const hub = createDiskUsageHub(played.api, () => { });
  hub.attach("epoch");
  await settle();
  hub.removed("alpha", ["/trees/alpha/busy-finch"]);
  assert.deepEqual([[...hub.getSnapshot().stale.projects], [...hub.getSnapshot().stale.paths]], [["alpha"], ["/trees/alpha/busy-finch"]]);
  hub.removed("beta", []);
  assert.deepEqual([...hub.getSnapshot().stale.projects], ["alpha"], "nothing removed, nothing marked");
});

test("a window without a host says so and measures nothing", async () => {
  const played = host({ status: "ok", measurement: value, running: false, current: true });
  const hub = createDiskUsageHub(played.api, () => { });
  hub.attach(null);
  await settle();
  assert.deepEqual([hub.getSnapshot().loading, hub.getSnapshot().ending], [false, { kind: "refused", status: "unavailable" }]);
  hub.measure();
  await settle();
  assert.equal(played.scans.length, 0);
});

test("a clean runs in the hub: every view sees the checkouts measured again and the totals, also when the view that asked is gone", async () => {
  const played = host({ status: "ok", measurement: value, running: false, current: true });
  const hub = createDiskUsageHub(played.api, () => { });
  hub.attach("epoch");
  await settle();
  const [, heron, , ] = value.projects[0].checkouts;
  const amber = { ...heron, path: "/trees/alpha/amber", name: "amber", measureId: "m-amber" };
  const seen: number[] = [];
  const asked = played.lastAsked;
  const cleaning = hub.clean([{ projectId: "alpha", checkout: heron, paths: ["bin"] }, { projectId: "alpha", checkout: amber, paths: ["bin", "obj"] }],
    { progress: progress => { seen.push(progress.index); } });
  await settle();
  assert.equal(played.cleans.length, 1, "one checkout after the other");
  assert.deepEqual(played.cleans[0].request.paths, ["bin"]);
  const fresh = { ...heron, planned: heron.planned.filter(entry => entry.path !== "bin") };
  played.cleans[0].answer([{ kind: "done", progress: null, result: { status: "ok", message: null, entries: [{ path: "bin", outcome: "removed", bytesBefore: "10", bytesFreed: "10" }],
    bytesFreed: "10", moreRemain: false, checkout: fresh } }]);
  await settle(); await settle();
  assert.deepEqual(hub.getSnapshot().shown?.view.projects[0].checkouts[1].planned.map(entry => entry.path), ["obj"], "the checkout is shown as measured again at once");
  assert.equal(played.cleans.length, 2);
  // The host does not answer the second: the clean still ends, and the totals are read again.
  played.cleans[1].answer(null);
  const answers = await cleaning;
  assert.deepEqual(answers.map(answer => answer.result.status), ["ok", "unconfirmed"]);
  assert.deepEqual(seen, [0, 1]);
  assert.equal(played.lastAsked, asked + 1, "the totals of the host are read once the clean ended");
});

test("a clean that is stopped finishes the checkout it cleans and starts no other", async () => {
  const played = host({ status: "ok", measurement: value, running: false, current: true });
  const hub = createDiskUsageHub(played.api, () => { });
  hub.attach("epoch");
  await settle();
  const heron = value.projects[0].checkouts[1];
  let stop = false;
  const cleaning = hub.clean([{ projectId: "alpha", checkout: heron, paths: ["bin"] }, { projectId: "alpha", checkout: heron, paths: ["obj"] }], { stopped: () => stop });
  await settle();
  stop = true;
  played.cleans[0].answer([{ kind: "done", progress: null, result: { status: "ok", message: null, entries: [], bytesFreed: "0", moreRemain: false, checkout: null } }]);
  const answers = await cleaning;
  assert.equal(answers.length, 1);
  assert.equal(played.cleans.length, 1);
});
