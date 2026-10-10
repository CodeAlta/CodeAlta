import assert from "node:assert/strict";
import test from "node:test";
import { AltaError, type AltaRpc } from "../pluginScript/alta";
import { retryLimit } from "../pluginScript/retry";
import { appModules } from "../lent/appModules";
import type { StatisticsEvent } from "./api";
import { projectOf, statisticsContext } from "./canvasContext";
import { createRpcApi, eventsName, everyDay, readEvent, type StatisticsDirectory } from "./rpcApi";
import type { StatisticsRequest, StatisticsStatus } from "./types";

// The StatisticsApi over alta.rpc, with a carrier that records every call and plays the plugin.

type Call = { name: string; input: any };

const status = (patch: Partial<StatisticsStatus> = {}): StatisticsStatus => ({ state: "done", sessionsTotal: 3, sessionsDone: 3, bytesTotal: 10, bytesDone: 10, skippedCount: 0, skipped: [], pendingFlow: 0, revision: 1, ...patch });

function carrier(answers: Record<string, (input: any) => unknown> = {}) {
  const calls: Call[] = [];
  const handlers = new Set<(value: unknown) => void>();
  const generation = { value: 0, listeners: new Set<(value: number) => void>() };
  let subscriptions = 0;
  let unsubscribed = 0;
  const rpc: AltaRpc = {
    async invoke(name, input, options) {
      calls.push({ name, input });
      if (options?.signal?.aborted) throw new AltaError("operation_canceled", "The call was canceled.");
      const answer = answers[name];
      if (!answer) throw new AltaError("command_not_found", `no ${name}`);
      return answer(input);
    },
    async stream() { throw new Error("not used"); },
    async subscribe(name, handler) {
      assert.equal(name, eventsName);
      subscriptions++;
      handlers.add(handler);
      return () => { unsubscribed++; handlers.delete(handler); };
    },
    generation: { get value() { return generation.value; }, subscribe: listener => { generation.listeners.add(listener); return () => { generation.listeners.delete(listener); }; } },
  };
  return {
    rpc, calls,
    emit: (value: unknown) => { for (const handler of [...handlers]) handler(value); },
    reconnect: () => { generation.value++; for (const listener of [...generation.listeners]) listener(generation.value); },
    counts: () => ({ subscriptions, unsubscribed, listening: handlers.size, generationListeners: generation.listeners.size }),
  };
}

const settle = () => new Promise(resolve => setTimeout(resolve, 5));
const request: StatisticsRequest = { period: "30d", frequency: "week", comparison: "previousPeriod", filter: { project: "p1" } };

test("every question is one call of the plugin, named statistics.<question>, with the request and its arguments as they are", async () => {
  const plugin = carrier(Object.fromEntries(["summary", "series", "top", "tools", "models", "projects", "sessions", "session", "distribution", "calendar", "week-hour", "records", "health", "details", "runs"]
    .map(name => [`statistics.${name}`, (input: unknown) => ({ echoed: name, input })])));
  const api = createRpcApi(plugin.rpc);
  await api.summary(request);
  await api.series(request, "tokens", "provider");
  await api.series(request, "runs", null);
  await api.top(request, "models", "calls");
  await api.tools(request);
  await api.models(request);
  await api.projects(request);
  await api.sessions(request, "recent");
  await api.session("abc", true);
  await api.distribution(request, "run-duration", "git");
  await api.calendar(request);
  await api.weekHour(request);
  await api.records(request);
  await api.health(request);
  await api.details(request, "skill");
  await api.runs(request, "longest");
  assert.deepEqual(plugin.calls.map(call => call.name), ["summary", "series", "series", "top", "tools", "models", "projects", "sessions", "session", "distribution", "calendar", "week-hour", "records", "health", "details", "runs"].map(name => `statistics.${name}`));
  assert.deepEqual(plugin.calls[0].input, { request });
  assert.deepEqual(plugin.calls[1].input, { request, metric: "tokens", group: "provider" });
  assert.deepEqual(plugin.calls[3].input, { request, kind: "models", by: "calls" });
  assert.deepEqual(plugin.calls[7].input, { request, sort: "recent" });
  assert.deepEqual(plugin.calls[8].input, { id: "abc", withChildren: true });
  assert.deepEqual(plugin.calls[9].input, { request, measure: "run-duration", subject: "git" });
  assert.deepEqual(plugin.calls[14].input, { request, list: "skill" });
  assert.deepEqual(plugin.calls[15].input, { request, sort: "longest" });
});

test("the results are what the plugin wrote, untouched", async () => {
  const written = { query: { period: "30d", coverage: { complete: true, historyState: "done" } }, tiles: [{ id: "tokens", value: 12 }] };
  const api = createRpcApi(carrier({ "statistics.summary": () => written }).rpc);
  assert.deepEqual(await api.summary(request), written);
});

test("the controls call the plugin and give the status it answers; a choice of days carries the days", async () => {
  const done = status();
  const plugin = carrier({
    "statistics.status": () => done, "statistics.choose-history": () => done, "statistics.pause": () => done, "statistics.resume": () => done, "statistics.stop-here": () => done,
    "statistics.reset": () => done, "statistics.forget-deleted": () => ({ count: 4 }), "statistics.context": () => ({ spaces: [], projects: [] }),
  });
  const api = createRpcApi(plugin.rpc);
  assert.equal(await api.status(), done);
  await api.chooseHistory({ kind: "all" });
  await api.chooseHistory({ kind: "days", days: 90 });
  await api.chooseHistory({ kind: "fromToday" });
  await api.pause();
  await api.resume();
  await api.stopHere();
  assert.equal(await api.forgetDeleted(), 4);
  await api.resetStatistics!();
  assert.deepEqual(await api.directory(), { spaces: [], projects: [] });
  assert.deepEqual(plugin.calls.map(call => call.name), ["status", "choose-history", "choose-history", "choose-history", "pause", "resume", "stop-here", "forget-deleted", "reset", "context"].map(name => `statistics.${name}`));
  assert.deepEqual(plugin.calls[1].input, { kind: "all" });
  assert.deepEqual(plugin.calls[2].input, { kind: "days", days: 90 });
  assert.deepEqual(plugin.calls[3].input, { kind: "fromToday" });
});

test("a call that is canceled rejects with an AbortError, before it is sent or while it waits; any other failure keeps its code", async () => {
  const plugin = carrier({ "statistics.summary": () => { throw new AltaError("invalid_request", "'soon' is not a period."); }, "statistics.tools": () => { throw new AltaError("operation_canceled", "The call was canceled."); } });
  const api = createRpcApi(plugin.rpc);
  const stopped = new AbortController();
  stopped.abort();
  await assert.rejects(api.summary(request, stopped.signal), { name: "AbortError" });
  assert.equal(plugin.calls.length, 0, "nothing is sent for a question already canceled");
  await assert.rejects(api.tools(request), { name: "AbortError" });
  await assert.rejects(api.summary(request), (error: AltaError) => error.code === "invalid_request" && error.message === "'soon' is not a period.");
  await assert.rejects(api.models(request), (error: AltaError) => error.code === "command_not_found");
});

test("readEvent accepts a status and a change of days, and nothing else", () => {
  assert.deepEqual(readEvent({ kind: "status", status: status() }), { kind: "status", status: status() });
  assert.deepEqual(readEvent({ kind: "data", change: { revision: 3, fromDay: 20261001, toDay: 20261009, sessionIds: ["a", 7, "b"] } }), { kind: "data", change: { revision: 3, fromDay: 20261001, toDay: 20261009, sessionIds: ["a", "b"] } });
  for (const bad of [null, "status", 4, [], {}, { kind: "status" }, { kind: "status", status: { state: 3 } }, { kind: "data", change: { fromDay: "x", toDay: 1 } }, { kind: "other", status: status() }]) assert.equal(readEvent(bad), null, JSON.stringify(bad));
});

test("subscribe listens to the events of the plugin, and stops when the function it gave is called", async () => {
  const plugin = carrier();
  const api = createRpcApi(plugin.rpc);
  const heard: StatisticsEvent[] = [];
  const off = api.subscribe(event => heard.push(event));
  await settle();
  plugin.emit({ kind: "status", status: status({ state: "reading" }) });
  plugin.emit({ kind: "data", change: { revision: 2, fromDay: 20261008, toDay: 20261009, sessionIds: [] } });
  plugin.emit({ kind: "junk" });
  assert.deepEqual(heard.map(event => event.kind), ["status", "data"]);
  off();
  assert.deepEqual(plugin.counts(), { subscriptions: 1, unsubscribed: 1, listening: 0, generationListeners: 0 });
  plugin.emit({ kind: "status", status: status() });
  assert.equal(heard.length, 2, "nothing is heard after the end");
});

test("subscribe is idempotent under a double run: a listening that ends before it began leaves nothing behind", async () => {
  const plugin = carrier();
  const api = createRpcApi(plugin.rpc);
  const first = api.subscribe(() => { });
  first();
  const second = api.subscribe(() => { });
  await settle();
  second();
  assert.deepEqual(plugin.counts(), { subscriptions: 2, unsubscribed: 2, listening: 0, generationListeners: 0 });
});

test("after the connection to the plugin is made again, the listening begins again, every day is stale and the status is read once more", async () => {
  const plugin = carrier({ "statistics.status": () => status({ state: "reading", revision: 9 }) });
  const api = createRpcApi(plugin.rpc);
  const heard: StatisticsEvent[] = [];
  const off = api.subscribe(event => heard.push(event));
  await settle();
  plugin.reconnect();
  await settle();
  assert.deepEqual(plugin.counts(), { subscriptions: 2, unsubscribed: 1, listening: 1, generationListeners: 1 }, "the old listening ends, a new one begins");
  assert.deepEqual(heard.map(event => event.kind), ["data", "status"]);
  assert.deepEqual(heard[0], { kind: "data", change: everyDay });
  assert.equal((heard[1] as { status: StatisticsStatus }).status.revision, 9);
  plugin.emit({ kind: "status", status: status({ revision: 10 }) });
  assert.equal(heard.length, 3);
  off();
  assert.equal(plugin.counts().listening, 0);
});

test("a window that carries no calls leaves the canvas quiet: subscribing never throws", async () => {
  const rpc: AltaRpc = {
    invoke: () => Promise.reject(new AltaError("rpc_unavailable", "none")), stream: () => Promise.reject(new AltaError("rpc_unavailable", "none")),
    subscribe: () => Promise.reject(new AltaError("rpc_unavailable", "none")), generation: { value: 0, subscribe: () => () => { } },
  };
  const api = createRpcApi(rpc);
  const off = api.subscribe(() => assert.fail("nothing to hear"));
  await settle();
  off();
  await assert.rejects(api.status(), (error: AltaError) => error.code === "rpc_unavailable");
});

/** A plugin whose connection the test opens and ends: listening fails while it is not reachable, and ends with the connection. */
function connection(answers: Record<string, (input: any) => unknown> = {}) {
  const handlers = new Set<(value: unknown) => void>();
  const listeners = new Set<(value: boolean) => void>();
  const state = { reachable: true, open: false, subscriptions: 0, refused: 0 };
  const set = (open: boolean) => { if (state.open === open) return; state.open = open; for (const listener of [...listeners]) listener(open); };
  const rpc: AltaRpc = {
    async invoke(name, input) {
      if (!state.reachable) throw new AltaError("connection_closed", "The window cannot reach the plugin now.", { retryable: true });
      set(true);
      const answer = answers[name];
      if (!answer) throw new AltaError("command_not_found", `no ${name}`);
      return answer(input);
    },
    async stream() { throw new Error("not used"); },
    async subscribe(_name, handler) {
      if (!state.reachable) { state.refused++; throw new AltaError("connection_closed", "The window cannot reach the plugin now.", { retryable: true }); }
      set(true);
      state.subscriptions++;
      handlers.add(handler);
      return () => { handlers.delete(handler); };
    },
    generation: { value: 0, subscribe: () => () => { } },
    connected: { get value() { return state.open; }, subscribe: listener => { listeners.add(listener); return () => { listeners.delete(listener); }; } },
  };
  return {
    rpc, state,
    emit: (value: unknown) => { for (const handler of [...handlers]) handler(value); },
    /** The connection ends: what listened on it is gone. */
    end: () => { handlers.clear(); set(false); },
    get listening() { return handlers.size; }, get connectedListeners() { return listeners.size; },
  };
}

const eventually = async (condition: () => boolean, what: string) => {
  for (let index = 0; index < 400; index++) { if (condition()) return; await settle(); }
  assert.fail(`never happened: ${what}`);
};

test("a listening that could not begin because the plugin was not reachable begins once it is, and reads what it missed", async () => {
  const plugin = connection({ "statistics.status": () => status({ state: "reading", revision: 4 }) });
  plugin.state.reachable = false;
  const api = createRpcApi(plugin.rpc, { retryMilliseconds: 5 });
  const heard: StatisticsEvent[] = [];
  const off = api.subscribe(event => heard.push(event));
  await eventually(() => plugin.state.refused >= 2, "it asks again while the plugin is not reachable");
  assert.equal(plugin.listening, 0);

  plugin.state.reachable = true;
  await eventually(() => plugin.listening === 1 && heard.length === 2, "it listens once the plugin is reached");
  assert.deepEqual(heard[0], { kind: "data", change: everyDay }, "what was read while nobody listened may be stale");
  assert.equal((heard[1] as { status: StatisticsStatus }).status.revision, 4);
  plugin.emit({ kind: "status", status: status({ revision: 5 }) });
  assert.equal(heard.length, 3);
  off();
  assert.equal(plugin.connectedListeners, 0);
});

test("a connection that ends while the canvas only listens is made again, with every day stale and the status read once more", async () => {
  const plugin = connection({ "statistics.status": () => status({ revision: 7 }) });
  const api = createRpcApi(plugin.rpc, { retryMilliseconds: 5 });
  const heard: StatisticsEvent[] = [];
  const off = api.subscribe(event => heard.push(event));
  await eventually(() => plugin.listening === 1, "it listens");
  assert.deepEqual(heard, []);

  // The plugin is reloaded: the canvas makes no call of its own that would tell it that nothing is heard any more.
  plugin.end();
  await eventually(() => plugin.listening === 1 && heard.length === 2, "it listens again");
  assert.equal(plugin.state.subscriptions, 2);
  assert.deepEqual(heard[0], { kind: "data", change: everyDay });
  assert.equal((heard[1] as { status: StatisticsStatus }).status.revision, 7);

  // A canvas that went away asks for nothing more.
  off();
  plugin.end();
  await settle(); await settle(); await settle();
  assert.equal(plugin.state.subscriptions, 2);
});

test("a listening that the plugin keeps refusing stops asking by itself, and asks again when the window reaches the plugin", async () => {
  // The host closed the instance of a hidden tab to make room: every connection is refused until the tab is shown and asks for its instance again.
  const plugin = connection({ "statistics.status": () => status({ revision: 9 }) });
  plugin.state.reachable = false;
  const api = createRpcApi(plugin.rpc, { retryMilliseconds: 1 });
  const heard: StatisticsEvent[] = [];
  const off = api.subscribe(event => heard.push(event));
  try {
    await eventually(() => plugin.state.refused >= 1 + retryLimit, "it asks again a few times");
    for (let index = 0; index < 20; index++) await settle();
    assert.equal(plugin.state.refused, 1 + retryLimit, "then it waits for a reason to ask: as many tries as a call makes");
    assert.equal(plugin.listening, 0);

    // A call of the canvas made the connection again (its tab is shown, its queries ask): the listening begins, and reads what it missed.
    plugin.state.reachable = true;
    await api.status();
    await eventually(() => plugin.listening === 1 && heard.length === 2, "it listens once a connection is open");
    assert.deepEqual(heard[0], { kind: "data", change: everyDay });
    assert.equal((heard[1] as { status: StatisticsStatus }).status.revision, 9);

    // The count starts again with each reason to ask: the connection ends and the plugin refuses once more.
    plugin.state.reachable = false;
    const before = plugin.state.refused;
    plugin.end();
    await eventually(() => plugin.state.refused >= before + retryLimit, "it asks again a few times after the connection ended");
    for (let index = 0; index < 20; index++) await settle();
    assert.equal(plugin.state.refused, before + retryLimit);
  } finally {
    // A listening that asks for ever would keep the test alive.
    off();
  }

  assert.equal(plugin.connectedListeners, 0);
});

// ---- what the canvas is given from the place it is opened at ----

const directory: StatisticsDirectory = {
  spaces: [{ id: "default", name: "Default", isDefault: true, projectIds: ["p1", "p2"] }, { id: "work", name: "Work", isDefault: false, projectIds: ["p1"] }],
  projects: [{ id: "p1", name: "CodeAlta" }, { id: "P2", name: "Tomlyn" }],
};
const alta = (context: Partial<{ key: string | null; input: unknown; spaceId: string | null }> = {}) => ({
  context: { pluginKey: "builtin:statistics", canvasId: "statistics", instanceId: "i-1", spaceId: "work", projectId: null, sessionId: null, key: null, input: null, ...context },
  host: { openSession: (id: string) => opened.push(id) },
}) as never;
const opened: string[] = [];

test("the project of a canvas is the one its key names, or its input", () => {
  assert.equal(projectOf({ key: "project:p1", input: null }), "p1");
  assert.equal(projectOf({ key: "project:", input: null }), null);
  assert.equal(projectOf({ key: "other", input: { project: "p9" } }), "p9");
  assert.equal(projectOf({ key: null, input: { project: "" } }), null);
  assert.equal(projectOf({ key: null, input: null }), null);
  assert.equal(projectOf({ key: null, input: "p1" }), null);
});

test("the context starts the canvas on the space it shows, with the spaces and their projects, and opens sessions through the window", () => {
  const context = statisticsContext(alta(), true, directory);
  assert.equal(context.instanceId, "i-1");
  assert.equal(context.visible, true);
  assert.equal(context.spaceId, "work");
  assert.deepEqual(context.spaces, [{ id: "default", name: "Default", projectIds: ["p1", "p2"] }, { id: "work", name: "Work", projectIds: ["p1"] }]);
  assert.equal(context.projectId, null);
  context.openSession!("s-9");
  assert.deepEqual(opened, ["s-9"]);
  assert.equal(statisticsContext(alta(), false, directory).visible, false);
});

test("the space that holds every project is no filter; a canvas of a project starts on the project, with its name, whatever the space", () => {
  assert.equal(statisticsContext(alta({ spaceId: "default" }), true, directory).spaceId, null);
  const project = statisticsContext(alta({ key: "project:p2" }), true, directory);
  assert.equal(project.projectId, "p2");
  assert.equal(project.projectName, "Tomlyn", "the name is found whatever the case of the id");
  assert.equal(project.spaceId, null);
  assert.equal(statisticsContext(alta({ key: "project:unknown" }), true, directory).projectName, null);
});

test("without the directory the canvas still opens, on the space of its tab", () => {
  const context = statisticsContext(alta(), true, null);
  assert.equal(context.spaceId, "work");
  assert.equal(context.spaces, undefined);
  assert.equal(statisticsContext(alta({ spaceId: null }), true, null).instanceId, "i-1");
});

test("the application module of the statistics is listed, with the entry the plugin names", () => {
  assert.deepEqual(appModules.filter(module => module.name === "statistics"), [{ name: "statistics", entry: "src/statistics/canvas.tsx" }]);
});
