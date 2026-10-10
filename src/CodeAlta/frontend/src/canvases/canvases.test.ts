import assert from "node:assert/strict";
import test from "node:test";
import type { CanvasEvent, PluginsEntry } from "#neoastra";
import { canvasView, refusedPhase } from "./CanvasPanel";
import { createCanvasHub, readClosedInstance, readInstanceEvent, readOpenRequest, retainedLimit, type CanvasApi, type CanvasInstanceEvent, type CanvasOpenRequest } from "./canvasHub";
import { createCanvasPluginControl, readPluginFolder, type CanvasPluginApi } from "./canvasPlugin";

const event = (kind: string, fields: Partial<CanvasEvent> = {}): CanvasEvent => ({ kind, actions: null, canvasId: null, focus: false, html: null, icon: null, instanceId: null, key: null,
  package: null, pluginKey: null, projectId: null, revision: null, connection: null, frames: null, reason: null, script: null, scriptProblem: null, sessionId: null, spaceId: null, state: null, statusText: null, title: null, ...fields });
const wait = (milliseconds = 10) => new Promise(resolve => setTimeout(resolve, milliseconds));

/** A host the test plays: the events it sends on its channel, and what the page asked of it. */
function host() {
  const calls: string[] = [];
  const queued: CanvasEvent[] = [];
  const waiting: ((result: IteratorResult<CanvasEvent>) => void)[] = [];
  let channels = 0;
  const api: CanvasApi = {
    list: async request => { calls.push(`list:${request.expectedEpoch}`); return { status: "ok", canvases: [{ pluginKey: "k", plugin: "Plugin", package: null, id: "board", title: "Board", description: null, icon: null, scope: "Application", input: false, actions: 0, describes: false, iconData: null }] }; },
    open: async request => { calls.push(`open:${request.canvasId}:${request.visible}`); return { status: "ok", instanceId: "i1", title: "Board", statusText: null, html: "<p>x</p>", actions: true, revision: 1, package: null, icon: null, iconData: null, script: null, scriptProblem: null, input: null }; },
    visible: async request => { calls.push(`visible:${request.instanceId}:${request.visible}`); return { status: "ok" }; },
    close: async request => { calls.push(`close:${request.instanceId}`); return { status: "ok" }; },
    closeSpace: async request => { calls.push(`closeSpace:${request.spaceId}`); return { status: "ok" }; },
    action: async request => { calls.push(`action:${request.action}`); return { status: "ok", html: null, closed: false, revision: 0 }; },
    describe: async () => ({ status: "ok", markdown: null }),
    rpcOpen: async () => ({ status: "unavailable", connection: null, maximumFrameBytes: 0 }),
    rpcSend: async () => ({ status: "closed" }),
    rpcClose: async () => ({ status: "ok" }),
    watch: async (_request, options) => {
      channels++;
      return { [Symbol.asyncIterator]: () => ({
        next: () => new Promise<IteratorResult<CanvasEvent>>(resolve => {
          const ready = queued.shift();
          if (ready) resolve({ done: false, value: ready }); else waiting.push(resolve);
          options?.signal?.addEventListener("abort", () => resolve({ done: true, value: undefined }), { once: true });
        }),
        return: async () => ({ done: true as const, value: undefined }),
      }) };
    },
  };
  return {
    api, calls, get channels() { return channels; },
    push(value: CanvasEvent) { const next = waiting.shift(); if (next) next({ done: false, value }); else queued.push(value); },
    end() { for (const next of waiting.splice(0)) next({ done: true, value: undefined }); },
  };
}
/** Timers the test runs by hand: the page asks again after a delay. */
function timers() {
  const pending = new Map<number, () => void>();
  let next = 0;
  return { set: (run: () => void) => { pending.set(++next, run); return next; }, clear: (timer: unknown) => { pending.delete(timer as number); },
    get count() { return pending.size; }, run() { for (const [id, run] of [...pending]) { pending.delete(id); run(); } } };
}

test("an open event is a request for a tab when it is well formed, and nothing otherwise", () => {
  const open = event("open", { pluginKey: "builtin:board", canvasId: "board", spaceId: "work", projectId: "p", sessionId: "s", key: "k", package: "plugin:global:board", focus: true, title: "Board", icon: "list-checks" });
  assert.deepEqual(readOpenRequest(open), { pluginKey: "builtin:board", canvasId: "board", spaceId: "work", projectId: "p", sessionId: "s", key: "k", plugin: "plugin:global:board", focus: true, title: "Board", icon: "list-checks" });
  assert.deepEqual(readOpenRequest(event("open", { pluginKey: "k", canvasId: "c" })),
    { pluginKey: "k", canvasId: "c", spaceId: null, projectId: null, sessionId: null, key: null, plugin: null, focus: false, title: null, icon: null });
  for (const bad of [{ kind: "update" }, { pluginKey: null }, { pluginKey: "" }, { pluginKey: "x\ny" }, { canvasId: null }, { canvasId: "bad id" }, { canvasId: "x".repeat(65) },
    { projectId: "x".repeat(257) }, { sessionId: "a\u0000" }, { key: "x".repeat(129) }, { spaceId: "x".repeat(257) }]) {
    assert.equal(readOpenRequest({ ...open, ...bad }), null, JSON.stringify(bad));
  }
});

test("an event about an instance is read for what it says, and an oversized or malformed one is not an update", () => {
  assert.deepEqual(readInstanceEvent(event("closed", { instanceId: "i" })), { instanceId: "i", event: { kind: "closed" } });
  assert.deepEqual(readInstanceEvent(event("state", { instanceId: "i", state: "plugin_stopped" })), { instanceId: "i", event: { kind: "state", state: "plugin_stopped" } });
  assert.deepEqual(readInstanceEvent(event("update", { instanceId: "i", revision: 3, html: "<p>x</p>", statusText: "" })),
    { instanceId: "i", event: { kind: "update", revision: 3, html: "<p>x</p>", title: null, statusText: "", actions: null, state: null, script: null, scriptProblem: null } });
  // The script of a reloaded plugin travels with the update: its path, or the empty text that says there is none now.
  const reloaded = readInstanceEvent(event("update", { instanceId: "i", revision: 4, script: "/plugin/k/s/ui/board.js", scriptProblem: "" }))!.event as { script: string | null; scriptProblem: string | null };
  assert.deepEqual([reloaded.script, reloaded.scriptProblem], ["/plugin/k/s/ui/board.js", ""]);
  assert.equal((readInstanceEvent(event("update", { instanceId: "i", revision: 4, script: "x".repeat(2049) }))!.event as { script: string | null }).script, null, "a path that is too long says nothing");
  assert.equal(readInstanceEvent(event("update", { instanceId: "i", revision: 3, html: "x".repeat(256 * 1024 + 1) }))!.event.kind, "update");
  assert.equal((readInstanceEvent(event("update", { instanceId: "i", revision: 3, html: "x".repeat(256 * 1024 + 1) }))!.event as { html: string | null }).html, null, "a fragment over the limit is no content");
  for (const bad of [event("update", { instanceId: null, revision: 1 }), event("update", { instanceId: "i" }), event("update", { instanceId: "i", revision: 1.5 }),
    event("state", { instanceId: "i" }), event("closed", { instanceId: "x\ny" }), event("open", { instanceId: "i" }), event("other", { instanceId: "i", revision: 1 })]) {
    assert.equal(readInstanceEvent(bad), null, JSON.stringify(bad));
  }
});

test("the script of a tab follows its plugin: set when it opens, replaced when the plugin is reloaded, and held back by the tab until it is shown", () => {
  const opened = canvasView(canvasView({ phase: "loading", instanceId: null, revision: 0, htmlRevision: 0, html: "", title: null, statusText: null, actions: false, script: null, scriptProblem: null, input: null }, { kind: "opening" }),
    { kind: "opened", instanceId: "i", revision: 1, html: "<p>a</p>", title: "Board", statusText: null, actions: false, script: "/plugin/k/one/ui/board.js", scriptProblem: null, input: "{\"a\":1}" });
  assert.deepEqual([opened.script, opened.scriptProblem, opened.input], ["/plugin/k/one/ui/board.js", null, "{\"a\":1}"]);
  const update = (fields: Partial<Extract<CanvasInstanceEvent, { kind: "update" }>>): CanvasInstanceEvent =>
    ({ kind: "update", revision: 2, html: null, title: null, statusText: null, actions: null, state: null, script: null, scriptProblem: null, ...fields });
  assert.equal(canvasView(opened, { kind: "event", event: update({ html: "<p>b</p>" }) }).script, "/plugin/k/one/ui/board.js", "a push of the fragment keeps the script");
  const reloaded = canvasView(opened, { kind: "event", event: update({ revision: 3, script: "/plugin/k/two/ui/board.js", scriptProblem: "" }) });
  assert.equal(reloaded.script, "/plugin/k/two/ui/board.js");
  assert.equal(canvasView(reloaded, { kind: "event", event: update({ revision: 4, script: "", scriptProblem: "The script of the canvas could not be found." }) }).script, null, "a plugin that dropped its script has none");
  assert.equal(canvasView(reloaded, { kind: "event", event: update({ revision: 4, script: "", scriptProblem: "The script of the canvas could not be found." }) }).scriptProblem, "The script of the canvas could not be found.");
  assert.equal(canvasView(opened, { kind: "event", event: { kind: "state", state: "plugin_stopped" } }).script, null, "a plugin that stopped shows its placeholder, not its script");
});

test("a tab follows what the plugin sends: its content, its title and status, and where it is not running", () => {
  const opened = canvasView(canvasView({ phase: "loading", instanceId: null, revision: 0, htmlRevision: 0, html: "", title: null, statusText: null, actions: false, script: null, scriptProblem: null, input: null }, { kind: "opening" }),
    { kind: "opened", instanceId: "i", revision: 2, html: "<p>a</p>", title: "Board", statusText: null, actions: true, script: null, scriptProblem: null, input: null });
  assert.deepEqual(opened, { phase: "ready", instanceId: "i", revision: 2, htmlRevision: 2, html: "<p>a</p>", title: "Board", statusText: null, actions: true, script: null, scriptProblem: null, input: null });
  const update = (fields: Partial<Extract<CanvasInstanceEvent, { kind: "update" }>>): CanvasInstanceEvent =>
    ({ kind: "update", revision: 3, html: null, title: null, statusText: null, actions: null, state: null, script: null, scriptProblem: null, ...fields });
  // Only what the event says changes; an event that is not newer is dropped.
  const pushed = canvasView(opened, { kind: "event", event: update({ html: "<p>b</p>", statusText: "3 of 8" }) });
  assert.deepEqual(pushed, { ...opened, revision: 3, htmlRevision: 3, html: "<p>b</p>", statusText: "3 of 8" });
  assert.equal(canvasView(pushed, { kind: "event", event: update({ revision: 2, html: "<p>old</p>" }) }), pushed);
  assert.equal(canvasView(pushed, { kind: "event", event: update({ revision: 4, statusText: "" }) }).statusText, null, "a blank status clears it");
  // The answer to an action replaces the content and says which revision it is: the fragment of a push the plugin made before the action and that arrives after is dropped.
  const answered = canvasView(pushed, { kind: "html", html: "<p>answer</p>", revision: 5 });
  assert.deepEqual([answered.html, answered.htmlRevision], ["<p>answer</p>", 5]);
  assert.equal(canvasView(answered, { kind: "event", event: update({ revision: 4, html: "<p>before the action</p>" }) }).html, "<p>answer</p>");
  // What such a push says besides its fragment is not older than what the tab has: the answer says nothing of the title or the status.
  assert.deepEqual(((late: ReturnType<typeof canvasView>) => [late.html, late.title, late.statusText])(canvasView(answered, { kind: "event", event: update({ revision: 4, html: "<p>before the action</p>", title: "Renamed", statusText: "4 of 8" }) })),
    ["<p>answer</p>", "Renamed", "4 of 8"]);
  // An answer that arrives after a newer push of a fragment is the one that is dropped.
  const newer = canvasView(pushed, { kind: "event", event: update({ revision: 6, html: "<p>after the action</p>" }) });
  assert.equal(canvasView(newer, { kind: "html", html: "<p>answer</p>", revision: 5 }), newer);
  // A push that says nothing of the fragment (a title, a status) is no newer content: the answer it overtook is still shown.
  const renamed = canvasView(pushed, { kind: "event", event: update({ revision: 6, title: "Renamed" }) });
  const afterRename = canvasView(renamed, { kind: "html", html: "<p>answer</p>", revision: 5 });
  assert.deepEqual([afterRename.html, afterRename.title, afterRename.htmlRevision], ["<p>answer</p>", "Renamed", 5]);
  assert.equal(canvasView(afterRename, { kind: "event", event: update({ revision: 7, html: "<p>later</p>" }) }).html, "<p>later</p>", "and a later push of a fragment replaces it");
  // The answer of the action before that one, which comes last, is the older content.
  assert.equal(canvasView(afterRename, { kind: "html", html: "<p>the action before</p>", revision: 4 }), afterRename);
  // The plugin stops: the tab keeps what identifies it and shows the placeholder; its next version brings the content back.
  const stopped = canvasView(pushed, { kind: "event", event: { kind: "state", state: "plugin_stopped" } });
  assert.deepEqual([stopped.phase, stopped.instanceId, stopped.html, stopped.title], ["stopped", "i", "", "Board"]);
  assert.equal(canvasView(stopped, { kind: "event", event: update({ revision: 5, html: "<p>new</p>", state: "ready", actions: true }) }).phase, "ready");
  assert.equal(canvasView(stopped, { kind: "event", event: update({ revision: 5, html: "<p>new</p>" }) }), stopped, "an update that does not say the plugin is back leaves the placeholder");
  assert.equal(canvasView(pushed, { kind: "event", event: { kind: "state", state: "ready" } }), pushed);
  assert.equal(canvasView(stopped, { kind: "html", html: "x", revision: 9 }), stopped);
  // What the host refuses says why.
  assert.deepEqual(["plugin_stopped", "unknown_canvas", "failed", "limit", "unavailable", "stale_epoch"].map(refusedPhase), ["stopped", "missing", "failed", "failed", "unavailable", "unavailable"]);
  assert.equal(canvasView(opened, { kind: "refused", phase: "failed" }).phase, "failed");
  assert.equal(canvasView(opened, { kind: "opening" }), opened, "a tab that shows its content keeps it while it asks again");
});

test("the requests of a plugin for a tab wait for the window, and the window takes them in order", async () => {
  const played = host();
  const hub = createCanvasHub(played.api, timers());
  const disconnect = hub.connect("epoch");
  const request = (key: string) => event("open", { pluginKey: "k", canvasId: "board", key, focus: true });
  for (let index = 0; index < 20; index++) played.push(request(`k${index}`));
  await wait(40);
  const taken: CanvasOpenRequest[] = [];

  hub.onOpenRequest(value => taken.push(value));

  assert.equal(taken.length, 16, "the window that is not ready keeps the newest sixteen");
  assert.equal(taken[0].key, "k4");
  played.push(request("later"));
  await wait(20);
  assert.equal(taken.at(-1)!.key, "later");
  hub.onOpenRequest(null);
  played.push(event("open", { pluginKey: "k", canvasId: "board", key: "bad key\n" }));
  played.push(request("kept"));
  await wait(20);
  assert.equal(taken.length, 17);
  const again: string[] = [];
  hub.onOpenRequest(value => again.push(value.key!));
  assert.deepEqual(again, ["kept"], "a request that is not well formed is not kept");
  disconnect();
});

test("what a plugin sent to an instance before its tab listened is given to the tab, newer than what it has", async () => {
  const played = host();
  const hub = createCanvasHub(played.api, timers());
  const disconnect = hub.connect("epoch");
  played.push(event("update", { instanceId: "i", revision: 2, html: "<p>2</p>" }));
  played.push(event("update", { instanceId: "i", revision: 3, title: "Renamed", statusText: "3 of 8" }));
  played.push(event("update", { instanceId: "other", revision: 9, html: "<p>other</p>" }));
  played.push(event("closed", { instanceId: "gone" }));
  await wait(40);

  const received: CanvasInstanceEvent[] = [];
  const detach = hub.attach("i", 1, value => received.push(value));
  assert.deepEqual(received, [{ kind: "update", revision: 3, html: "<p>2</p>", title: "Renamed", statusText: "3 of 8", actions: null, state: null, script: null, scriptProblem: null }], "the two updates are one");
  // Then what comes goes to the listener.
  played.push(event("update", { instanceId: "i", revision: 4, html: "<p>4</p>" }));
  await wait(20);
  assert.equal(received.length, 2);
  assert.equal((received[1] as { html: string }).html, "<p>4</p>");
  detach();
  // A tab that already has what was sent is given nothing, but is told of a close.
  const known: CanvasInstanceEvent[] = [];
  hub.attach("other", 9, value => known.push(value));
  assert.deepEqual(known, []);
  const closing: CanvasInstanceEvent[] = [];
  hub.attach("gone", 0, value => closing.push(value));
  assert.deepEqual(closing, [{ kind: "closed" }]);
  disconnect();
});

test("a tab is told what came after it asked for its instance, in the order it came, and nothing that the answer of the host is newer than", async () => {
  const played = host();
  let answer: (() => void) | null = null;
  const open = played.api.open;
  const api: CanvasApi = { ...played.api, open: async (request, options) => { await new Promise<void>(resolve => { answer = resolve; }); return open(request, options); } };
  const hub = createCanvasHub(api, timers());
  const disconnect = hub.connect("epoch");
  const ask = () => hub.open({ pluginKey: "k", canvasId: "board", spaceId: null, projectId: null, sessionId: null, key: null, visible: true });
  const told = (since: number) => { const received: CanvasInstanceEvent[] = []; hub.attach("i1", since, value => received.push(value))(); return received; };

  // The plugin stopped while the tab of the instance was not listening (it waits, and asks again when plugins change); then it runs again.
  played.push(event("state", { instanceId: "i1", state: "plugin_stopped" }));
  await wait(40);
  let opening = ask();
  await wait(10);
  answer!();
  assert.deepEqual(told((await opening).revision), [], "the instance the host just opened is not said to be stopped");

  // What comes while the host answers is not older than its answer: the plugin closes the instance at once.
  opening = ask();
  await wait(10);
  played.push(event("closed", { instanceId: "i1" }));
  await wait(40);
  answer!();
  assert.deepEqual(told((await opening).revision), [{ kind: "closed" }]);

  // The plugin stops and comes back while the host answers: the tab ends on what came last.
  opening = ask();
  await wait(10);
  played.push(event("state", { instanceId: "i1", state: "plugin_stopped" }));
  played.push(event("update", { instanceId: "i1", revision: 7, html: "<p>back</p>", state: "ready" }));
  await wait(40);
  answer!();
  const back = told((await opening).revision);
  assert.deepEqual(back.map(value => value.kind), ["update"], "the state that the plugin left is moot");
  // And the other way round: an update, then the plugin stops.
  opening = ask();
  await wait(10);
  played.push(event("update", { instanceId: "i1", revision: 8, html: "<p>newer</p>" }));
  played.push(event("state", { instanceId: "i1", state: "plugin_stopped" }));
  await wait(40);
  answer!();
  assert.deepEqual(told((await opening).revision), [{ kind: "state", state: "plugin_stopped" }]);
  disconnect();
});

test("a closed event names the tab of its instance when it is well formed", () => {
  const closed = event("closed", { instanceId: "i", pluginKey: "builtin:board", canvasId: "board", spaceId: "work", projectId: "p", sessionId: "s", key: "k" });
  assert.deepEqual(readClosedInstance(closed), { instanceId: "i", pluginKey: "builtin:board", canvasId: "board", spaceId: "work", projectId: "p", sessionId: "s", key: "k" });
  assert.deepEqual(readClosedInstance(event("closed", { instanceId: "i", pluginKey: "k", canvasId: "c" })),
    { instanceId: "i", pluginKey: "k", canvasId: "c", spaceId: null, projectId: null, sessionId: null, key: null });
  for (const bad of [{ kind: "open" }, { instanceId: null }, { pluginKey: null }, { canvasId: "bad id" }, { spaceId: "x".repeat(257) }, { sessionId: "a\u0000" }, { key: "x".repeat(129) }]) {
    assert.equal(readClosedInstance({ ...closed, ...bad }), null, JSON.stringify(bad));
  }
});

test("an instance that a plugin closes takes its tab away, whether the tab listens, is asking for it, or is in a space that is not shown", async () => {
  const played = host();
  let answer: (() => void) | null = null;
  const open = played.api.open;
  const api: CanvasApi = { ...played.api, open: async (request, options) => { await new Promise<void>(resolve => { answer = resolve; }); return open(request, options); } };
  const hub = createCanvasHub(api, timers());
  const disconnect = hub.connect("epoch");
  const seen: string[] = [];
  hub.onOpenRequest(request => seen.push(`open:${request.spaceId}`), closed => seen.push(`closed:${closed.instanceId}:${closed.spaceId}`));
  const closed = (instanceId: string, spaceId: string) => event("closed", { instanceId, pluginKey: "k", canvasId: "board", spaceId });
  const ask = () => hub.open({ pluginKey: "k", canvasId: "board", spaceId: "shown", projectId: null, sessionId: null, key: null, visible: true });
  const told = (since: number) => { const received: CanvasInstanceEvent[] = []; hub.attach("i1", since, value => received.push(value))(); return received; };

  // The tab is in a space that the window does not show: nothing listens to its instance, and the window is told which tab it was.
  played.push(closed("i1", "other"));
  await wait(40);
  assert.deepEqual(seen, ["closed:i1:other"]);
  // The tab the user opens afterwards is a new one: what closed before it asked is not said to it.
  let opening = ask();
  await wait(10);
  answer!();
  assert.deepEqual(told((await opening).revision), [], "a tab that asks after the close keeps the instance the host just gave it");

  // The tab listens: it is told, and the window is not, so that the tab is closed once.
  const heard: CanvasInstanceEvent[] = [];
  const detach = hub.attach("i1", 1, value => heard.push(value));
  played.push(closed("i1", "shown"));
  await wait(40);
  assert.deepEqual(heard, [{ kind: "closed" }]);
  assert.deepEqual(seen, ["closed:i1:other"]);
  detach();

  // The tab asked for its instance and the host has not answered: the window is told at once, and so is the tab if it is still there when the answer comes.
  opening = ask();
  await wait(10);
  played.push(closed("i1", "shown"));
  await wait(40);
  assert.deepEqual(seen, ["closed:i1:other", "closed:i1:shown"]);
  answer!();
  assert.deepEqual(told((await opening).revision), [{ kind: "closed" }]);

  // A close that does not name its tab is kept for the tab alone.
  played.push(event("closed", { instanceId: "i2" }));
  await wait(40);
  assert.equal(seen.length, 2);
  const late: CanvasInstanceEvent[] = [];
  hub.attach("i2", 0, value => late.push(value));
  assert.deepEqual(late, [{ kind: "closed" }]);
  disconnect();
});

test("a plugin that closes an instance and asks for its tab again while the tab waits for the host leaves the tab open", async () => {
  const played = host();
  let answer: (() => void) | null = null;
  const open = played.api.open;
  const api: CanvasApi = { ...played.api, open: async (request, options) => { await new Promise<void>(resolve => { answer = resolve; }); return open(request, options); } };
  const hub = createCanvasHub(api, timers());
  const disconnect = hub.connect("epoch");
  const seen: string[] = [];
  hub.onOpenRequest(request => seen.push(`open:${request.canvasId}`), closed => seen.push(`closed:${closed.instanceId}`));
  const closed = (instanceId: string) => event("closed", { instanceId, pluginKey: "k", canvasId: "board", spaceId: "shown" });
  const request = (instanceId: string | null) => event("open", { instanceId, pluginKey: "k", canvasId: "board", spaceId: "shown", focus: true });
  const ask = () => hub.open({ pluginKey: "k", canvasId: "board", spaceId: "shown", projectId: null, sessionId: null, key: null, visible: true });
  const told = (since: number) => { const received: CanvasInstanceEvent[] = []; hub.attach("i1", since, value => received.push(value))(); return received; };

  // The tab asked for its instance (the page was loaded again) and the host has not answered. The plugin closes the instance, then asks
  // for its tab: the window takes the tab away and puts it back at once, so the tab is still there when the answer comes.
  let opening = ask();
  await wait(10);
  played.push(closed("i1"));
  played.push(request("i1"));
  await wait(40);
  assert.deepEqual(seen, ["closed:i1", "open:board"], "the window hears both, in order");
  answer!();
  assert.deepEqual(told((await opening).revision), [], "the close that came before the request is not said to the tab the plugin asked for");

  // The other order closes the tab: asked for, then closed.
  opening = ask();
  await wait(10);
  played.push(request("i1"));
  played.push(closed("i1"));
  await wait(40);
  answer!();
  assert.deepEqual(told((await opening).revision), [{ kind: "closed" }]);

  // A request for another instance, or one that does not say which, leaves the close of this one.
  opening = ask();
  await wait(10);
  played.push(closed("i1"));
  played.push(request("other"));
  played.push(request(null));
  await wait(40);
  answer!();
  assert.deepEqual(told((await opening).revision), [{ kind: "closed" }]);

  // What the plugin sent that is no close stays for the tab: a request for the tab says nothing of what the instance shows.
  opening = ask();
  await wait(10);
  played.push(event("update", { instanceId: "i1", revision: 9, html: "<p>pushed</p>" }));
  played.push(request("i1"));
  await wait(40);
  answer!();
  assert.deepEqual(told((await opening).revision).map(value => value.kind), ["update"]);
  disconnect();
});

test("what plugins asked and closed before the window was ready is given to it in the order it came", async () => {
  const played = host();
  const hub = createCanvasHub(played.api, timers());
  const disconnect = hub.connect("epoch");
  const request = (key: string) => event("open", { pluginKey: "k", canvasId: "board", spaceId: "other", key, focus: true });
  const closed = (key: string) => event("closed", { instanceId: `i-${key}`, pluginKey: "k", canvasId: "board", spaceId: "other", key });
  // Asked for then closed, and closed then asked for again: the window ends with the second tab and without the first.
  for (const value of [request("a"), closed("a"), closed("b"), request("b")]) played.push(value);
  await wait(40);

  const taken: string[] = [];
  hub.onOpenRequest(value => taken.push(`open:${value.key}`), value => taken.push(`closed:${value.key}`));

  assert.deepEqual(taken, ["open:a", "closed:a", "closed:b", "open:b"]);
  // A window that does not follow the closes takes the requests alone.
  hub.onOpenRequest(null);
  for (const value of [closed("c"), request("c")]) played.push(value);
  await wait(40);
  const requests: string[] = [];
  hub.onOpenRequest(value => requests.push(value.key!));
  assert.deepEqual(requests, ["c"]);
  played.push(closed("d"));
  await wait(40);
  assert.deepEqual(requests, ["c"]);
  disconnect();
});

test("the closes kept for a window that is not ready do not take the place of the requests, and none is let go", async () => {
  const played = host();
  const hub = createCanvasHub(played.api, timers());
  const disconnect = hub.connect("epoch");
  const request = (key: string) => event("open", { pluginKey: "k", canvasId: "board", spaceId: "other", key, focus: true });
  const closed = (key: string) => event("closed", { instanceId: `i-${key}`, pluginKey: "k", canvasId: "board", spaceId: "other", key });
  // The host tells a page that watches again everything it missed at once: as many closes as it keeps instances, with the requests around them.
  for (let index = 0; index < retainedLimit; index++) { played.push(closed(`c${index}`)); if (index < 20) played.push(request(`r${index}`)); }
  // An instance that was closed twice is one tab to take away, where its last close came.
  played.push(closed("c0"));
  await wait(200);

  const taken: string[] = [];
  hub.onOpenRequest(value => taken.push(`open:${value.key}`), value => taken.push(`closed:${value.key}`));

  const closes = taken.filter(value => value.startsWith("closed:"));
  assert.deepEqual(closes, [...Array.from({ length: retainedLimit - 1 }, (_value, index) => `closed:c${index + 1}`), "closed:c0"], "every close, each instance once");
  assert.deepEqual(taken.filter(value => value.startsWith("open:")), Array.from({ length: 16 }, (_value, index) => `open:r${index + 4}`), "the newest sixteen requests, as before");
  assert.ok(taken.indexOf("closed:c7") < taken.indexOf("open:r7") && taken.indexOf("open:r7") < taken.indexOf("closed:c8"), "in the order they came");
  disconnect();
});

test("the events of instances that no tab listens to are kept within a limit", async () => {
  const played = host();
  const hub = createCanvasHub(played.api, timers());
  const disconnect = hub.connect("epoch");
  for (let index = 0; index < retainedLimit + 10; index++) played.push(event("update", { instanceId: `i${index}`, revision: 5, html: `<p>${index}</p>` }));
  await wait(80);

  const first: CanvasInstanceEvent[] = [], last: CanvasInstanceEvent[] = [];
  hub.attach("i0", 0, value => first.push(value));
  hub.attach(`i${retainedLimit + 9}`, 0, value => last.push(value));

  assert.equal(first.length, 0, "the oldest was let go");
  assert.equal(last.length, 1);
  disconnect();
});

test("the hub lists the canvases when it connects and again when the host says plugins changed, and tells the tabs that wait", async () => {
  const played = host();
  const hub = createCanvasHub(played.api, timers());
  assert.equal(hub.connected, false);
  assert.equal((await hub.open({ pluginKey: "k", canvasId: "board", spaceId: null, projectId: null, sessionId: null, key: null, visible: true })).status, "unavailable");
  assert.equal(await hub.setVisible("i", true), "unreachable");
  assert.equal((await hub.action("i", "tick", null, {})).status, "unavailable");
  await hub.close("i");
  await hub.closeSpace("s");
  assert.deepEqual(played.calls, []);

  let changes = 0, catalogs = 0;
  hub.subscribeChanges(() => changes++);
  hub.subscribeCatalog(() => catalogs++);
  const disconnect = hub.connect("epoch");
  await wait(40);
  assert.equal(hub.connected, true);
  assert.deepEqual(hub.getCatalog().map(item => item.id), ["board"]);
  assert.equal(catalogs, 1);
  assert.equal(changes, 1, "a tab that waited for the host asks again when it is reached");
  played.push(event("plugins"));
  await wait(40);
  assert.equal(changes, 2);
  assert.equal(catalogs, 2);
  assert.equal(hub.getVersion(), 2);

  assert.equal((await hub.open({ pluginKey: "k", canvasId: "board", spaceId: "work", projectId: null, sessionId: null, key: null, visible: false })).instanceId, "i1");
  assert.equal(await hub.setVisible("i1", false), "ok");
  await hub.action("i1", "tick", "v", { a: "b" });
  await hub.close("i1");
  await hub.closeSpace("work");
  assert.deepEqual(played.calls.slice(2), ["open:board:false", "visible:i1:false", "action:tick", "close:i1", "closeSpace:work"]);
  disconnect();
  assert.equal(hub.connected, false);
});

test("the hub says whether the host knows the instance a tab shows, or could not be told", async () => {
  const played = host();
  let answer: "unknown" | "throw" = "unknown";
  const api: CanvasApi = { ...played.api, visible: async () => { if (answer === "throw") throw new Error("gone"); return { status: answer }; } };
  const hub = createCanvasHub(api, timers());
  const disconnect = hub.connect("epoch");
  assert.equal(await hub.setVisible("i1", true), "unknown", "the host closed the instance to make room: the tab asks for it again");
  answer = "throw";
  assert.equal(await hub.setVisible("i1", true), "unreachable");
  disconnect();
});

test("the hub listens again after the host stopped telling, until the window is gone", async () => {
  const played = host();
  const clock = timers();
  const hub = createCanvasHub(played.api, clock);
  const disconnect = hub.connect("epoch");
  await wait(20);
  assert.equal(played.channels, 1);

  played.end();
  await wait(20);
  assert.equal(clock.count, 1, "it asks again after a delay");
  clock.run();
  await wait(20);
  assert.equal(played.channels, 2);

  disconnect();
  played.end();
  await wait(20);
  assert.equal(clock.count, 0, "a window that is gone listens no more");
});

test("the folder of a plugin is read from the id the host gave, and nothing else", () => {
  assert.deepEqual(readPluginFolder("plugin:global:notes"), { id: "plugin:global:notes", scope: "Global", projectId: null, packageId: "notes" });
  assert.deepEqual(readPluginFolder("plugin:project:abc123:notes.v2"), { id: "plugin:project:abc123:notes.v2", scope: "Project", projectId: "abc123", packageId: "notes.v2" });
  for (const bad of [null, undefined, "", "builtin:x", "plugin:global:", "plugin:global:-x", "plugin:global:a b", "plugin:project::x", "plugin:project:p:", "plugin:other:x", "plugin:global:" + "x".repeat(200)]) {
    assert.equal(readPluginFolder(bad), null, String(bad));
  }
});

test("what a tab can do about a plugin that does not run comes from the plugins the host lists", async () => {
  const entry = (fields: Partial<PluginsEntry>): PluginsEntry => ({ changed: false, description: null, enabled: true, enabledGlobal: null, enabledProject: null, errors: null, folder: "plugin:global:notes",
    id: "notes", kind: "Source", loadable: true, name: "Notes", path: "/home/.alta/plugins/notes", runtime: "stopped", runtimeMessage: null, scope: "Global", state: "Ready", ...fields });
  const reloads: unknown[] = [];
  let entries: PluginsEntry[] = [entry({})];
  let reload: { status: string; message: string | null } = { status: "ok", message: null };
  const api: CanvasPluginApi = {
    list: async request => ({ status: "ok", projectId: request.projectId, plugins: entries, omitted: 0, problems: null }),
    reload: async request => { reloads.push(request); return { status: reload.status, message: reload.message, applied: reload.status === "ok" }; },
  };

  assert.equal(createCanvasPluginControl(api, "epoch", null), null, "a built-in plugin has no folder");
  assert.equal(createCanvasPluginControl(api, null, "plugin:global:notes"), null);
  assert.equal(createCanvasPluginControl(api, "epoch", "folder:x"), null);
  const control = createCanvasPluginControl(api, "epoch", "plugin:global:notes")!;
  assert.deepEqual(await control.probe(), { state: "stopped", message: null, folder: { id: "plugin:global:notes", path: "/home/.alta/plugins/notes", name: "Notes" } });
  entries = [entry({ runtime: "failed", errors: ["plugin.cs(3,1): error CS1002"], runtimeMessage: "ignored" })];
  assert.deepEqual((await control.probe()).message, "plugin.cs(3,1): error CS1002");
  assert.equal((await control.probe()).state, "failed");
  entries = [entry({ runtime: null, enabled: false })];
  assert.equal((await control.probe()).state, "disabled");
  entries = [entry({ folder: "plugin:global:other" })];
  assert.deepEqual(await control.probe(), { state: "unknown", message: null, folder: null });

  assert.deepEqual(await control.rebuild(), { ok: true, message: null });
  reload = { status: "build_failed", message: "error CS0103" };
  assert.deepEqual(await control.rebuild(), { ok: false, message: "error CS0103" });
  assert.deepEqual(reloads[0], { expectedEpoch: "epoch", projectId: null, scope: "Global", id: "notes" });
  // A plugin of a project is asked for with its project.
  const project = createCanvasPluginControl(api, "epoch", "plugin:project:p1:notes")!;
  await project.rebuild();
  assert.deepEqual(reloads.at(-1), { expectedEpoch: "epoch", projectId: "p1", scope: "Project", id: "notes" });
});

test("the frames of a connection reach the listener of their instance in order, and every connection ends when the watch does", async () => {
  const fake = host();
  const hub = createCanvasHub(fake.api, timers());
  const heard: string[] = [];
  hub.rpc.listen("i1", { frames: (connection, frames) => heard.push(`${connection}:${frames.join("|")}`), closed: (connection, reason) => heard.push(`closed:${connection}:${reason}`) });
  assert.deepEqual(await hub.rpc.open("i1"), { status: "unreachable", connection: null, maximumFrameBytes: 0 }, "nothing to ask before the host is known: that is not an instance without calls");
  assert.equal(await hub.rpc.send("i1", "c1", ["x"]), "unreachable");

  const disconnect = hub.connect("epoch");
  await wait();
  fake.push(event("rpc", { instanceId: "i1", connection: "c1", frames: ["a", "b"] }));
  fake.push(event("rpc", { instanceId: "elsewhere", connection: "c9", frames: ["lost"] }));
  fake.push(event("rpc", { instanceId: "i1", connection: "c1", frames: ["c"] }));
  fake.push(event("rpcClosed", { instanceId: "i1", connection: "c1", reason: "replaced" }));
  fake.push(event("rpc", { instanceId: "i1", connection: "", frames: ["bad"] }));
  await wait();

  assert.deepEqual(heard, ["closed:null:watch_started", "c1:a|b", "c1:c", "closed:c1:replaced"]);
  assert.equal((await hub.rpc.open("i1")).status, "unavailable", "the host of the test has none");
  await hub.rpc.close("i1", "c1");
  // A call that the host never answers is the host that cannot be reached, not what it would have said.
  const silent = createCanvasHub({ ...fake.api, rpcOpen: async () => { throw new Error("gone"); }, rpcSend: async () => { throw new Error("gone"); } }, timers());
  const leave = silent.connect("epoch");
  assert.equal((await silent.rpc.open("i1")).status, "unreachable");
  assert.equal(await silent.rpc.send("i1", "c1", ["x"]), "unreachable");
  leave();
  fake.end();
  await wait();
  assert.equal(heard.at(-1), "closed:null:watch_ended");
  disconnect();
});

test("a tab whose call the host turned away because too many were served asks again, and gives up after a few tries", async () => {
  const played = host();
  let refusals = 2, asked = 0;
  const open = played.api.open;
  const api: CanvasApi = { ...played.api, open: async (request, options) => {
    asked++;
    if (refusals-- > 0) throw Object.assign(new Error("The RPC command concurrency limit is exhausted."), { code: "too_many_requests", retryable: true });
    return open(request, options);
  } };
  const hub = createCanvasHub(api, { set: (run) => setTimeout(run, 1), clear: timer => clearTimeout(timer as number) });
  const disconnect = hub.connect("epoch");
  const reply = await hub.open({ pluginKey: "k", canvasId: "board", spaceId: null, projectId: null, sessionId: null, key: null, visible: true });
  assert.equal(reply.status, "ok");
  assert.equal(asked, 3, "two refusals, then the instance");

  refusals = 100;
  asked = 0;
  const gone = await hub.open({ pluginKey: "k", canvasId: "board", spaceId: null, projectId: null, sessionId: null, key: null, visible: true });
  assert.equal(gone.status, "unavailable");
  assert.equal(asked, 6, "it does not insist");

  // Another failure is not retried.
  asked = 0;
  refusals = 0;
  const other: CanvasApi = { ...played.api, open: async () => { asked++; throw new Error("boom"); } };
  const second = createCanvasHub(other, { set: (run) => setTimeout(run, 1), clear: timer => clearTimeout(timer as number) });
  second.connect("epoch");
  assert.equal((await second.open({ pluginKey: "k", canvasId: "board", spaceId: null, projectId: null, sessionId: null, key: null, visible: true })).status, "unavailable");
  assert.equal(asked, 1);
  disconnect();
});
