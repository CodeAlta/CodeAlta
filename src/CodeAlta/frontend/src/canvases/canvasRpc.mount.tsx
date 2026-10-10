// Disposable page; the production tab of a canvas whose script calls its plugin, over a hub whose host the test plays.
import { StrictMode, createElement } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import type { CanvasEvent } from "#neoastra";
import { canvasTab, type FileTab } from "../fileTabs";
import { CanvasPanel } from "./CanvasPanel";
import { createCanvasHub, type CanvasApi } from "./canvasHub";
import { useRpc, useStream } from "../pluginScript/codealta";
import { useAlta } from "../pluginScript/PluginScript";
import type { ScriptLoader } from "../pluginScript/scriptModule";

const root = createRoot(document.getElementById("root")!);
const state = { opens: 0, closes: 0, batches: [] as string[][], acks: [] as number[], connection: "", connections: 0, instances: [] as (string | null)[],
  /** What the host answers to a script that asks for its connection, and the times it was asked. */
  rpcOpenStatus: "ok", rpcOpens: 0 };
const waiting: ((result: IteratorResult<CanvasEvent>) => void)[] = [];
const queued: CanvasEvent[] = [];
const blank = { actions: null, canvasId: null, focus: false, html: null, icon: null, instanceId: "instance-1", key: null, package: null, pluginKey: null, projectId: null, revision: null,
  script: null, scriptProblem: null, sessionId: null, spaceId: null, state: null, statusText: null, title: null, connection: null, frames: null, reason: null };
const push = (event: Partial<CanvasEvent> & { kind: string }) => {
  const full = { ...blank, ...event } as CanvasEvent;
  const next = waiting.shift();
  if (next) next({ done: false, value: full }); else queued.push(full);
};

// The plugin: `echo` answers with its input, `fail` with an error of its own, `count` is a stream that waits for acknowledgements.
const stream = { channel: "", sent: 0, acknowledged: 0, total: 0 };
const credits = 2;
const reply = (...frames: unknown[]) => queueMicrotask(() => push({ kind: "rpc", connection: state.connection, frames: frames.map(frame => JSON.stringify(frame)) }));
const pump = () => {
  while (stream.sent < stream.total && stream.sent - stream.acknowledged < credits) {
    stream.sent++;
    reply({ neoastra: 1, kind: "channel_item", channel: stream.channel, sequence: stream.sent, value: stream.sent });
  }
  if (stream.sent === stream.total && stream.acknowledged === stream.total) reply({ neoastra: 1, kind: "channel_complete", channel: stream.channel });
};

const api: CanvasApi = {
  list: async () => ({ status: "ok", canvases: [] }),
  open: async () => { state.opens++; return { status: "ok", instanceId: "instance-1", title: "Board", statusText: null, html: "<p>skeleton</p>", actions: false, revision: 1, package: "plugin:global:board", icon: "layers", iconData: null, script: (fixture.script), scriptProblem: null, input: null }; },
  visible: async () => ({ status: "ok" }),
  close: async () => ({ status: "ok" }),
  closeSpace: async () => ({ status: "ok" }),
  action: async () => ({ status: "ok", html: null, closed: false, revision: 0 }),
  describe: async () => ({ status: "ok", markdown: null }),
  rpcOpen: async () => {
    state.rpcOpens++;
    if (state.rpcOpenStatus !== "ok") return { status: state.rpcOpenStatus, connection: null, maximumFrameBytes: 0 };
    state.connection = `c${++state.connections}`;
    return { status: "ok", connection: state.connection, maximumFrameBytes: 1 << 20 };
  },
  rpcSend: async request => {
    if (request.connection !== state.connection) return { status: "closed" };
    state.batches.push([...request.frames ?? []]);
    for (const text of request.frames ?? []) {
      const frame = JSON.parse(text) as Record<string, unknown>;
      if (frame.kind === "invoke" && frame.command === "echo") reply({ neoastra: 1, kind: "result", id: frame.id, ok: true, value: { echo: frame.args } });
      else if (frame.kind === "invoke" && frame.command === "fail") reply({ neoastra: 1, kind: "result", id: frame.id, ok: false, error: { code: "not_found", message: "No such board.", retryable: false } });
      else if (frame.kind === "invoke" && frame.command === "count") {
        Object.assign(stream, { channel: `ch${state.connections}`, sent: 0, acknowledged: 0, total: 50 });
        reply({ neoastra: 1, kind: "result", id: frame.id, ok: true, value: { channel: stream.channel } });
        queueMicrotask(pump);
      } else if (frame.kind === "channel_ack") { state.acks.push(Number(frame.sequence)); stream.acknowledged = Math.max(stream.acknowledged, Number(frame.sequence)); pump(); }
    }
    return { status: "ok" };
  },
  rpcClose: async () => { state.closes++; return { status: "ok" }; },
  watch: async () => ({ [Symbol.asyncIterator]: () => ({
    next: () => new Promise<IteratorResult<CanvasEvent>>(resolve => { const ready = queued.shift(); if (ready) resolve({ done: false, value: ready }); else waiting.push(resolve); }),
    return: async () => ({ done: true as const, value: undefined }),
  }) }),
};

// The script: a call, a stream and an error, shown as text.
const Board = () => {
  const alta = useAlta();
  const call = useRpc<{ echo: { n: number } }>("echo", { n: 1 });
  const failed = useRpc("fail");
  const live = useStream<number>("count");
  return createElement("div", { className: "rpc-board", "data-generation": String(alta.rpc.generation.value) },
    createElement("p", { className: "call" }, call.loading ? "loading" : call.error ? `error ${call.error.message}` : `echo ${call.data?.echo.n}`),
    createElement("p", { className: "failed" }, failed.error ? `${(failed.error as { code?: string }).code}: ${failed.error.message}` : failed.loading ? "loading" : "no error"),
    createElement("p", { className: "stream" }, `latest ${live.latest ?? "none"} of ${live.items.length}`));
};
const loadScript: ScriptLoader = async () => ({ default: Board });

const hub = createCanvasHub(api);
void hub.connect("epoch");
const tab: FileTab = canvasTab({ pluginKey: "builtin:board", canvasId: "board", key: "k" }, { title: "Board", icon: "layers", plugin: "plugin:global:board" });

const fixture = {
  state, script: "/plugin/k/one/board.js" as string | null, push,
  /** Under StrictMode, as in the application: React then runs each effect of a new component twice. */
  render(options: { visible?: boolean } = {}) {
    flushSync(() => root.render(createElement(StrictMode, null, createElement(CanvasPanel, {
      tab, spaceId: "work", hub, visible: options.visible ?? true, active: true, onActivate: () => { }, onLook: () => { }, onInstance: instance => { state.instances.push(instance); },
      onClose: () => { }, onOpenSource: () => { }, control: null, loadScript,
    }))));
  },
};
(window as unknown as { rpcFixture: typeof fixture }).rpcFixture = fixture;
