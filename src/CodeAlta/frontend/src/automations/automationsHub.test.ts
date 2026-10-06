import assert from "node:assert/strict";
import test from "node:test";
import type { AutomationItem, AutomationRunItem, AutomationsListResponse, AutomationTriggerItem } from "#neoastra";
import { createAutomationsHub, reconnectMilliseconds, refreshMilliseconds, type AutomationsApi } from "./automationsHub";

const item = (id: string): AutomationItem => ({
  id, name: `Automation ${id}`, enabled: true, prompt: "Do the thing.", projectId: null, projectName: null, projectFolder: null, storeProjectId: null, file: "/home/config.toml",
  provider: null, model: null, effort: null, agent: null, catchUp: false, triggers: [], problem: null, nextRunAt: null, running: false, lastRun: null, repository: null, watchProblem: null, allowed: true,
});
const run = (status: string, sessionId: string | null = "s1"): AutomationRunItem =>
  ({ id: "r1", automationId: "a", name: "A", sessionId, projectId: "p", startedAt: "2026-10-06T12:00:00Z", endedAt: null, trigger: "manual", detail: null, status, message: status === "failed" ? "No model." : null });
const listed = (items: AutomationItem[], change: Partial<AutomationsListResponse> = {}): AutomationsListResponse =>
  ({ status: "ok", paused: false, scanned: true, items, faults: [], runs: [], upcoming: [], ...change });
const trigger: AutomationTriggerItem = { type: "daily", minute: 0, every: 1, at: ["09:00"], days: [], expression: null, event: "opened", authors: "trusted" };
const settle = () => new Promise<void>(resolve => setTimeout(resolve, 0));

// A host that answers what the test says and records what it is asked. Its notices are sent by the test.
function fixture() {
  const calls: string[] = [];
  const timers: { run: () => void; milliseconds: number }[] = [];
  const state = { list: listed([item("a")]), mutation: { status: "ok", id: "a", message: null as string | null }, run: { status: "ok", run: run("running") as AutomationRunItem | null },
    fail: false, watches: [] as { notify: () => void; end: () => void; signal?: AbortSignal }[],
    /** The listings that wait for the test to let them answer, oldest first; null when they answer at once. */
    held: null as (() => void)[] | null };
  const answer = async <T,>(name: string, request: object, value: T) => {
    calls.push(`${name}:${JSON.stringify(request)}`);
    if (state.fail) throw new Error("gone");
    return value;
  };
  const api = {
    list: async (request: object) => {
      const held = state.held;
      if (held) await new Promise<void>(resolve => held.push(resolve));
      return answer("list", request, state.list);
    },
    refresh: (request: object) => answer("refresh", request, state.list),
    save: (request: object) => answer("save", request, state.mutation),
    delete: (request: object) => answer("delete", request, state.mutation),
    setEnabled: (request: object) => answer("setEnabled", request, state.mutation),
    allow: (request: object) => answer("allow", request, state.mutation),
    setPaused: (request: object) => answer("setPaused", request, state.mutation),
    run: (request: object) => answer("run", request, state.run),
    runs: (request: object) => answer("runs", request, { status: "ok", runs: [run("completed")] }),
    preview: (request: object) => answer("preview", request, { status: "ok", message: null, times: ["2026-10-07T09:00:00Z"] }),
    watch: async (request: object, options?: { signal?: AbortSignal }) => {
      calls.push(`watch:${JSON.stringify(request)}`);
      if (state.fail) throw new Error("gone");
      const waiting: (() => void)[] = [];
      let pending = 0;
      let ended = false;
      const wake = () => { for (const resume of waiting.splice(0)) resume(); };
      state.watches.push({ notify: () => { pending++; wake(); }, end: () => { ended = true; wake(); }, signal: options?.signal });
      options?.signal?.addEventListener("abort", () => { ended = true; wake(); }, { once: true });
      return (async function* () {
        yield { revision: 0 };
        for (let revision = 1; ; revision++) {
          while (pending === 0 && !ended) await new Promise<void>(resolve => waiting.push(resolve));
          if (ended) return;
          pending--;
          yield { revision };
        }
      })();
    },
  } as unknown as AutomationsApi;
  const hub = createAutomationsHub(api, { set: (run, milliseconds) => { const timer = { run, milliseconds }; timers.push(timer); return timer; }, clear: timer => { timers.splice(timers.indexOf(timer as typeof timers[number]), 1); } });
  return { hub, calls, timers, state };
}

test("the page lists once it is connected, again when the host says something changed, and forgets when it leaves", async () => {
  const { hub, calls, state } = fixture();
  const seen: number[] = [];
  hub.subscribe(() => seen.push(hub.getSnapshot().items.length));
  assert.deepEqual(hub.getSnapshot(), { loaded: false, available: false, paused: false, scanned: false, items: [], faults: [], runs: [], upcoming: [] });
  assert.deepEqual(await hub.runs("a"), [], "Nothing is asked before a host is known.");
  assert.equal((await hub.run("a")).ok, false);
  assert.equal(await hub.preview(trigger, new AbortController().signal), null);
  assert.equal(calls.length, 0);

  const disconnect = hub.connect("epoch-1");
  await settle();
  assert.deepEqual(calls, ['watch:{"expectedEpoch":"epoch-1"}', 'list:{"expectedEpoch":"epoch-1"}']);
  assert.deepEqual([hub.getSnapshot().loaded, hub.getSnapshot().available, hub.getSnapshot().items.map(value => value.id)], [true, true, ["a"]]);

  state.list = listed([item("a"), item("b")], { paused: true, runs: [run("completed")], upcoming: [{ automationId: "a", at: "2026-10-07T09:00:00Z" }] });
  state.watches[0].notify();
  await settle();
  const snapshot = hub.getSnapshot();
  assert.deepEqual([snapshot.items.length, snapshot.paused, snapshot.runs.length, snapshot.upcoming.length], [2, true, 1, 1]);
  assert.equal(calls.filter(call => call.startsWith("list:")).length, 2);

  disconnect();
  assert.equal(state.watches[0].signal?.aborted, true);
  assert.deepEqual([hub.getSnapshot().loaded, hub.getSnapshot().items.length], [false, 0]);
  assert.ok(seen.length >= 3 && seen.at(-1) === 0);
});

test("a host without automations, or of another epoch, shows none; a host that stops telling is listened to again", async () => {
  const { hub, calls, timers, state } = fixture();
  state.list = { ...listed([]), status: "unavailable" };
  hub.connect("epoch-1");
  await settle();
  assert.deepEqual([hub.getSnapshot().loaded, hub.getSnapshot().available], [true, false]);

  // The times shown are brought up to date while nothing else changes.
  const clock = timers.find(timer => timer.milliseconds === refreshMilliseconds)!;
  state.list = listed([item("a")]);
  clock.run();
  await settle();
  assert.equal(hub.getSnapshot().items.length, 1);
  assert.equal(timers.filter(timer => timer.milliseconds === refreshMilliseconds).length, 2, "The clock is set again.");

  // The channel ends: the page waits, then listens again.
  state.watches[0].end();
  await settle();
  const again = timers.find(timer => timer.milliseconds === reconnectMilliseconds)!;
  assert.ok(again);
  again.run();
  await settle();
  assert.equal(calls.filter(call => call.startsWith("watch:")).length, 2);

  // A host that is gone leaves what was shown; the next listing brings it up to date.
  state.fail = true;
  await hub.refresh();
  assert.equal(hub.getSnapshot().items.length, 1);
  assert.deepEqual(await hub.save({ id: null, name: "n", enabled: true, prompt: "p", projectId: null, provider: null, model: null, effort: null, agent: null, catchUp: false, triggers: [] }, null), { ok: false, message: null });
  assert.deepEqual(await hub.runs("a"), []);
  assert.deepEqual(await hub.run("a"), { ok: false });
  assert.equal(await hub.preview(trigger, new AbortController().signal), null);
});

test("a change is sent to the host and the list is read again before it answers", async () => {
  const { hub, calls, state } = fixture();
  hub.connect("epoch-1");
  await settle();
  calls.length = 0;
  const input = { id: null, name: "Nightly", enabled: true, prompt: "Review.", projectId: "p", provider: null, model: null, effort: null, agent: null, catchUp: false, triggers: [trigger] };

  state.list = listed([item("a"), item("new")]);
  state.mutation = { status: "ok", id: "new", message: null };
  assert.deepEqual(await hub.save(input, "p"), { ok: true, id: "new", message: null });
  assert.deepEqual(calls, [`save:${JSON.stringify({ expectedEpoch: "epoch-1", automation: input, storeProjectId: "p" })}`, 'list:{"expectedEpoch":"epoch-1"}']);
  assert.ok(hub.getSnapshot().items.some(value => value.id === "new"), "The automation that was saved is listed when the call returns.");

  state.mutation = { status: "refused", id: null as unknown as string, message: "An automation has a name." };
  assert.deepEqual(await hub.save({ ...input, name: "" }, null), { ok: false, id: null, message: "An automation has a name." });

  calls.length = 0;
  state.mutation = { status: "ok", id: "a", message: null };
  assert.equal((await hub.setEnabled("a", false)).ok, true);
  assert.equal((await hub.allow("a")).ok, true);
  assert.equal((await hub.setPaused(true)).ok, true);
  assert.equal((await hub.remove("a")).ok, true);
  await hub.refresh();
  assert.deepEqual(calls.filter(call => !call.startsWith("list:")), ['setEnabled:{"expectedEpoch":"epoch-1","id":"a","enabled":false}', 'allow:{"expectedEpoch":"epoch-1","id":"a"}',
    'setPaused:{"expectedEpoch":"epoch-1","paused":true}',
    'delete:{"expectedEpoch":"epoch-1","id":"a"}', 'refresh:{"expectedEpoch":"epoch-1"}']);
});

test("a change that the host also announces returns once the newest list is shown", async () => {
  const { hub, state } = fixture();
  hub.connect("epoch-1");
  await settle();
  const input = { id: null, name: "Nightly", enabled: true, prompt: "Review.", projectId: null, provider: null, model: null, effort: null, agent: null, catchUp: false, triggers: [] };
  state.mutation = { status: "ok", id: "new", message: null };
  state.held = [];
  let done = false;
  const saving = hub.save(input, null).then(outcome => { done = true; return outcome; });
  await settle();
  // The host says something changed while the list asked for by the save is on its way.
  state.watches[0].notify();
  await settle();
  assert.equal(state.held.length, 2);
  state.held[0]();
  await settle();
  assert.equal(done, false, "The list that was overtaken shows nothing: the save waits for the one that will.");
  state.list = listed([item("a"), item("new")]);
  state.held[1]();
  assert.deepEqual(await saving, { ok: true, id: "new", message: null });
  assert.ok(hub.getSnapshot().items.some(value => value.id === "new"));
});

test("a run answers with its session, or with why it did not start", async () => {
  const { hub, calls, state } = fixture();
  hub.connect("epoch-1");
  await settle();
  const started = await hub.run("a");
  assert.deepEqual([started.ok, started.run?.sessionId, started.message], [true, "s1", null]);
  assert.ok(calls.includes('run:{"expectedEpoch":"epoch-1","id":"a"}'));

  state.run = { status: "ok", run: run("failed", null) };
  assert.deepEqual(await hub.run("a"), { ok: false, run: run("failed", null), message: "No model." });
  state.run = { status: "not_found", run: null };
  assert.deepEqual(await hub.run("a"), { ok: false, run: null, message: null });

  assert.deepEqual((await hub.runs("a")).map(value => value.status), ["completed"]);
  assert.ok(calls.includes('runs:{"expectedEpoch":"epoch-1","id":"a","limit":100}'));
  assert.deepEqual(await hub.preview(trigger, new AbortController().signal), { times: ["2026-10-07T09:00:00Z"], problem: null });
});
