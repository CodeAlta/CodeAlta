import assert from "node:assert/strict";
import test from "node:test";
import { createHostLiveness } from "./hostLiveness";

test("a host that misses three pings in a row is reported silent, and one answer clears it", async () => {
  let answer = true;
  const liveness = createHostLiveness(async () => { if (!answer) throw new Error("timeout"); });
  const changes: boolean[] = [];
  liveness.subscribe(() => changes.push(liveness.getSnapshot()));
  await liveness.check();
  assert.equal(liveness.getSnapshot(), false);

  answer = false;
  await liveness.check(); await liveness.check();
  assert.equal(liveness.getSnapshot(), false, "a slow or busy host is not silent yet");
  await liveness.check();
  assert.equal(liveness.getSnapshot(), true);
  await liveness.check();
  assert.deepEqual(changes, [true], "listeners hear about the change, not about every ping");

  answer = true;
  await liveness.check();
  assert.equal(liveness.getSnapshot(), false);
  assert.deepEqual(changes, [true, false]);
});

test("misses must be consecutive", async () => {
  const answers = [false, false, true, false, false];
  const liveness = createHostLiveness(async () => { if (!answers.shift()) throw new Error("timeout"); });
  for (let index = 0; index < 5; index++) await liveness.check();
  assert.equal(liveness.getSnapshot(), false);
});

test("a ping still in flight is not started again", async () => {
  let pings = 0;
  let fail!: (error: Error) => void;
  const liveness = createHostLiveness(() => { pings++; return new Promise<void>((_, reject) => { fail = reject; }); });
  const first = liveness.check();
  await liveness.check(); await liveness.check(); await liveness.check();
  assert.equal(pings, 1);
  assert.equal(liveness.getSnapshot(), false, "skipped checks are not misses");
  fail(new Error("timeout"));
  await first;
  assert.equal(liveness.getSnapshot(), false);
});

test("unsubscribed listeners are not called", async () => {
  const liveness = createHostLiveness(async () => { throw new Error("timeout"); });
  let calls = 0;
  const unsubscribe = liveness.subscribe(() => { calls++; });
  unsubscribe();
  for (let index = 0; index < 3; index++) await liveness.check();
  assert.equal(liveness.getSnapshot(), true);
  assert.equal(calls, 0);
});
