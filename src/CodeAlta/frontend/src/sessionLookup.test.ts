import assert from "node:assert/strict";
import test from "node:test";
import { findWithPatience } from "./sessionLookup";

const noWait = async () => { };

test("A session listed a moment after its run started is found, without waiting longer", async () => {
  let attempts = 0;
  const waits: number[] = [];
  const found = await findWithPatience(async () => ++attempts === 3, () => true, [500, 1000, 1500], async milliseconds => { waits.push(milliseconds); });
  assert.equal(found, true);
  assert.equal(attempts, 3);
  assert.deepEqual(waits, [500, 1000], "it stops looking once found");
});

test("A session never listed is not found once every delay passed, and a caller gone stops the search", async () => {
  let attempts = 0;
  assert.equal(await findWithPatience(async () => { attempts++; return false; }, () => true, [1, 2], noWait), false);
  assert.equal(attempts, 3, "the first look, then one after each delay");

  attempts = 0;
  assert.equal(await findWithPatience(async () => { attempts++; return false; }, () => false, [1, 2], noWait), false);
  assert.equal(attempts, 1, "a closed window looks no further");
});

test("A session listed already is found at once", async () => {
  let waited = false;
  assert.equal(await findWithPatience(async () => true, () => true, [1], async () => { waited = true; }), true);
  assert.equal(waited, false);
});
