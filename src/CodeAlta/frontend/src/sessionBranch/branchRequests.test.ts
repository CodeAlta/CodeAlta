import assert from "node:assert/strict";
import test from "node:test";
import { createBranchRequests, keepRemovedTurnsKey, readKeepRemovedTurns, saveKeepRemovedTurns } from "./branchRequests";

test("a request is the one of its session, the same object until it is closed or replaced", () => {
  const requests = createBranchRequests();
  let published = 0;
  const unsubscribe = requests.subscribe(() => { published++; });
  assert.equal(requests.forSession("a"), null);
  requests.open("a", "rewind", "1200");
  assert.equal(published, 1);
  const first = requests.forSession("a");
  assert.deepEqual(first, { sessionId: "a", mode: "rewind", promptOffset: "1200" });
  // A pane that follows its request is given the same object: it has nothing to render again.
  assert.equal(requests.forSession("a"), first);
  assert.equal(requests.forSession("b"), null);
  // One window at a time: a request for another session takes the place of the first.
  requests.open("b", "fork");
  assert.equal(requests.forSession("a"), null);
  assert.deepEqual(requests.forSession("b"), { sessionId: "b", mode: "fork", promptOffset: null });
  // The pane of the first session closes its own request: the newer one stays.
  requests.close(first);
  assert.equal(requests.forSession("b")?.mode, "fork");
  assert.equal(published, 2);
  requests.close(requests.forSession("b"));
  assert.equal(requests.forSession("b"), null);
  assert.equal(published, 3);
  requests.close(null);
  assert.equal(published, 3);
  // A change that was made closes the window of its session, whoever shows it now, and no other.
  requests.open("a", "rewind");
  requests.done("b");
  assert.equal(requests.forSession("a")?.mode, "rewind");
  requests.done("a");
  assert.equal(requests.forSession("a"), null);
  assert.equal(published, 5);
  unsubscribe();
  requests.open("a", "fork");
  assert.equal(published, 5);
});

test("a rewind keeps the removed turns unless the user chose otherwise, and the choice is remembered", () => {
  const stored = new Map<string, string>();
  assert.equal(readKeepRemovedTurns(key => stored.get(key) ?? null), true);
  saveKeepRemovedTurns((key, value) => stored.set(key, value), false);
  assert.equal(stored.get(keepRemovedTurnsKey), "false");
  assert.equal(readKeepRemovedTurns(key => stored.get(key) ?? null), false);
  saveKeepRemovedTurns((key, value) => stored.set(key, value), true);
  assert.equal(readKeepRemovedTurns(key => stored.get(key) ?? null), true);
  // Storage that cannot be read or written never turns the safe choice off.
  assert.equal(readKeepRemovedTurns(() => { throw new Error("denied"); }), true);
  assert.doesNotThrow(() => saveKeepRemovedTurns(() => { throw new Error("denied"); }, false));
});
