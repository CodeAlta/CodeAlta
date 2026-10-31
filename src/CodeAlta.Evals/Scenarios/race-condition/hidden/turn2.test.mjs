import { test } from "node:test";
import assert from "node:assert/strict";

const root = process.env.EVAL_PROJECT_URL;
const { createCache } = await import(new URL("src/cache.js", root));
const tick = () => new Promise((resolve) => setImmediate(resolve));
const unhandled = [];
process.on("unhandledRejection", (reason) => unhandled.push(reason));

function gated() {
  const gates = [];
  let version = 1;
  const cache = createCache(async () => {
    const value = version;
    await new Promise((resolve) => gates.push(resolve));
    return value;
  });
  return { cache, gates, set: (value) => { version = value; } };
}

test("a load that ends after invalidate does not bring its old value back", async () => {
  const { cache, gates, set } = gated();
  const first = cache.get("a");
  await tick();
  cache.invalidate("a");
  set(2);
  const second = cache.get("a");
  await tick();
  gates[0]();
  assert.equal(await first, 1);
  gates[1]();
  assert.equal(await second, 2);
  await tick();
  assert.equal(await cache.get("a"), 2);
  assert.equal(gates.length, 2, "no third load");
});

test("the old load ending last does not win either", async () => {
  const { cache, gates, set } = gated();
  const first = cache.get("a");
  await tick();
  cache.invalidate("a");
  set(2);
  const second = cache.get("a");
  await tick();
  gates[1]();
  await second;
  gates[0]();
  await first;
  await tick();
  assert.equal(await cache.get("a"), 2);
});

test("a failed load is not kept: the next get loads again", async () => {
  let calls = 0;
  const cache = createCache(async () => {
    calls++;
    if (calls === 1) throw new Error("down");
    return "up";
  });
  await assert.rejects(cache.get("a"), /down/);
  await tick();
  assert.equal(await cache.get("a"), "up");
  assert.equal(calls, 2);
});

test("the gets of a key during a load share it", async () => {
  let calls = 0;
  const cache = createCache(async () => { calls++; await tick(); return calls; });
  const [a, b] = await Promise.all([cache.get("k"), cache.get("k")]);
  assert.deepEqual([a, b, calls], [1, 1, 1]);
});

test("a value expires after ttlMs", async () => {
  let time = 0;
  let calls = 0;
  const cache = createCache(async () => ++calls, { ttlMs: 100, now: () => time });
  assert.equal(await cache.get("a"), 1);
  time = 50;
  assert.equal(await cache.get("a"), 1);
  time = 1000;
  assert.equal(await cache.get("a"), 2);
});

test("no rejection is left unhandled", async () => {
  const cache = createCache(async () => { throw new Error("boom"); });
  await assert.rejects(cache.get("x"));
  await tick();
  await tick();
  assert.deepEqual(unhandled, []);
});
