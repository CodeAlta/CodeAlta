import assert from "node:assert/strict";
import test from "node:test";
import { AltaError } from "../pluginScript/alta";
import { createCanvasRpc, createCarrierConnection, toAltaError, type CanvasRpcCarrier, type CanvasRpcListener } from "./canvasRpc";

const wait = (milliseconds = 5) => new Promise(resolve => setTimeout(resolve, milliseconds));
const until = async (condition: () => boolean, what: string) => {
  for (let index = 0; index < 400; index++) { if (condition()) return; await wait(5); }
  assert.fail(`never happened: ${what}`);
};

/**
 * A host the test plays: it opens connections, reads the frames the page gives it the way the NeoAstra host does (invoke, cancel, subscribe,
 * channel acknowledgements), and answers on the listener the page registered.
 */
function host(options: { maximumFrameBytes?: number; credits?: number } = {}) {
  const batches: string[][] = [];
  const closed: string[] = [];
  const opened: string[] = [];
  const state = { openStatus: "ok", sendStatus: "ok", listener: null as CanvasRpcListener | null, connections: 0, acks: [] as number[], cancels: [] as string[], unsubscribes: [] as string[],
    streams: new Map<string, { channel: string; sent: number; acknowledged: number; total: number }>(), subscriptions: new Map<string, string>(), eventSequence: 0, pushed: 0 };
  const credits = options.credits ?? 2;
  let connection = "";

  const reply = (...frames: unknown[]) => queueMicrotask(() => state.listener?.frames(connection, frames.map(frame => JSON.stringify(frame))));
  const pump = (stream: { channel: string; sent: number; acknowledged: number; total: number }) => {
    while (stream.sent < stream.total && stream.sent - stream.acknowledged < credits) {
      stream.sent++;
      reply({ neoastra: 1, kind: "channel_item", channel: stream.channel, sequence: stream.sent, value: stream.sent });
    }

    if (stream.sent === stream.total && stream.acknowledged === stream.total) reply({ neoastra: 1, kind: "channel_complete", channel: stream.channel });
  };

  const carrier: CanvasRpcCarrier = {
    async open(instanceId) {
      opened.push(instanceId);
      if (state.openStatus !== "ok") return { status: state.openStatus, connection: null, maximumFrameBytes: 0 };
      connection = `c${++state.connections}`;
      return { status: "ok", connection, maximumFrameBytes: options.maximumFrameBytes ?? 1 << 20 };
    },
    async send(_instanceId, id, frames) {
      batches.push([...frames]);
      if (state.sendStatus !== "ok") return state.sendStatus;
      connection = id;
      for (const text of frames) {
        const frame = JSON.parse(text) as Record<string, unknown>;
        if (frame.kind === "invoke") {
          const args = frame.args as { count?: number } | null;
          if (frame.command === "echo") reply({ neoastra: 1, kind: "result", id: frame.id, ok: true, value: { echo: frame.args } });
          else if (frame.command === "fail") reply({ neoastra: 1, kind: "result", id: frame.id, ok: false, error: { code: "not_found", message: "No such board.", retryable: true } });
          else if (frame.command === "slow") { /* never answers: the test cancels or ends the connection */ }
          else if (frame.command === "count") {
            const stream = { channel: `ch${state.streams.size + 1}`, sent: 0, acknowledged: 0, total: args?.count ?? 5 };
            state.streams.set(stream.channel, stream);
            reply({ neoastra: 1, kind: "result", id: frame.id, ok: true, value: { channel: stream.channel } });
            queueMicrotask(() => pump(stream));
          } else reply({ neoastra: 1, kind: "result", id: frame.id, ok: false, error: { code: "command_not_found", message: "The requested command is not registered.", retryable: false } });
        } else if (frame.kind === "cancel") state.cancels.push(String(frame.id));
        else if (frame.kind === "channel_ack") {
          state.acks.push(Number(frame.sequence));
          const stream = state.streams.get(String(frame.channel));
          if (stream) { stream.acknowledged = Math.max(stream.acknowledged, Number(frame.sequence)); pump(stream); }
        } else if (frame.kind === "subscribe") {
          state.subscriptions.set(String(frame.id), String(frame.event));
          reply({ neoastra: 1, kind: "subscribed", id: frame.id });
        } else if (frame.kind === "unsubscribe") state.unsubscribes.push(String(frame.id));
      }

      return "ok";
    },
    async close(_instanceId, id) { closed.push(id); },
    listen(_instanceId, listener) { state.listener = listener; return () => { if (state.listener === listener) state.listener = null; }; },
  };
  return {
    carrier, batches, closed, opened, state,
    event(name: string, value: unknown) {
      for (const id of state.subscriptions.keys()) reply({ neoastra: 1, kind: "event", subscription: id, sequence: ++state.eventSequence, value: { name, value } });
    },
    endConnection(why = "closed") { state.listener?.closed(connection, why); },
    get frames() { return batches.flat().map(text => JSON.parse(text) as Record<string, unknown>); },
  };
}

const controllerOf = (fake: ReturnType<typeof host>, extra: { minimumGapMilliseconds?: number } = {}) => createCanvasRpc({ carrier: fake.carrier, instanceId: "i1", minimumGapMilliseconds: 0, ...extra });

test("nothing is opened until a script calls; the first call connects, and the next ones reuse the connection", async () => {
  const fake = host();
  const { rpc } = controllerOf(fake);
  assert.deepEqual(fake.opened, []);

  assert.deepEqual(await rpc.invoke("echo", { a: 1 }), { echo: { a: 1 } });
  assert.deepEqual(await rpc.invoke("echo"), { echo: null }, "a call without input is sent as null: the plugin reads {}");

  assert.deepEqual(fake.opened, ["i1"], "one session for the tab");
  assert.equal(rpc.generation.value, 0);
});

test("calls made together reach the host together, in order, with nothing sent twice", async () => {
  const fake = host();
  const { rpc } = controllerOf(fake);
  await rpc.invoke("echo", 0);
  fake.batches.length = 0;

  const results = await Promise.all([rpc.invoke("echo", 1), rpc.invoke("echo", 2), rpc.invoke("echo", 3)]);

  assert.deepEqual(results, [{ echo: 1 }, { echo: 2 }, { echo: 3 }]);
  assert.equal(fake.batches.length, 1, "one call to the host carried the three");
  assert.deepEqual(fake.frames.map(frame => (frame.args as number)), [1, 2, 3]);
});

test("an error of the plugin is an AltaError with its code, its message and whether to try again", async () => {
  const fake = host();
  const { rpc } = controllerOf(fake);

  await assert.rejects(rpc.invoke("fail"), (error: unknown) => error instanceof AltaError && error.code === "not_found" && error.message === "No such board." && error.retryable === true);
  await assert.rejects(rpc.invoke("unknown"), (error: unknown) => error instanceof AltaError && error.code === "command_not_found" && !error.retryable);
  assert.equal(toAltaError(new DOMException("x", "AbortError")).code, "operation_canceled");
  assert.equal(toAltaError("odd").code, "rpc_failed");
});

test("a call that is too large to carry is answered by the page, and the connection goes on", async () => {
  const fake = host({ maximumFrameBytes: 200 });
  const { rpc } = controllerOf(fake);
  await rpc.invoke("echo", 1);
  const before = fake.frames.length;

  await assert.rejects(rpc.invoke("echo", "x".repeat(500)), (error: unknown) => error instanceof AltaError && error.code === "payload_too_large");

  assert.equal(fake.frames.length, before, "it never left the page");
  assert.deepEqual(await rpc.invoke("echo", 2), { echo: 2 });
});

test("cancelling a call sends the cancel to the host and rejects with operation_canceled", async () => {
  const fake = host();
  const { rpc } = controllerOf(fake);
  const controller = new AbortController();

  const call = rpc.invoke("slow", {}, { signal: controller.signal });
  await until(() => fake.frames.some(frame => frame.command === "slow"), "the call reached the host");
  controller.abort();

  await assert.rejects(call, (error: unknown) => error instanceof AltaError && error.code === "operation_canceled");
  await until(() => fake.state.cancels.length === 1, "the cancel reached the host");
  await assert.rejects(rpc.invoke("echo", 1, { signal: controller.signal }), (error: unknown) => error instanceof AltaError && error.code === "operation_canceled", "a signal that is aborted already sends nothing");
});

test("a connection the host ends fails what is pending, and the next call connects again", async () => {
  const fake = host();
  const { rpc } = controllerOf(fake);
  await rpc.invoke("echo", 1);
  const generations: number[] = [];
  rpc.generation.subscribe(value => generations.push(value));

  const pending = rpc.invoke("slow", {});
  await until(() => fake.frames.some(frame => frame.command === "slow"), "the call reached the host");
  fake.endConnection("replaced");

  await assert.rejects(pending, (error: unknown) => error instanceof AltaError && error.code === "connection_closed" && error.retryable);
  assert.deepEqual(await rpc.invoke("echo", 2), { echo: 2 });
  assert.deepEqual(fake.opened, ["i1", "i1"], "a second session");
  assert.equal(rpc.generation.value, 1, "what was read before may be stale");
  assert.deepEqual(generations, [1]);
  assert.deepEqual(fake.closed, [], "the host ended it: nothing to tell it");
});

test("a host that refuses the frames ends the connection, and a host that cannot be reached is rpc_unavailable", async () => {
  const fake = host();
  const { rpc } = controllerOf(fake);
  await rpc.invoke("echo", 1);

  fake.state.sendStatus = "closed";
  await assert.rejects(rpc.invoke("echo", 2), (error: unknown) => error instanceof AltaError && error.code === "connection_closed" && error.retryable);

  fake.state.sendStatus = "ok";
  fake.state.openStatus = "unavailable";
  await assert.rejects(rpc.invoke("echo", 3), (error: unknown) => error instanceof AltaError && error.code === "rpc_unavailable" && !error.retryable, "an instance without calls has none later either");
  fake.state.openStatus = "unknown";
  await assert.rejects(rpc.invoke("echo", 4), (error: unknown) => error instanceof AltaError && error.code === "connection_closed" && error.retryable);
  // The host could not be asked, or no page watches yet: nothing is wrong with the canvas, and the same call can work a moment later.
  for (const status of ["unreachable", "not_watching"]) {
    fake.state.openStatus = status;
    await assert.rejects(rpc.invoke("echo", 4), (error: unknown) => error instanceof AltaError && error.code === "connection_closed" && error.retryable, status);
  }

  fake.state.openStatus = "stale_epoch";
  await assert.rejects(rpc.invoke("echo", 4), (error: unknown) => error instanceof AltaError && error.code === "rpc_unavailable" && !error.retryable);
  fake.state.openStatus = "ok";
  assert.deepEqual(await rpc.invoke("echo", 5), { echo: 5 });
});

test("a script is told when its connection opens and when it ends, since its subscriptions end with it", async () => {
  const fake = host();
  const controller = controllerOf(fake);
  const { rpc } = controller;
  const heard: boolean[] = [];
  const stop = rpc.connected!.subscribe(value => heard.push(value));
  assert.equal(rpc.connected!.value, false, "nothing is opened until a script calls");

  await rpc.subscribe("board.changed", () => { });
  assert.equal(rpc.connected!.value, true);
  // The host ends the connection while the script only listens: no call of its own would tell it.
  fake.endConnection("replaced");
  assert.equal(rpc.connected!.value, false);
  await rpc.invoke("echo", 1);
  assert.deepEqual(heard, [true, false, true]);
  // The page loses the host, then the tab goes away.
  fake.state.listener!.closed(null, "watch_ended");
  await rpc.invoke("echo", 2);
  controller.detach();
  assert.deepEqual(heard, [true, false, true, false, true, false]);
  stop();
  controller.attach();
  await rpc.invoke("echo", 3);
  assert.equal(heard.length, 6, "a listener that stopped hears nothing");
});

test("the signal of one listener ends its own listening, not the one subscription the others share", async () => {
  const fake = host();
  const { rpc } = controllerOf(fake);
  const first: unknown[] = [], second: unknown[] = [];
  const controller = new AbortController();

  await rpc.subscribe("board.changed", value => first.push(value), { signal: controller.signal });
  await rpc.subscribe("board.changed", value => second.push(value));
  fake.event("board.changed", 1);
  await until(() => first.length === 1 && second.length === 1, "both heard the first event");

  controller.abort();
  fake.event("board.changed", 2);
  await until(() => second.length === 2, "the other listener still hears");
  assert.deepEqual(first, [1], "the listener whose signal aborted hears no more");
  assert.deepEqual(fake.state.unsubscribes, [], "the subscription the others share stays");
  await assert.rejects(rpc.subscribe("board.changed", () => { }, { signal: controller.signal }), (error: unknown) => error instanceof AltaError && error.code === "operation_canceled");
});

test("a listener whose signal aborts while it joins the subscription hears nothing, whenever the abort falls", async () => {
  const fake = host();
  const { rpc } = controllerOf(fake);
  // One listener keeps the subscription: the others join one that is made already, and are told so a few steps later.
  await rpc.subscribe("board", () => { });
  const outcomes = new Set<string>();
  for (let steps = 0; steps < 12; steps++) {
    const controller = new AbortController();
    const heard: unknown[] = [];
    const joining = rpc.subscribe("board", value => heard.push(value), { signal: controller.signal });
    // The signal aborts after that many steps of the page: before the answer, after it, and in between.
    let step = Promise.resolve();
    for (let index = 0; index < steps; index++) step = step.then(() => { });
    void step.then(() => controller.abort());
    outcomes.add(await joining.then(() => "listening", (error: unknown) => (error as AltaError).code));
    await wait();
    assert.ok(controller.signal.aborted);

    fake.event("board", steps);
    await wait(10);
    assert.deepEqual(heard, [], `a listener whose signal aborted after ${steps} steps is told nothing`);
  }

  assert.deepEqual([...outcomes].sort(), ["listening", "operation_canceled"], "the signal fell on both sides of the answer");
});

test("frames of another connection are dropped, and a stream gives its items in order and ends", async () => {
  const fake = host();
  const { rpc } = controllerOf(fake);

  const stream = await rpc.stream("count", { count: 5 });
  fake.state.listener!.frames("another", [JSON.stringify({ neoastra: 1, kind: "channel_item", channel: "ch1", sequence: 1, value: "stray" })]);

  const seen: unknown[] = [];
  for await (const item of stream) seen.push(item);
  assert.deepEqual(seen, [1, 2, 3, 4, 5]);
});

test("the acknowledgements of a stream are merged, so a long stream costs few calls to the host", async () => {
  const fake = host({ credits: 40 });
  const { rpc } = controllerOf(fake, { minimumGapMilliseconds: 20 });

  const stream = await rpc.stream("count", { count: 200 });
  let seen = 0;
  for await (const _ of stream) seen++;

  assert.equal(seen, 200);
  assert.ok(fake.state.acks.length < 100, `${fake.state.acks.length} acknowledgements for 200 items`);
  assert.ok(fake.batches.length < 60, `${fake.batches.length} calls to the host`);
});

test("a hidden tab holds the acknowledgements back, so its stream pauses on the host, and a shown tab lets it go on", async () => {
  const fake = host({ credits: 2 });
  const controller = controllerOf(fake);
  const stream = (await controller.rpc.stream("count", { count: 50 }))[Symbol.asyncIterator]();
  assert.deepEqual((await stream.next()).value, 1);
  const hosted = fake.state.streams.get("ch1")!;

  controller.setVisible(false);
  await wait(40);
  const sentWhenHidden = hosted.sent;
  const acksWhenHidden = fake.state.acks.length;
  // The script reads what reached the page; the host is not told, so it sends no more than its credits.
  const taken: number[] = [];
  let pending = stream.next();
  while (true) {
    const next = await Promise.race([pending, wait(40).then(() => null)]);
    if (next === null) break;
    taken.push(Number(next.value));
    pending = stream.next();
  }

  await wait(60);
  assert.equal(hosted.sent, sentWhenHidden, "the host sent nothing while the tab was hidden");
  assert.equal(fake.state.acks.length, acksWhenHidden, "no acknowledgement while hidden");
  assert.ok(hosted.sent - hosted.acknowledged >= 2, "the host waits for credits");

  controller.setVisible(true);
  await until(() => hosted.sent > sentWhenHidden, "the stream went on once shown");
  const next = await pending;
  assert.deepEqual([...taken, Number(next.value)], Array.from({ length: taken.length + 1 }, (_, index) => index + 2), "in order, nothing lost");
  await stream.return?.();
});

test("stopping the iteration ends the stream, and a stream the connection loses fails with a retryable error", async () => {
  const fake = host({ credits: 2 });
  const { rpc } = controllerOf(fake);

  const first = (await rpc.stream("count", { count: 100 }))[Symbol.asyncIterator]();
  await first.next();
  await first.return?.();
  await until(() => fake.frames.some(frame => frame.kind === "channel_close"), "the host was told to close the channel");

  const second = (await rpc.stream("count", { count: 100 }))[Symbol.asyncIterator]();
  await second.next();
  fake.endConnection("failed");
  await assert.rejects(async () => { for (let index = 0; index < 100; index++) await second.next(); }, (error: unknown) => error instanceof AltaError && error.code === "connection_closed" && error.retryable);
});

test("events are dispatched by name on one subscription, and the last unsubscribe frees it", async () => {
  const fake = host();
  const { rpc } = controllerOf(fake);
  const changed: unknown[] = [];
  const other: unknown[] = [];

  const stopChanged = await rpc.subscribe("board.changed", value => changed.push(value));
  const stopOther = await rpc.subscribe("board.other", value => other.push(value));
  fake.event("board.changed", { n: 1 });
  fake.event("board.other", "x");
  fake.event("board.unheard", 3);
  await until(() => changed.length === 1 && other.length === 1, "the events arrived");

  assert.deepEqual([changed, other], [[{ n: 1 }], ["x"]]);
  assert.equal(fake.state.subscriptions.size, 1, "one subscription for all the names");
  stopChanged();
  stopChanged();
  fake.event("board.changed", { n: 2 });
  await wait(20);
  assert.equal(changed.length, 1);
  stopOther();
  await until(() => fake.state.unsubscribes.length === 1, "the subscription was freed");
  await assert.rejects(rpc.subscribe("x", "no" as never), (error: unknown) => error instanceof AltaError && error.code === "invalid_request");
});

test("detaching ends the connection and refuses calls; attaching again serves them (React runs effects twice)", async () => {
  const fake = host();
  const controller = controllerOf(fake);
  controller.attach();
  controller.detach();
  controller.attach();
  assert.deepEqual(fake.opened, [], "a double run opens nothing");
  await controller.rpc.invoke("echo", 1);
  const pending = controller.rpc.invoke("slow", {});
  await until(() => fake.frames.some(frame => frame.command === "slow"), "the call reached the host");

  controller.detach();

  await assert.rejects(pending, (error: unknown) => error instanceof AltaError && error.code === "connection_closed");
  await until(() => fake.closed.length === 1, "the host was told to end the session");
  await assert.rejects(controller.rpc.invoke("echo", 2), (error: unknown) => error instanceof AltaError && error.code === "connection_closed");
  assert.equal(fake.state.listener, null);
  controller.attach();
  assert.deepEqual(await controller.rpc.invoke("echo", 3), { echo: 3 });
});

test("a call made while the connection is being opened waits for it, and a call cancelled meanwhile does not", async () => {
  const fake = host();
  const { rpc } = controllerOf(fake);
  const controller = new AbortController();

  const cancelled = rpc.invoke("echo", 1, { signal: controller.signal });
  const waiting = rpc.invoke("echo", 2);
  controller.abort();

  await assert.rejects(cancelled, (error: unknown) => error instanceof AltaError && error.code === "operation_canceled");
  assert.deepEqual(await waiting, { echo: 2 });
  assert.deepEqual(fake.opened, ["i1"], "one session for both");
});

test("a connection ends when the host loses the page, whatever is pending", async () => {
  const fake = host();
  const { rpc } = controllerOf(fake);
  await rpc.invoke("echo", 1);
  const pending = rpc.invoke("slow", {});
  await until(() => fake.frames.some(frame => frame.command === "slow"), "the call reached the host");

  fake.state.listener!.closed(null, "watch_ended");

  await assert.rejects(pending, (error: unknown) => error instanceof AltaError && error.code === "connection_closed" && error.retryable);
});

test("the connection object follows the contract of the client: it refuses frames once closed, and drops what is not a frame", () => {
  const sent: string[][] = [];
  const connection = createCarrierConnection({ carrier: { open: async () => ({ status: "ok", connection: "c", maximumFrameBytes: 100 }), send: async (_i, _c, frames) => { sent.push([...frames]); return "ok"; },
    close: async () => { }, listen: () => () => { } }, instanceId: "i", connection: "c", maximumFrameBytes: 100, minimumGapMilliseconds: 0 });
  const received: unknown[] = [];
  const remove = connection.setReceiveHandler(frame => received.push(frame));

  connection.deliver(["not json", JSON.stringify([1]), JSON.stringify({ neoastra: 2, kind: "result" }), JSON.stringify({ neoastra: 1, kind: "result", id: "a" })]);
  assert.deepEqual(received, [{ neoastra: 1, kind: "result", id: "a" }]);
  assert.throws(() => connection.send({ kind: "invoke" }), /discriminator/);
  remove();
  connection.deliver([JSON.stringify({ neoastra: 1, kind: "result", id: "b" })]);
  assert.equal(received.length, 1);

  connection.end("host_closed");
  assert.equal(connection.state, "closed");
  assert.equal(connection.closeReason, "host_closed");
  assert.equal(connection.closed.aborted, true);
  assert.throws(() => connection.send({ neoastra: 1, kind: "cancel", id: "x" }), (error: unknown) => (error as { code?: string }).code === "connection_closed");
  assert.deepEqual(sent, []);
});
