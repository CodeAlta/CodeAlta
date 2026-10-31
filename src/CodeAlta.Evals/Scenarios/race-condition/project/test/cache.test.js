import { test } from "node:test";
import assert from "node:assert/strict";
import { createCache } from "../src/cache.js";

const tick = () => new Promise((resolve) => setImmediate(resolve));

test("a value is loaded once", async () => {
  let loads = 0;
  const cache = createCache(async (key) => { loads++; return key.toUpperCase(); });
  assert.equal(await cache.get("a"), "A");
  assert.equal(await cache.get("a"), "A");
  assert.equal(loads, 1);
});

test("after invalidate, a get loads the new value", async () => {
  let version = 1;
  const gates = [];
  const cache = createCache(async () => {
    const value = version;
    await new Promise((resolve) => gates.push(resolve));
    return value;
  });
  const first = cache.get("a");
  await tick();
  cache.invalidate("a");
  version = 2;
  const second = cache.get("a");
  await tick();
  gates[1]();
  assert.equal(await second, 2);
  gates[0]();
  assert.equal(await first, 1);
  await tick();
  assert.equal(await cache.get("a"), 2);
});
