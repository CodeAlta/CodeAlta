// How the Search canvas follows the index: the host tells when it changed (`localSearch.watch`, at most twice a second), and the
// canvas reads again. A watch that ends (the host restarted, the connection dropped) is opened again a moment later.

type Timers = Readonly<{ set: (run: () => void, milliseconds: number) => unknown; clear: (timer: unknown) => void }>;

/** How long the canvas waits before it listens again after the host stopped telling. */
export const reconnectMilliseconds = 2000;

const defaultTimers: Timers = { set: (run, milliseconds) => setTimeout(run, milliseconds), clear: timer => clearTimeout(timer as number) };

/**
 * Listens to the changes of the index until the returned function is called, and calls `changed` for each: the first one comes as soon as
 * the host listens, so the canvas reads once at the start of each connection. Call it only while the canvas is shown.
 */
export function watchIndex(watch: (signal: AbortSignal) => Promise<AsyncIterable<unknown>>, changed: () => void, timers: Timers = defaultTimers): () => void {
  const abort = new AbortController();
  let timer: unknown = null;
  async function listen() {
    timer = null;
    try {
      for await (const _ of await watch(abort.signal)) {
        if (abort.signal.aborted) return;
        changed();
      }
    } catch { /* Opened again below. */ }
    if (!abort.signal.aborted) timer = timers.set(() => void listen(), reconnectMilliseconds);
  }
  void listen();
  return () => {
    abort.abort();
    if (timer !== null) timers.clear(timer);
  };
}
