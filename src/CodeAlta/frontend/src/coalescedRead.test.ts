import assert from "node:assert/strict";
import test from "node:test";
import { createCoalescedRead } from "./coalescedRead";

const settle = () => new Promise(resolve => setTimeout(resolve, 0));

test("a read asked for while a move blocks it runs once the move ends, and only then", async () => {
  let moving = true, reads = 0;
  const reader = createCoalescedRead(() => moving, async () => { reads++; });
  reader.run();
  reader.run();
  await settle();
  assert.equal(reads, 0, "Nothing is read while the move runs.");
  moving = false;
  reader.resume();
  await settle();
  assert.equal(reads, 1, "The reads asked for meanwhile are one read.");
  reader.resume();
  await settle();
  assert.equal(reads, 1, "Nothing was asked for since: nothing more is read.");
  reader.resume(true);
  await settle();
  assert.equal(reads, 2, "A move whose own read failed asks for one.");
});

test("a read asked for while one runs runs once more after it, and a failed read is no stop", async () => {
  let release!: () => void;
  let reads = 0;
  const reader = createCoalescedRead(() => false, () => { reads++; return reads === 1 ? new Promise<void>(resolve => { release = resolve; }) : Promise.reject(Error("lost")); });
  reader.run();
  reader.run();
  reader.run();
  assert.equal(reads, 1);
  release();
  await settle();
  assert.equal(reads, 2, "A run that ended during the read is read once more.");
  reader.run();
  await settle();
  assert.equal(reads, 3, "After a failed read, the next one runs.");
});

test("a read that ends while a move began is read again when the move ends", async () => {
  let moving = false, reads = 0;
  const reader = createCoalescedRead(() => moving, async () => { reads++; if (moving) reader.defer(); });
  moving = true;
  // A read already under way when the move begins: what it found is not shown, and it is read again after.
  reader.run();
  await settle();
  assert.equal(reads, 0);
  moving = false;
  reader.run();
  await settle();
  assert.equal(reads, 1);
  moving = true;
  reader.defer();
  moving = false;
  reader.resume();
  await settle();
  assert.equal(reads, 2);
});
