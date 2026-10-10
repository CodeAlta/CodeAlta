// The calls of the script of a canvas to its plugin (`alta.rpc`), over the one service of the page that carries them.
//
// A plugin page gets no native bridge and the contract of the page cannot grow at run time, so each canvas instance has its own NeoAstra
// host on the .NET side, and its frames travel through `canvases.rpcSend` (in) and the `rpc` events of `canvases.watch` (out). Here the
// unchanged `NeoRpcClient` of `@neoastra/client` runs on a connection object that the page makes, so a script gets the real client: ids,
// cancellation, timeouts, typed errors, channels with acknowledgements.
import { NeoAstraClientError, NeoRpcClient, NeoRpcError, type NeoAstraConnection } from "@neoastra/client";
import { AltaError, type AltaRpc, type AltaSignal } from "../pluginScript/alta";

/**
 * The host's answer to opening a connection: `ok` with the connection, `unavailable` (the instance has no calls), `unknown` (no such instance),
 * `not_watching` (no page watches the canvases yet) or a refusal; `unreachable` when the host could not be asked.
 */
export type CanvasRpcOpened = Readonly<{ status: string; connection: string | null; maximumFrameBytes: number }>;

/** What the host sends for the connections of one instance. `connection` is null when it ends them all (the page lost the host). */
export type CanvasRpcListener = Readonly<{
  frames(connection: string, frames: readonly string[]): void;
  closed(connection: string | null, reason: string): void;
}>;

/** The page's way to the host for the calls of canvases: what `canvasHub.rpc` is. */
export type CanvasRpcCarrier = Readonly<{
  open(instanceId: string): Promise<CanvasRpcOpened>;
  /** Gives frames to the session of a connection; the answer is `ok`, `payload_too_large` or a reason the connection is over. */
  send(instanceId: string, connection: string, frames: readonly string[]): Promise<string>;
  close(instanceId: string, connection: string): Promise<void>;
  listen(instanceId: string, listener: CanvasRpcListener): () => void;
}>;

export type CanvasRpcTimers = Readonly<{ set: (run: () => void, milliseconds: number) => unknown; clear: (timer: unknown) => void }>;

/** The frames that wait to be sent are given to the host together; one call at a time, a few milliseconds apart. */
export const minimumSendGapMilliseconds = 8;
const maximumFramesPerSend = 128;
const maximumCharsPerSend = 2 * 1024 * 1024;

const defaultTimers: CanvasRpcTimers = { set: (run, milliseconds) => setTimeout(run, milliseconds), clear: timer => clearTimeout(timer as number) };
const encoder = new TextEncoder();

/** A connection the page made for `NeoRpcClient`, with what the page does to it besides: delivering frames, pausing acknowledgements, ending it. */
export type CarrierConnection = NeoAstraConnection & Readonly<{
  /** Gives the frames the host sent for this connection to the client. */
  deliver(frames: readonly string[]): void;
  /** Holds back the acknowledgements of channel items, so a stream the script reads pauses on the host's side; the other frames go on. */
  setPaused(paused: boolean): void;
  /** Ends the connection: the client fails what it has pending with a retryable `connection_closed`. */
  end(reason: string): void;
}>;

/** What a connection needs to be made. */
export type CarrierConnectionOptions = Readonly<{
  carrier: CanvasRpcCarrier;
  instanceId: string;
  connection: string;
  maximumFrameBytes: number;
  timers?: CanvasRpcTimers;
  now?: () => number;
  minimumGapMilliseconds?: number;
}>;

const isRecord = (value: unknown): value is Record<string, unknown> => typeof value === "object" && value !== null && !Array.isArray(value);

/**
 * Makes the connection object that `NeoRpcClient` sends its frames on. It batches what the client writes (a stream's acknowledgements
 * are merged into the last one of each channel), keeps one call to the host in flight at a time so the order is the client's, and ends
 * when the host says so or refuses a send.
 */
export function createCarrierConnection(options: CarrierConnectionOptions): CarrierConnection {
  const { carrier, instanceId, connection: id, maximumFrameBytes } = options;
  const timers = options.timers ?? defaultTimers;
  const now = options.now ?? (() => performance.now());
  const gap = options.minimumGapMilliseconds ?? minimumSendGapMilliseconds;
  const controller = new AbortController();
  let state: NeoAstraConnection["state"] = "connected";
  let reason: string | undefined;
  let receive: ((frame: Readonly<Record<string, unknown>>) => void) | null = null;
  let paused = false;
  let flushing = false;
  let timer: unknown = null;
  let lastSend = -Infinity;
  const queue: string[] = [];
  const acknowledgements = new Map<string, number>();
  const held = new Map<string, number>();

  function schedule() {
    if (state !== "connected" || flushing || timer !== null) return;
    if (queue.length === 0 && acknowledgements.size === 0) return;
    const wait = Math.max(0, lastSend + gap - now());
    if (wait === 0) { timer = 0; queueMicrotask(() => { timer = null; void flush(); }); }
    else timer = timers.set(() => { timer = null; void flush(); }, wait);
  }

  function takeBatch(): string[] {
    const batch: string[] = [];
    let chars = 0;
    while (queue.length > 0 && batch.length < maximumFramesPerSend && (batch.length === 0 || chars + queue[0].length <= maximumCharsPerSend)) {
      chars += queue[0].length;
      batch.push(queue.shift()!);
    }
    // The acknowledgements go after the frames: the last one of a channel frees every item before it.
    if (queue.length === 0) {
      for (const [channel, sequence] of acknowledgements) batch.push(JSON.stringify({ neoastra: 1, kind: "channel_ack", channel, sequence }));
      acknowledgements.clear();
    }
    return batch;
  }

  async function flush() {
    if (state !== "connected" || flushing) return;
    const batch = takeBatch();
    if (batch.length === 0) return;
    flushing = true;
    lastSend = now();
    let status = "unreachable";
    try { status = await carrier.send(instanceId, id, batch); } catch { /* The host is gone: the connection ends below. */ }
    flushing = false;
    if (status !== "ok" && status !== "payload_too_large") { end(status === "closed" ? "host_closed" : status); return; }
    schedule();
  }

  function end(why: string) {
    if (state === "closed") return;
    state = "closed";
    reason = why;
    queue.length = 0;
    acknowledgements.clear();
    held.clear();
    if (timer !== null && timer !== 0) timers.clear(timer);
    timer = null;
    controller.abort(new NeoAstraClientError("connection_closed", "The connection to the plugin closed.", true));
    // The host told us, or cannot be told: otherwise it frees the session.
    if (why === "client_close") void carrier.close(instanceId, id).catch(() => { });
  }

  function rejectLocally(frame: Readonly<Record<string, unknown>>, code: string, message: string) {
    // The call is answered here, as the host would: the frame never left the page.
    if (frame.kind !== "invoke" || typeof frame.id !== "string") return;
    const answer = Object.freeze({ neoastra: 1, kind: "result", id: frame.id, ok: false, error: { code, message, retryable: false } });
    queueMicrotask(() => { try { receive?.(answer); } catch { /* A handler that throws has nothing more to hear. */ } });
  }

  const connection: CarrierConnection = {
    runtimeInfo: Object.freeze({ available: true as const, protocolMajor: 1, protocolMinor: 0, negotiatedFeatures: Object.freeze(["invoke", "cancel", "events"]),
      viewLabel: `canvas:${instanceId}`, documentSessionId: id, platform: "windows" as const, backend: "webview2" as const, wholeViewTrust: false }),
    get state() { return state; },
    closed: controller.signal,
    get closeReason() { return reason; },
    hasFeature: feature => feature === "invoke" || feature === "cancel" || feature === "events",
    send(frame: unknown) {
      if (state !== "connected") throw new NeoAstraClientError("connection_closed", "The connection to the plugin is closed.", true);
      if (!isRecord(frame) || frame.neoastra !== 1 || typeof frame.kind !== "string") throw new NeoAstraClientError("invalid_frame", "A frame must be an object with the NeoAstra discriminator and a kind.");
      if (frame.kind === "channel_ack" && typeof frame.channel === "string" && typeof frame.sequence === "number") {
        const target = paused ? held : acknowledgements;
        target.set(frame.channel, Math.max(target.get(frame.channel) ?? 0, frame.sequence));
        if (!paused) schedule();
        return;
      }

      const json = JSON.stringify(frame);
      if (json.length > maximumFrameBytes || encoder.encode(json).byteLength > maximumFrameBytes) {
        rejectLocally(frame, "payload_too_large", "The request is too large to send to the plugin.");
        return;
      }

      if (frame.kind === "channel_close" && typeof frame.channel === "string") { acknowledgements.delete(frame.channel); held.delete(frame.channel); }
      queue.push(json);
      schedule();
    },
    setReceiveHandler(handler) {
      receive = handler;
      return () => { if (receive === handler) receive = null; };
    },
    close() { end("client_close"); },
    deliver(frames) {
      if (state !== "connected") return;
      for (const text of frames) {
        let frame: unknown;
        try { frame = JSON.parse(text); } catch { continue; }
        if (!isRecord(frame) || frame.neoastra !== 1 || typeof frame.kind !== "string") continue;
        try { receive?.(Object.freeze(frame)); } catch { /* A handler that throws has nothing more to hear. */ }
      }
    },
    setPaused(next) {
      if (paused === next) return;
      paused = next;
      if (paused || held.size === 0) return;
      // Back on screen: the stream goes on from where the script is.
      for (const [channel, sequence] of held) acknowledgements.set(channel, Math.max(acknowledgements.get(channel) ?? 0, sequence));
      held.clear();
      schedule();
    },
    end,
  };
  return connection;
}

/** Turns what the client or the transport throws into the stable error of `alta.rpc`. */
export function toAltaError(error: unknown): AltaError {
  if (error instanceof AltaError) return error;
  if (error instanceof NeoRpcError) return new AltaError(error.code, error.message, { retryable: error.retryable, correlationId: error.correlationId });
  if (error instanceof NeoAstraClientError) return new AltaError(error.code, error.message, { retryable: error.retryable });
  if (error instanceof DOMException && error.name === "AbortError") return new AltaError("operation_canceled", "The call was canceled.");
  return new AltaError("rpc_failed", error instanceof Error ? error.message : String(error));
}

const canceled = () => new AltaError("operation_canceled", "The call was canceled.");
const closedError = () => new AltaError("connection_closed", "The canvas is closed.");

function openError(status: string): AltaError {
  // The instance has no calls, and will have none while it is open.
  if (status === "unavailable") return new AltaError("rpc_unavailable", "This canvas has no calls: its plugin registered none.");
  if (status === "unknown") return new AltaError("connection_closed", "The canvas is not open on the plugin's side.", { retryable: true });
  // The host could not be asked, or no page watches the canvases yet (the window starts, or reconnects): the same call can work a moment later.
  if (status === "unreachable" || status === "not_watching") return new AltaError("connection_closed", "The window cannot reach the plugin now.", { retryable: true });
  return new AltaError("rpc_unavailable", "The window cannot reach the plugin now.", { retryable: status !== "stale_epoch" });
}

/** What the page keeps of the calls of one canvas tab: the `alta.rpc` of its scripts, and how the tab drives it. */
export type CanvasRpcController = Readonly<{
  rpc: AltaRpc;
  /** Says whether the tab is shown: while it is not, the acknowledgements of the streams are held back. */
  setVisible(visible: boolean): void;
  /** The tab is drawn: the controller serves calls. A controller serves calls until it is detached; React may attach it again (its development double run). */
  attach(): void;
  /** The tab went away: the connection ends, and what is pending fails. */
  detach(): void;
}>;

/** What a controller needs. */
export type CanvasRpcControllerOptions = Readonly<{
  carrier: CanvasRpcCarrier;
  instanceId: string;
  timers?: CanvasRpcTimers;
  now?: () => number;
  minimumGapMilliseconds?: number;
}>;

type Session = Readonly<{
  id: string;
  connection: CarrierConnection;
  client: NeoRpcClient;
  events: EventMux;
}>;

// The events of the plugin come on one NeoAstra event, `alta.events`, as `{ name, value }`: one subscription of the client serves every name.
class EventMux {
  private readonly handlers = new Map<string, Set<(value: unknown) => void>>();
  private subscription: Promise<() => Promise<void>> | null = null;

  constructor(private readonly client: NeoRpcClient) { }

  async add(name: string, handler: (value: unknown) => void, signal: AbortSignal | undefined): Promise<() => void> {
    if (signal?.aborted) throw canceled();
    let set = this.handlers.get(name);
    if (!set) this.handlers.set(name, set = new Set());
    set.add(handler);
    let active = true;
    const remove = () => {
      if (!active) return;
      active = false;
      signal?.removeEventListener("abort", remove);
      const current = this.handlers.get(name);
      current?.delete(handler);
      if (current?.size === 0) this.handlers.delete(name);
      // The last listener frees the subscription; one that failed is made again by the next call, not kept.
      if (this.handlers.size === 0 && this.subscription) {
        const ending = this.subscription;
        this.subscription = null;
        void ending.then(unsubscribe => unsubscribe()).catch(() => { });
      }
    };
    try {
      // The subscription serves every listener and belongs to none: the signal of a listener ends its own listening, never the others'.
      const shared = this.subscription ??= this.client.subscribe<unknown>("alta.events", value => this.dispatch(value)).then(unsubscribe => unsubscribe);
      await (signal ? new Promise<unknown>((resolve, reject) => {
        const abort = () => reject(canceled());
        signal.addEventListener("abort", abort, { once: true });
        shared.then(resolve, reject).finally(() => signal.removeEventListener("abort", abort));
      }) : shared);
    } catch (error) {
      remove();
      throw toAltaError(error);
    }

    signal?.addEventListener("abort", remove, { once: true });
    return remove;
  }

  private dispatch(value: unknown) {
    if (!isRecord(value) || typeof value.name !== "string") return;
    for (const handler of [...(this.handlers.get(value.name) ?? [])]) {
      try { handler(value.value); } catch { /* A listener that throws does not stop the others. */ }
    }
  }
}

async function* items(iterable: AsyncIterable<unknown>): AsyncGenerator<unknown> {
  try { yield* iterable; }
  catch (error) { throw toAltaError(error); }
}

/**
 * Makes the `alta.rpc` of the canvas tab of an instance. Nothing is opened until a script calls: the first call connects (the host opens a
 * session), a connection that ends is made again by the next call, and `generation` counts those remakings. Calls are never replayed.
 */
export function createCanvasRpc(options: CanvasRpcControllerOptions): CanvasRpcController {
  const { carrier, instanceId } = options;
  let alive = true;
  let visible = true;
  let session: Session | null = null;
  let opening: Promise<Session> | null = null;
  let generation = 0;
  let everConnected = false;
  let stopListening: (() => void) | null = null;
  const generationListeners = new Set<(value: number) => void>();
  // Whether a connection is open, for the scripts that only listen: no call of theirs would tell them that it ended.
  let open = false;
  const connectedListeners = new Set<(value: boolean) => void>();
  function setOpen(next: boolean) {
    if (open === next) return;
    open = next;
    for (const each of [...connectedListeners]) { try { each(next); } catch { /* A listener that throws does not stop the others. */ } }
  }

  const listener: CanvasRpcListener = {
    frames(connection, frames) { if (session?.id === connection) session.connection.deliver(frames); },
    closed(connection, why) {
      if (!session || connection !== null && session.id !== connection) return;
      session.connection.end(why === "client_close" ? "host_closed" : why);
    },
  };

  function drop(closing: Session) {
    if (session !== closing) return;
    session = null;
    setOpen(false);
  }

  async function connect(): Promise<Session> {
    stopListening ??= carrier.listen(instanceId, listener);
    const reply = await carrier.open(instanceId);
    if (reply.status !== "ok" || !reply.connection) throw openError(reply.status);
    if (!alive) { void carrier.close(instanceId, reply.connection).catch(() => { }); throw closedError(); }
    const connection = createCarrierConnection({ carrier, instanceId, connection: reply.connection, maximumFrameBytes: reply.maximumFrameBytes,
      timers: options.timers, now: options.now, minimumGapMilliseconds: options.minimumGapMilliseconds });
    connection.setPaused(!visible);
    const client = new NeoRpcClient(connection);
    const made: Session = { id: reply.connection, connection, client, events: new EventMux(client) };
    connection.closed.addEventListener("abort", () => drop(made), { once: true });
    session = made;
    if (everConnected) {
      generation++;
      for (const each of [...generationListeners]) each(generation);
    }

    everConnected = true;
    setOpen(true);
    return made;
  }

  async function ensure(signal: AbortSignal | undefined): Promise<Session> {
    if (!alive) throw closedError();
    if (signal?.aborted) throw canceled();
    if (session && session.connection.state === "connected") return session;
    opening ??= connect().finally(() => { opening = null; });
    if (!signal) return opening;
    return await new Promise<Session>((resolve, reject) => {
      const abort = () => reject(canceled());
      signal.addEventListener("abort", abort, { once: true });
      opening!.then(resolve, reject).finally(() => signal.removeEventListener("abort", abort));
    });
  }

  const rpc: AltaRpc = Object.freeze({
    async invoke(name: string, input?: unknown, callOptions?: Readonly<{ signal?: AbortSignal }>) {
      try {
        const made = await ensure(callOptions?.signal);
        return await made.client.invoke<unknown, unknown>(name, input ?? null, { signal: callOptions?.signal });
      } catch (error) { throw toAltaError(error); }
    },
    async stream(name: string, input?: unknown, callOptions?: Readonly<{ signal?: AbortSignal }>) {
      try {
        const made = await ensure(callOptions?.signal);
        return items(await made.client.invokeChannel<unknown, unknown>(name, input ?? null, { signal: callOptions?.signal }));
      } catch (error) { throw toAltaError(error); }
    },
    async subscribe(name: string, handler: (value: unknown) => void, callOptions?: Readonly<{ signal?: AbortSignal }>) {
      if (typeof handler !== "function") throw new AltaError("invalid_request", "An event handler is required.");
      try {
        const made = await ensure(callOptions?.signal);
        return await made.events.add(name, handler, callOptions?.signal);
      } catch (error) { throw toAltaError(error); }
    },
    generation: Object.freeze<AltaSignal<number>>({
      get value() { return generation; },
      subscribe(listenerFunction) { generationListeners.add(listenerFunction); return () => { generationListeners.delete(listenerFunction); }; },
    }),
    connected: Object.freeze<AltaSignal<boolean>>({
      get value() { return open; },
      subscribe(listenerFunction) { connectedListeners.add(listenerFunction); return () => { connectedListeners.delete(listenerFunction); }; },
    }),
  });

  return {
    rpc,
    setVisible(next) {
      visible = next;
      session?.connection.setPaused(!next);
    },
    attach() { alive = true; },
    detach() {
      alive = false;
      const ending = session;
      session = null;
      setOpen(false);
      stopListening?.();
      stopListening = null;
      if (ending) {
        ending.client.close();
        ending.connection.end("client_close");
      }
    },
  };
}
