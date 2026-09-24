import assert from "node:assert/strict";
import test from "node:test";
import { createReminderActions } from "./reminderActions";
import type { ReminderMutationResponse } from "#neoastra";

test("original pending create survives session switches and uncertain response blocks only exact epoch/session", async () => {
  const calls: unknown[] = [];
  let resolve!: (reply: ReminderMutationResponse) => void;
  const actions = createReminderActions(request => { calls.push(request); return new Promise(done => { resolve = done; }); },
    () => { throw new Error("delete should not be invoked"); });
  const one = { epoch: "e1", sessionId: "one" };
  const two = { epoch: "e1", sessionId: "two" };
  const request = { expectedEpoch: "e1", sessionId: "one", content: "original", delaySeconds: 30, repeatCount: 2 };
  const original = actions.submit(one, request, "create");
  assert.equal(actions.get(one)?.pending, true);
  assert.deepEqual(actions.get(one)?.request, request);
  assert.equal(actions.get(two), undefined);
  assert.equal(await actions.submit(one, request, "create"), false);
  resolve({ status: "ok", epoch: "e1", sessionId: "two", reminderId: "reminder-elsewhere" });
  assert.equal(await original, false);
  assert.equal(actions.get(one)?.hold, true);
  assert.deepEqual(actions.get(one)?.request, request);
  assert.equal(await actions.submit(one, request, "create"), false);
  assert.equal(calls.length, 1);
  assert.equal(actions.get(two), undefined);
  assert.equal(actions.get({ epoch: "e2", sessionId: "one" }), undefined);
});

test("delete reply must return the exact confirmed reminder identity", async () => {
  const calls: unknown[] = [];
  const actions = createReminderActions(() => { throw new Error("create should not be invoked"); }, request => {
    calls.push(request);
    return Promise.resolve({ status: "ok", epoch: "e1", sessionId: "one", reminderId: "different" });
  });
  const target = { epoch: "e1", sessionId: "one" };
  const request = { expectedEpoch: "e1", sessionId: "one", reminderId: "reminder-one", confirmation: "reminder-one" };
  assert.equal(await actions.submit(target, request, "delete"), false);
  assert.equal(actions.get(target)?.hold, true);
  assert.deepEqual(actions.get(target)?.request, request);
  assert.equal(calls.length, 1);
});
