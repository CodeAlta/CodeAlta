import assert from "node:assert/strict";
import test from "node:test";
import type { AltaRpc } from "../../pluginScript/alta";
import { changedEvent, createMobileApi, type MobileWatchEvent } from "./api";
import { mobileView, qrPath, quietZone, readStatus, type MobileStatus } from "./status";

const serving = (overrides: Partial<MobileStatus> = {}, tailscale: Partial<MobileStatus["tailscale"]> = {}): MobileStatus => ({
  server: "listening", port: 51234, serverDetail: null,
  tailscale: { state: "serving", url: "https://pc.tail1.ts.net:8443", detail: "Published on the tailnet.", fixUrl: null, ...tailscale },
  connectUrl: "https://pc.tail1.ts.net:8443/#k=secret", address: "https://pc.tail1.ts.net:8443", qr: ["101", "010", "111"], ...overrides,
});

test("the QR rows become one path of dark runs inside the quiet zone", () => {
  const { size, path } = qrPath(["110", "011", "101"]);
  assert.equal(size, 3 + 2 * quietZone);
  assert.equal(path, "M4 4h2v1h-2zM5 5h2v1h-2zM4 6h1v1h-1zM6 6h1v1h-1z");
  assert.equal(qrPath(["000", "000", "000"]).path, "");
});

test("a status is read as the plugin sends it, and what is not one is refused", () => {
  assert.deepEqual(readStatus(JSON.parse(JSON.stringify(serving()))), serving());
  assert.equal(readStatus({ server: "dancing", tailscale: { state: "serving" } }), null);
  assert.equal(readStatus(null), null);
  assert.equal(readStatus({ ...serving(), qr: ["10", "1"] })?.qr, null, "a code that is not square is left out");
  assert.equal(readStatus({ ...serving(), qr: ["1x", "01"] })?.qr, null);
});

test("served: the code, the address and nothing wrong", () => {
  const view = mobileView(serving());
  assert.equal(view.code?.address, "https://pc.tail1.ts.net:8443");
  assert.equal(view.code?.size, 3 + 2 * quietZone);
  assert.deepEqual(view.problems, []);
  assert.equal(view.waiting, null);
});

test("starting: a line that says so, and no problem", () => {
  assert.equal(mobileView(serving({ server: "starting", connectUrl: null, address: null, qr: null }, { state: "pending" })).waiting, "The phone page is starting.");
  const pending = mobileView(serving({ connectUrl: null, address: null, qr: null }, { state: "pending" }));
  assert.equal(pending.waiting, "Tailscale is starting to publish the page.");
  assert.deepEqual(pending.problems, []);
});

test("a port in use says which, and why when the plugin knows it", () => {
  const [problem] = mobileView(serving({ server: "port-in-use", serverDetail: null, connectUrl: null, qr: null }, { state: "stopped" })).problems;
  assert.deepEqual([problem.key, problem.intent, problem.title, problem.parameters, problem.hint, problem.fix, problem.retry],
    ["server", "danger", "The port {port} is in use by another program.", { port: 51234 }, "Another CodeAlta with the Mobile plugin on may be using it.", null, false]);
  const failed = mobileView(serving({ server: "failed", serverDetail: "Access denied." }));
  assert.deepEqual([failed.problems[0].title, failed.problems[0].detail], ["The server of the phone page did not start.", "Access denied."]);
  assert.equal(failed.waiting, null);
});

test("each Tailscale state that keeps a phone out says what fixes it, and Try again only where the state can change", () => {
  const problem = (state: MobileStatus["tailscale"]["state"], fixUrl: string | null = null) =>
    mobileView(serving({ connectUrl: null, address: null, qr: null }, { state, fixUrl, detail: "line of the plugin" })).problems[0];
  const notInstalled = problem("not-installed", "https://tailscale.com/download");
  assert.deepEqual([notInstalled.intent, notInstalled.fix, notInstalled.retry], ["danger", { label: "Download Tailscale", url: "https://tailscale.com/download" }, false]);
  const serve = problem("serve-disabled", "https://login.tailscale.com/admin/settings/features");
  assert.deepEqual([serve.title, serve.fix?.label, serve.retry], ["Tailscale Serve is not enabled for this tailnet.", "Enable it in the admin console", true]);
  assert.deepEqual([problem("not-running").retry, problem("not-running").fix], [true, null]);
  assert.equal(problem("port-taken").retry, true);
  assert.equal(problem("failed").retry, true);
  assert.equal(problem("failed", "https://example.com/help").fix?.label, "Open the page that fixes it");
  assert.equal(problem("stopped").retry, false);
  assert.equal(problem("failed").detail, "line of the plugin");
  assert.equal(mobileView(serving({}, { state: "serving" })).problems.length, 0);
});

// A plugin the test plays: the calls answer from `answers`, the event reaches the listeners that subscribed.
function plugin() {
  const handlers = new Set<(value: unknown) => void>();
  const generation = { value: 0, listeners: new Set<(value: number) => void>() };
  const calls: string[] = [];
  let answers: Array<unknown> = [];
  const rpc: AltaRpc = {
    invoke: async name => { calls.push(name); const next = answers.shift(); if (next instanceof Error) throw next; return next; },
    stream: async () => { throw new Error("not used"); },
    subscribe: async (name, handler) => { calls.push(`subscribe ${name}`); handlers.add(handler); return () => { handlers.delete(handler); }; },
    generation: { get value() { return generation.value; }, subscribe: listener => { generation.listeners.add(listener); return () => { generation.listeners.delete(listener); }; } },
  };
  return {
    rpc, calls, handlers,
    answer(...values: unknown[]) { answers = values; },
    publish(value: unknown) { for (const handler of handlers) handler(value); },
    reconnect() { generation.value++; for (const listener of generation.listeners) listener(generation.value); },
  };
}
const settle = () => new Promise(resolve => setTimeout(resolve, 0));

test("the tab listens before it reads the status, follows the changes, and listens again after a reconnection", async () => {
  const fake = plugin();
  const events: MobileWatchEvent[] = [];
  fake.answer(serving({ server: "starting" }));
  const stop = createMobileApi(fake.rpc).watch(event => events.push(event));
  await settle();
  assert.deepEqual(fake.calls, [`subscribe ${changedEvent}`, "mobile.status"]);
  assert.deepEqual(events.map(e => e.kind === "status" && e.status.server), ["starting"]);
  fake.publish(serving());
  fake.publish({ nonsense: true });
  assert.deepEqual(events.map(e => e.kind === "status" && e.status.server), ["starting", "listening"]);
  fake.answer(serving());
  fake.reconnect();
  await settle();
  assert.deepEqual(fake.calls.slice(2), [`subscribe ${changedEvent}`, "mobile.status"]);
  assert.equal(fake.handlers.size, 1, "the listening of the old connection is stopped");
  stop();
  assert.equal(fake.handlers.size, 0);
});

test("a plugin that does not answer is told, and asked again when it may answer later", async () => {
  const fake = plugin();
  const events: MobileWatchEvent[] = [];
  fake.answer(Object.assign(new Error("connection closed"), { retryable: true }), serving());
  const stop = createMobileApi(fake.rpc, { retryMilliseconds: 1 }).watch(event => events.push(event));
  await new Promise(resolve => setTimeout(resolve, 30));
  assert.deepEqual(events.map(e => e.kind), ["failed", "status"]);
  assert.deepEqual(events[0], { kind: "failed", message: "connection closed" });
  stop();
});

test("Try again asks Tailscale again and gives the new status", async () => {
  const fake = plugin();
  fake.answer(serving({}, { state: "serving" }));
  const status = await createMobileApi(fake.rpc).retry();
  assert.deepEqual(fake.calls, ["mobile.retry"]);
  assert.equal(status.tailscale.state, "serving");
  fake.answer({ wrong: true });
  await assert.rejects(createMobileApi(fake.rpc).status(), /cannot read/);
});
