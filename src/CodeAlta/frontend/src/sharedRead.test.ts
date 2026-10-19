import assert from "node:assert/strict";
import test from "node:test";
import { createSharedRead } from "./sharedRead";

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: Error) => void;
  const promise = new Promise<T>((done, fail) => { resolve = done; reject = fail; });
  return { promise, resolve, reject };
}

test("readers of the same key share one waiting read, and a fresh answer is given again", async () => {
  let time = 0;
  const calls: string[] = [];
  let pending = deferred<string>();
  const read = createSharedRead((request: { session: string }) => { calls.push(request.session); return pending.promise; }, request => request.session, 500, () => time);
  const first = read({ session: "a" });
  const second = read({ session: "a" });
  const other = read({ session: "b" });
  assert.deepEqual(calls, ["a", "b"]);
  pending.resolve("page");
  assert.equal(await first, "page");
  assert.equal(await second, "page");
  assert.equal(await other, "page");
  time = 400;
  assert.equal(await read({ session: "a" }), "page");
  assert.deepEqual(calls, ["a", "b"], "an answer younger than the fresh time is given again");
  pending = deferred<string>();
  time = 500;
  const later = read({ session: "a" });
  assert.deepEqual(calls, ["a", "b", "a"]);
  pending.resolve("newer");
  assert.equal(await later, "newer");
});

test("a failed read is not given again, and one caller's abort stops only its own wait", async () => {
  let calls = 0;
  let pending = deferred<string>();
  const read = createSharedRead(() => { calls++; return pending.promise; }, () => "key", 10_000);
  const failed = read({});
  pending.reject(new Error("internal_error"));
  await assert.rejects(failed, /internal_error/);
  pending = deferred<string>();
  const controller = new AbortController();
  const leaving = read({}, { signal: controller.signal });
  const staying = read({});
  assert.equal(calls, 2);
  controller.abort();
  await assert.rejects(leaving);
  pending.resolve("page");
  assert.equal(await staying, "page");
  await assert.rejects(read({}, { signal: controller.signal }), "an aborted signal ends at once");
});

test("a forgotten key is read again, by the next reader only", async () => {
  let calls = 0;
  let pending = deferred<string>();
  const read = createSharedRead(() => { calls++; return pending.promise; }, (request: { session: string }) => request.session, 10_000);
  const before = read({ session: "a" });
  read.forget({ session: "a" });
  const older = pending;
  pending = deferred<string>();
  const after = read({ session: "a" });
  assert.equal(calls, 2, "a read that waited when the key was forgotten is not joined");
  older.resolve("before the change");
  pending.resolve("after the change");
  assert.equal(await before, "before the change");
  assert.equal(await after, "after the change");
  assert.equal(await read({ session: "a" }), "after the change");
  assert.equal(calls, 2);
});
