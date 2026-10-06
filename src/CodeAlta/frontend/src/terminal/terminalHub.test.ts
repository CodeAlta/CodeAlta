import assert from "node:assert/strict";
import test from "node:test";
import type { TerminalEvent, TerminalItem } from "#neoastra";
import { acknowledgeMilliseconds, createTerminalHub, inputChunk, reconnectMilliseconds, type TerminalsApi } from "./terminalHub";

const item = (id: string): TerminalItem => ({
  id, projectId: "p", sessionId: null, title: "/p", titled: false, folder: "/p", profile: "sh", profileName: "sh", programTitle: null,
  running: true, exitCode: null, integrated: false, busy: false, command: null, lastExitCode: null, columns: 120, rows: 30, created: "2026-10-06T00:00:00Z",
  open: false, attention: false, agent: false });
const event = (kind: string, more: Partial<TerminalEvent> = {}): TerminalEvent => ({ kind, feed: null, id: null, terminals: null, data: null, columns: 0, rows: 0, replayed: false, ...more });
const settle = async () => { for (let turn = 0; turn < 20; turn++) await Promise.resolve(); };

// A host that tells what a test gives it, records what it is asked, and timers a test runs by hand.
function fixture() {
  const calls: string[] = [];
  const watches: Array<{ push: (value: TerminalEvent) => void; end: () => void }> = [];
  const pending: Array<() => void> = [];
  const timers: Array<{ run: () => void; milliseconds: number }> = [];
  let inputStatus = "ok";
  const reply = (name: string, request: object) => { calls.push(`${name} ${JSON.stringify(request)}`); return Promise.resolve({ status: "ok" }); };
  const api = {
    profiles: () => Promise.resolve({ status: "ok", profiles: [{ id: "sh", name: "sh" }], platform: "linux", build: 0 }),
    watch: () => {
      const queue: TerminalEvent[] = [];
      let wake: (() => void) | null = null, ended = false;
      watches.push({ push: value => { queue.push(value); wake?.(); }, end: () => { ended = true; wake?.(); } });
      return Promise.resolve((async function* () {
        while (true) {
          while (queue.length > 0) yield queue.shift()!;
          if (ended) return;
          await new Promise<void>(resolve => { wake = resolve; });
        }
      })());
    },
    create: (request: { projectId: string | null; integration?: boolean | null }) => { calls.push(`create ${JSON.stringify(request)}`); return Promise.resolve({ status: "ok", terminal: item("created") }); },
    input: (request: object) => { calls.push(`input ${JSON.stringify(request)}`); return new Promise<{ status: string }>(resolve => pending.push(() => resolve({ status: inputStatus }))); },
    resize: (request: object) => { calls.push(`resize ${JSON.stringify(request)}`); return new Promise<{ status: string }>(resolve => pending.push(() => resolve({ status: "ok" }))); },
    rename: (request: object) => reply("rename", request),
    close: (request: object) => reply("close", request),
    attach: (request: object) => reply("attach", request),
    detach: (request: object) => reply("detach", request),
    show: (request: object) => reply("show", request),
    acknowledge: (request: object) => reply("acknowledge", request),
  } as unknown as TerminalsApi;
  const hub = createTerminalHub(api, { set: (run, milliseconds) => { const timer = { run, milliseconds }; timers.push(timer); return timer; }, clear: timer => { const index = timers.indexOf(timer as typeof timers[number]); if (index >= 0) timers.splice(index, 1); } });
  return { hub, calls, watches, pending, timers, fail: () => { inputStatus = "refused"; },
    take: () => calls.splice(0),
    // Runs the timers set for a delay, and leaves the others waiting.
    fire: (milliseconds: number) => {
      const due = timers.filter(candidate => candidate.milliseconds === milliseconds);
      for (const timer of due) timers.splice(timers.indexOf(timer), 1);
      for (const timer of due) timer.run();
    } };
}

test("the page lists the terminals the host tells, and is told what those it shows write", async () => {
  const { hub, watches, take, fire, timers } = fixture();
  let changes = 0;
  hub.subscribe(() => { changes++; });
  const written: string[] = [];
  const done: Array<() => void> = [];
  // Shown before the host is there: asked for once it is.
  hub.attach("a", { start: (columns, rows, modes, replayed) => written.push(`start ${columns}x${rows} ${modes} ${replayed}`),
    write: (data, columns, rows, replayed, taken) => { written.push(`write ${data} ${columns}x${rows} ${replayed}`); done.push(taken); } });
  hub.show("a", true);
  assert.deepEqual(take(), []);
  const stop = hub.connect("epoch");
  await settle();
  assert.deepEqual(hub.system(), { platform: "linux", build: 0, profiles: [{ id: "sh", name: "sh" }] });
  watches[0].push(event("feed", { feed: "f1" }));
  await settle();
  assert.deepEqual(take(), ['attach {"expectedEpoch":"epoch","feed":"f1","id":"a"}', 'show {"expectedEpoch":"epoch","feed":"f1","id":"a","visible":true}']);
  watches[0].push(event("list", { terminals: [item("a"), item("b")] }));
  watches[0].push(event("start", { id: "a", data: "modes", columns: 100, rows: 40 }));
  watches[0].push(event("data", { id: "a", data: "hello", replayed: true }));
  // What a terminal nothing shows writes is not for this page.
  watches[0].push(event("data", { id: "b", data: "elsewhere" }));
  await settle();
  assert.deepEqual(hub.list().map(terminal => terminal.id), ["a", "b"]);
  assert.ok(changes >= 2);
  assert.deepEqual(written, ["start 100x40 modes false", "write hello 0x0 true"]);

  // What was taken in is told in one call a while later, however many pieces there were.
  watches[0].push(event("data", { id: "a", data: "!!" }));
  await settle();
  done[0]();
  done[1]();
  assert.deepEqual(take(), []);
  assert.equal(timers.filter(timer => timer.milliseconds === acknowledgeMilliseconds).length, 1);
  fire(acknowledgeMilliseconds);
  assert.deepEqual(take(), ['acknowledge {"expectedEpoch":"epoch","feed":"f1","id":"a","characters":7}']);

  // The tab that is shown, and the terminal the page stops showing.
  hub.show("a", true);
  hub.show("a", false);
  hub.detach("a");
  hub.detach("a");
  assert.deepEqual(take(), ['show {"expectedEpoch":"epoch","feed":"f1","id":"a","visible":false}', 'detach {"expectedEpoch":"epoch","feed":"f1","id":"a"}']);
  stop();
});

test("a host that stops telling is listened to again, and what the page shows is asked for again", async () => {
  const { hub, watches, take, fire, timers } = fixture();
  hub.attach("a", { start: () => { }, write: (_data, _columns, _rows, _replayed, taken) => taken() });
  const stop = hub.connect("epoch");
  await settle();
  watches[0].push(event("feed", { feed: "f1" }));
  watches[0].push(event("data", { id: "a", data: "old" }));
  await settle();
  take();
  watches[0].end();
  await settle();
  // What the feed that is gone gave is not counted by the next one.
  fire(acknowledgeMilliseconds);
  assert.deepEqual(take(), []);
  assert.equal(timers.filter(timer => timer.milliseconds === reconnectMilliseconds).length, 1);
  fire(reconnectMilliseconds);
  await settle();
  assert.equal(watches.length, 2);
  watches[1].push(event("feed", { feed: "f2" }));
  await settle();
  assert.deepEqual(take(), ['attach {"expectedEpoch":"epoch","feed":"f2","id":"a"}']);
  // A page that stopped listening does not listen again.
  stop();
  watches[1].end();
  await settle();
  assert.equal(timers.filter(timer => timer.milliseconds === reconnectMilliseconds).length, 0);
  hub.input("a", "x");
  assert.deepEqual(take(), []);
});

test("what is typed is sent in order, one call at a time; of several sizes the last one counts", async () => {
  const { hub, pending, take, fail } = fixture();
  const stop = hub.connect("epoch");
  hub.input("a", "ls");
  hub.input("a", " -la");
  hub.input("a", "\r");
  hub.input("b", "other");
  await settle();
  assert.deepEqual(take(), ['input {"expectedEpoch":"epoch","id":"a","data":"ls"}', 'input {"expectedEpoch":"epoch","id":"b","data":"other"}']);
  pending.shift()!();
  await settle();
  assert.deepEqual(take(), ['input {"expectedEpoch":"epoch","id":"a","data":" -la\\r"}']);
  pending.splice(0).forEach(resolve => resolve());
  await settle();

  // More than a call takes goes in pieces.
  hub.input("a", "x".repeat(inputChunk + 5));
  await settle();
  assert.equal(take().length, 1);
  pending.shift()!();
  await settle();
  assert.deepEqual(take(), [`input {"expectedEpoch":"epoch","id":"a","data":"xxxxx"}`]);
  pending.shift()!();
  await settle();

  // A terminal that takes nothing more drops what waits for it.
  hub.input("a", "one");
  hub.input("a", "two");
  await settle();
  fail();
  pending.shift()!();
  await settle();
  assert.equal(take().length, 1);
  assert.equal(pending.length, 0);

  hub.resize("a", 80, 24);
  hub.resize("a", 100, 30);
  hub.resize("a", 120, 40);
  await settle();
  assert.deepEqual(take(), ['resize {"expectedEpoch":"epoch","id":"a","columns":80,"rows":24}']);
  pending.shift()!();
  await settle();
  assert.deepEqual(take(), ['resize {"expectedEpoch":"epoch","id":"a","columns":120,"rows":40}']);
  pending.shift()!();
  stop();
});

test("a terminal the page creates is listed at once; a host asks for the tab of one to be shown", async () => {
  const { hub, watches, take } = fixture();
  assert.deepEqual(await hub.create("p", null), { status: "unavailable", terminal: null });
  assert.equal(await hub.rename("a", "t"), "unavailable");
  const stop = hub.connect("epoch");
  await settle();
  const created = await hub.create("p", "s", false);
  assert.equal(created.terminal?.id, "created");
  assert.deepEqual(hub.list().map(terminal => terminal.id), ["created"]);
  assert.deepEqual(take(), ['create {"expectedEpoch":"epoch","projectId":"p","sessionId":"s","profile":null,"columns":null,"rows":null,"integration":false}']);
  await hub.create("p", null);
  assert.deepEqual(hub.list().map(terminal => terminal.id), ["created"], "Listed once.");
  assert.ok(take()[0].includes('"integration":true'));
  // The shell that was chosen, by the name the host gave it.
  await hub.create(null, null, true, "git-bash");
  assert.deepEqual(take(), ['create {"expectedEpoch":"epoch","projectId":null,"sessionId":null,"profile":"git-bash","columns":null,"rows":null,"integration":true}']);
  assert.equal(await hub.rename("created", "build"), "ok");
  hub.close("created");
  assert.deepEqual(take(), ['rename {"expectedEpoch":"epoch","id":"created","title":"build"}', 'close {"expectedEpoch":"epoch","id":"created"}']);

  const revealed: string[] = [];
  const forget = hub.onReveal(id => revealed.push(id));
  watches[0].push(event("show", { id: "created" }));
  watches[0].push(event("show"));
  await settle();
  assert.deepEqual(revealed, ["created"]);
  forget();
  watches[0].push(event("show", { id: "created" }));
  await settle();
  assert.deepEqual(revealed, ["created"]);
  stop();
});
