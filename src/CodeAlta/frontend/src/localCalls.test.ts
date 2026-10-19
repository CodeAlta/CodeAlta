import assert from "node:assert/strict";
import test from "node:test";
import type { NeoRpcCallOptions } from "@neoastra/client";
import { createSlowCallWatch, installLocalCallPolicy, refusedBeforeRunning, sendUntilAdmitted, withoutTimeout } from "./localCalls";

function fakeTimers() {
  let now = 0;
  const pending = new Map<number, { at: number; callback: () => void }>();
  let next = 0;
  return {
    timers: {
      now: () => now,
      setTimeout: (callback: () => void, milliseconds: number) => { pending.set(++next, { at: now + milliseconds, callback }); return next; },
      clearTimeout: (handle: unknown) => { pending.delete(handle as number); },
    },
    advance(milliseconds: number) {
      now += milliseconds;
      for (const [id, timer] of [...pending]) if (timer.at <= now) { pending.delete(id); timer.callback(); }
    },
  };
}

test("a call to the host never carries a timeout, and keeps its signal and contract", () => {
  const signal = new AbortController().signal;
  assert.deepEqual(withoutTimeout({ signal, timeoutMilliseconds: 8_000, contractHash: "h" }), { signal, contractHash: "h" });
  assert.equal(withoutTimeout(undefined), undefined);
  const options = { contractHash: "h" };
  assert.equal(withoutTimeout(options), options);
});

test("a slow call is reported while it waits and when it answers, never given up", () => {
  const clock = fakeTimers();
  const reports: string[] = [];
  const watch = createSlowCallWatch((level, message) => reports.push(`${level}: ${message}`), clock.timers, 10_000);
  const quick = watch.start("workspace.snapshot");
  clock.advance(9_000);
  quick();
  const slow = watch.start("sessionRuntimeState.observe");
  clock.advance(10_000);
  assert.equal(watch.slow(), 1);
  clock.advance(5_500);
  slow();
  slow();
  assert.equal(watch.slow(), 0);
  assert.deepEqual(reports, [
    "warning: sessionRuntimeState.observe has not answered for 10 s; the call goes on.",
    "information: sessionRuntimeState.observe answered after 15.5 s.",
  ]);
  // The report of the page to its host is not watched: it would report on itself.
  watch.start("boot.report");
  clock.advance(60_000);
  assert.equal(reports.length, 2);
});

test("a report that throws never breaks a call", () => {
  const clock = fakeTimers();
  const watch = createSlowCallWatch(() => { throw new Error("no log"); }, clock.timers, 10);
  const end = watch.start("x");
  clock.advance(20);
  end();
  assert.equal(watch.slow(), 0);
});

test("the policy removes the timeout of every kind of call and watches it until it settles", async () => {
  const seen: Array<NeoRpcCallOptions | undefined> = [];
  class Client {
    invoke(_command: string, _args: unknown, options?: NeoRpcCallOptions) { seen.push(options); return Promise.resolve("ok"); }
    invokeChannel(_command: string, _args: unknown, options?: NeoRpcCallOptions) { seen.push(options); return Promise.reject(new Error("closed")); }
    subscribe(_event: string, _handler: (value: unknown) => void, options?: NeoRpcCallOptions) { seen.push(options); return Promise.resolve(async () => {}); }
  }
  const started: string[] = [];
  let ended = 0;
  installLocalCallPolicy({ start: name => { started.push(name); return () => { ended++; }; }, slow: () => 0 }, Client.prototype);
  installLocalCallPolicy({ start: () => assert.fail("installed twice"), slow: () => 0 }, Client.prototype);
  const client = new Client();
  assert.equal(await client.invoke("boot.status", {}, { timeoutMilliseconds: 5_000, contractHash: "h" }), "ok");
  await assert.rejects(client.invokeChannel("terminals.watch", {}, { timeoutMilliseconds: 30_000 }), /closed/);
  await client.subscribe("alta.events", () => {}, { timeoutMilliseconds: 8_000 });
  assert.deepEqual(seen, [{ contractHash: "h" }, {}, {}]);
  assert.deepEqual(started, ["boot.status", "terminals.watch", "alta.events (subscription)"]);
  assert.equal(ended, 3);
});

test("a call a limit of the host refused before running it is sent again until it is admitted", async () => {
  const refusal = Object.assign(new Error("An RPC concurrency limit is exhausted."), { code: "too_many_requests" });
  let sends = 0;
  let told = 0;
  const waits: number[] = [];
  const value = await sendUntilAdmitted(async () => { if (++sends < 6) throw refusal; return "ok"; }, undefined, () => { told++; },
    async milliseconds => { waits.push(milliseconds); });
  assert.equal(value, "ok");
  assert.equal(sends, 6);
  assert.equal(told, 1, "the refusal is reported once per call");
  assert.deepEqual(waits, [50, 100, 200, 400, 800]);
  assert.equal(refusedBeforeRunning(refusal), true);
  assert.equal(refusedBeforeRunning(Object.assign(new Error("x"), { code: "internal_error" })), false);
  assert.equal(refusedBeforeRunning(null), false);
});

test("any other failure, and an aborted call, end at once", async () => {
  await assert.rejects(sendUntilAdmitted(async () => { throw Object.assign(new Error("boom"), { code: "internal_error" }); }, undefined, () => {}), /boom/);
  const controller = new AbortController();
  let sends = 0;
  const refused = Object.assign(new Error("limit"), { code: "too_many_requests" });
  let waiting!: () => void;
  const entered = new Promise<void>(resolve => { waiting = resolve; });
  const call = sendUntilAdmitted(async () => { sends++; throw refused; }, controller.signal, () => {},
    (_, signal) => new Promise((_resolve, reject) => { signal?.addEventListener("abort", () => reject(new Error("aborted")), { once: true }); waiting(); }));
  await entered;
  controller.abort();
  await assert.rejects(call, /aborted/);
  assert.equal(sends, 1);
});
