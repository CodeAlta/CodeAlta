import assert from "node:assert/strict";
import test from "node:test";
import { backoff, createLiveStream, silenceLimit, type EventSourceLike, type LinkState } from "./liveStream";

// An EventSource the test plays, and timers it runs by hand.
class FakeSource implements EventSourceLike {
  readyState = 0;
  closed = false;
  private listeners = new Map<string, ((event: MessageEvent) => void)[]>();
  addEventListener(type: string, listener: (event: MessageEvent) => void) { this.listeners.set(type, [...this.listeners.get(type) ?? [], listener]); }
  close() { this.closed = true; this.readyState = 2; }
  emit(type: string, data?: unknown) {
    if (type === "open") this.readyState = 1;
    for (const listener of this.listeners.get(type) ?? []) listener({ data: data === undefined ? undefined : JSON.stringify(data) } as MessageEvent);
  }
  fail(closed: boolean) { this.readyState = closed ? 2 : 0; this.emit("error"); }
}

function harness(check: () => Promise<"ok" | "signed-out" | "unreachable"> = async () => "ok") {
  const sources: FakeSource[] = [], states: LinkState[] = [], sessions: string[] = [];
  const timers = new Map<number, { callback: () => void; ms: number }>();
  let nextTimer = 0, reconnected = 0;
  const stream = createLiveStream({
    create: () => { const source = new FakeSource(); sources.push(source); return source; },
    onSession: live => sessions.push(`${live.id}:${live.live}`),
    onState: state => states.push(state),
    onReconnected: () => reconnected++,
    check,
    setTimer: (callback, ms) => { timers.set(++nextTimer, { callback, ms }); return nextTimer; },
    clearTimer: timer => { timers.delete(timer as number); },
  });
  // Runs the timers of a delay (or every one when none is named).
  const run = (ms?: number) => { for (const [id, timer] of [...timers]) if (ms === undefined || timer.ms === ms) { timers.delete(id); timer.callback(); } };
  return { stream, sources, states, sessions, timers, run, reconnected: () => reconnected };
}
const settle = () => new Promise(resolve => setImmediate(resolve));

test("the stream gives the session events and says it is live", () => {
  const h = harness();
  h.stream.start();
  h.sources[0].emit("open");
  h.sources[0].emit("session", { id: "a", live: "running" });
  h.sources[0].emit("session", "not a session");
  assert.deepEqual(h.sessions, ["a:running"]);
  assert.deepEqual(h.states, ["connecting", "live"]);
  assert.equal(h.reconnected(), 0, "the first open is no reconnection");
});

test("while the browser retries by itself the page waits; once it reopens, what was missed is read again", () => {
  const h = harness();
  h.stream.start();
  h.sources[0].emit("open");
  h.sources[0].fail(false);
  assert.equal(h.sources.length, 1, "a CONNECTING source is left to the browser");
  assert.equal(h.states.at(-1), "reconnecting");
  h.sources[0].emit("open");
  assert.equal(h.reconnected(), 1);
});

test("a stream the browser gave up is opened again after a check, with a backoff that grows", async () => {
  let reachable = false;
  const h = harness(async () => reachable ? "ok" : "unreachable");
  h.stream.start();
  h.sources[0].emit("open");
  h.sources[0].fail(true);
  await settle();
  assert.equal(h.states.at(-1), "offline");
  assert.equal([...h.timers.values()].find(t => t.callback.name === "connect")?.ms, backoff(1));
  h.run(backoff(1));
  assert.equal(h.sources.length, 2);
  h.sources[1].fail(true);
  await settle();
  assert.equal([...h.timers.values()].find(t => t.callback.name === "connect")?.ms, backoff(2));
  reachable = true;
  h.run(backoff(2));
  h.sources[2].emit("open");
  assert.equal(h.states.at(-1), "live");
  assert.equal(h.reconnected(), 1, "the page reads again after the stream came back");
  assert.deepEqual([backoff(1), backoff(2), backoff(3), backoff(10)], [1000, 2000, 4000, 30_000]);
});

test("a stream refused because the phone is signed out stops for good", async () => {
  const h = harness(async () => "signed-out");
  h.stream.start();
  h.sources[0].fail(true);
  await settle();
  assert.equal(h.timers.size, 0);
  h.stream.wake();
  assert.equal(h.sources.length, 1, "a stopped stream does not wake");
});

test("waking opens the stream at once when it is not open, and leaves an open one alone", async () => {
  const h = harness(async () => "unreachable");
  h.stream.start();
  h.sources[0].emit("open");
  h.stream.wake();
  assert.equal(h.sources.length, 1);
  h.sources[0].fail(true);
  await settle();
  h.stream.wake();
  assert.equal(h.sources.length, 2, "no wait for the backoff");
  h.sources[1].emit("open");
  assert.equal(h.reconnected(), 1);
});

test("a stream silent for three pings is replaced", () => {
  const h = harness();
  h.stream.start();
  h.sources[0].emit("open");
  h.sources[0].emit("ping");
  h.run(silenceLimit);
  assert.equal(h.sources[0].closed, true);
  assert.equal(h.sources.length, 2);
  h.sources[1].emit("open");
  assert.equal(h.reconnected(), 1);
});
