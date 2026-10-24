import type { Live } from "./contract";

/** The state of the link to the computer: the event stream is open, being opened again, or given up until something changes. */
export type LinkState = "connecting" | "live" | "reconnecting" | "offline";

/** The part of `EventSource` the stream uses. */
export type EventSourceLike = {
  readonly readyState: number;
  addEventListener(type: string, listener: (event: MessageEvent) => void): void;
  close(): void;
};

/** How long to wait before opening the stream again after the n-th failure in a row: 1 s, 2 s, 4 s… up to 30 s. */
export const backoff = (failures: number): number => Math.min(30_000, 1000 * 2 ** Math.max(0, failures - 1));

/** The server pings every 15 s: a stream silent for three pings is dead, even when the browser still calls it open. */
export const silenceLimit = 45_000;

const closed = 2, open = 1;

/**
 * The live stream of `/api/events`. The browser opens an `EventSource` again by itself while it is CONNECTING; once it
 * is CLOSED (a refused request, a 401) it gave up, and this opens a new one after a backoff, first asking `check`
 * whether the phone is still signed in (`check` signs in again with the kept token, or reports the phone signed out).
 * A stream that stays silent too long is replaced. `wake` (the page is shown again, the phone is back online) opens it
 * again at once when it is not open. Each time the stream opens after having been lost, `onReconnected` lets the page
 * read again what it may have missed.
 */
export function createLiveStream(options: Readonly<{
  create: () => EventSourceLike;
  onSession: (live: Live) => void;
  onState: (state: LinkState) => void;
  onReconnected: () => void;
  check: () => Promise<"ok" | "signed-out" | "unreachable">;
  setTimer: (callback: () => void, ms: number) => unknown;
  clearTimer: (timer: unknown) => void;
}>) {
  let source: EventSourceLike | null = null;
  let failures = 0, opened = false, lost = false, stopped = true;
  let retry: unknown = null, silence: unknown = null;

  const clear = (timer: unknown) => { if (timer !== null) options.clearTimer(timer); };
  const watchSilence = () => { clear(silence); silence = options.setTimer(() => { if (source === current()) restart(); }, silenceLimit); };
  const current = () => source;

  function connect() {
    clear(retry); retry = null;
    source?.close();
    const events = options.create();
    source = events;
    if (!opened) options.onState("connecting");
    events.addEventListener("open", () => {
      if (source !== events) return;
      failures = 0;
      options.onState("live");
      watchSilence();
      if (lost) { lost = false; options.onReconnected(); }
      opened = true;
    });
    events.addEventListener("ping", () => { if (source === events) watchSilence(); });
    events.addEventListener("session", event => {
      if (source !== events) return;
      watchSilence();
      let live: Live;
      try { live = JSON.parse(String(event.data)) as Live; } catch { return; }
      if (live && typeof live.id === "string") options.onSession(live);
    });
    events.addEventListener("error", () => {
      if (source !== events) return;
      lost = true;
      if (events.readyState !== closed) { options.onState("reconnecting"); return; }
      events.close();
      source = null;
      clear(silence); silence = null;
      void giveUp();
    });
  }

  async function giveUp() {
    failures++;
    let state: Awaited<ReturnType<typeof options.check>>;
    try { state = await options.check(); } catch { state = "unreachable"; }
    if (stopped) return;
    if (state === "signed-out") { stop(); return; }
    options.onState(state === "ok" ? "reconnecting" : "offline");
    clear(retry);
    retry = options.setTimer(connect, state === "ok" && failures === 1 ? 0 : backoff(failures));
  }

  function restart() {
    lost = true;
    source?.close();
    source = null;
    options.onState("reconnecting");
    connect();
  }

  function stop() {
    stopped = true;
    clear(retry); clear(silence); retry = silence = null;
    source?.close();
    source = null;
  }

  return {
    start() { if (!stopped) return; stopped = false; connect(); },
    /** The page is shown again, or the phone is online again: open the stream now if it is not open. */
    wake() {
      if (stopped) return;
      if (source?.readyState === open) return;
      failures = 0;
      lost = true;
      connect();
    },
    stop,
    /** Whether the stream is open now. */
    get open() { return source?.readyState === open; },
  };
}
