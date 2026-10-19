import assert from "node:assert/strict";
import test from "node:test";
import { createHostLiveness } from "./hostLiveness";

function deferred() {
  let resolve!: () => void;
  let reject!: (error: Error) => void;
  const promise = new Promise<void>((done, fail) => { resolve = done; reject = fail; });
  return { promise, resolve, reject };
}

test("a ping that waits makes the host busy for as long as it waits, and its answer clears it", async () => {
  let time = 0;
  let answer = deferred();
  const reports: string[] = [];
  const liveness = createHostLiveness(() => answer.promise, { now: () => time, pingInterval: () => 10_000, busyAfter: 10_000,
    report: (level, message) => reports.push(`${level}: ${message}`) });
  const changes: string[] = [];
  liveness.subscribe(() => { const state = liveness.getSnapshot(); changes.push(`${state.busy}:${state.seconds}`); });
  const ping = liveness.check();
  time = 9_000; await liveness.check();
  assert.equal(liveness.getSnapshot().busy, false, "a host a little slow is not busy yet");
  time = 12_000; await liveness.check();
  assert.deepEqual(liveness.getSnapshot(), { busy: true, seconds: 12, lost: false });
  const same = liveness.getSnapshot();
  await liveness.check();
  assert.equal(liveness.getSnapshot(), same, "the snapshot changes only with what it says");
  time = 31_000; await liveness.check();
  assert.equal(liveness.getSnapshot().seconds, 31);
  answer.resolve();
  await ping;
  assert.deepEqual(liveness.getSnapshot(), { busy: false, seconds: 0, lost: false });
  assert.deepEqual(changes, ["true:12", "true:31", "false:0"]);
  assert.deepEqual(reports, ["warning: The host has not answered for 12 s; the window waits for it.", "information: The host answered again after 31.0 s."]);
  // A host that answers is pinged again only once the interval passed.
  answer = deferred();
  let pings = 0;
  const counted = createHostLiveness(() => { pings++; return Promise.resolve(); }, { now: () => time });
  await counted.check(); await counted.check();
  time += 10_000; await counted.check();
  assert.equal(pings, 2);
});

test("a failed ping is an answer: the host is there", async () => {
  let time = 0;
  const failure = deferred();
  const liveness = createHostLiveness(() => failure.promise, { now: () => time, busyAfter: 1_000 });
  const ping = liveness.check();
  time = 5_000; await liveness.check();
  assert.equal(liveness.getSnapshot().busy, true);
  failure.reject(new Error("internal_error"));
  await ping;
  assert.equal(liveness.getSnapshot().busy, false);
});

test("a ping still waiting is not sent again", async () => {
  let pings = 0;
  let time = 0;
  const liveness = createHostLiveness(() => { pings++; return new Promise<void>(() => {}); }, { now: () => time });
  void liveness.check();
  for (let index = 0; index < 5; index++) { time += 10_000; await liveness.check(); }
  assert.equal(pings, 1);
  assert.equal(liveness.getSnapshot().seconds, 50);
});

test("unsubscribed listeners are not called, and a listener that throws does not stop the others", async () => {
  let time = 0;
  const liveness = createHostLiveness(() => new Promise<void>(() => {}), { now: () => time, busyAfter: 1 });
  let calls = 0;
  const unsubscribe = liveness.subscribe(() => { calls++; });
  unsubscribe();
  let heard = 0;
  liveness.subscribe(() => { throw new Error("listener"); });
  liveness.subscribe(() => { heard++; });
  void liveness.check();
  time = 2_000; await liveness.check();
  assert.equal(calls, 0);
  assert.equal(heard, 1);
});
