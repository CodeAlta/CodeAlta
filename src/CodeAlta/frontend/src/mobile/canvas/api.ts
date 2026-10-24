import type { AltaRpc } from "../../pluginScript/alta";
import { retryLimit } from "../../pluginScript/retry";
import { readStatus, type MobileStatus } from "./status";

// What the Mobile tab asks of the Mobile plugin over `alta.rpc`: its status, to ask Tailscale again, and the event of its changes.

/** The name of the event the plugin sends each new status on. */
export const changedEvent = "mobile.changed";

/** What the tab hears: a status, or the call that failed (the plugin does not answer). */
export type MobileWatchEvent = Readonly<{ kind: "status"; status: MobileStatus } | { kind: "failed"; message: string }>;

const failureMessage = (error: unknown) => error instanceof Error && error.message ? error.message : String(error);
const retryable = (error: unknown) => (error as { retryable?: unknown } | null)?.retryable === true;

/** How long the tab waits before it asks to listen again when the plugin could not be reached; it doubles each time, up to 8 s. */
export const listenRetryMilliseconds = 500;

/** The calls of the Mobile tab. A call that fails rejects with the `AltaError` of the call. */
export function createMobileApi(rpc: AltaRpc, options: Readonly<{ retryMilliseconds?: number }> = {}) {
  const retryMilliseconds = options.retryMilliseconds ?? listenRetryMilliseconds;
  const status = async (name: string) => {
    const value = readStatus(await rpc.invoke(name, {}));
    if (!value) throw new Error("The Mobile plugin sent a status the tab cannot read.");
    return value;
  };
  return {
    status: () => status("mobile.status"),
    /** Asks Tailscale again. */
    retry: () => status("mobile.retry"),
    /**
     * Listens to the changes of the plugin, then reads its status (an event is not kept: one sent before the listening began is read
     * this way). Listens again when the connection to the plugin is made again, and after a wait when the plugin could not be reached.
     * Returns the function that stops it.
     */
    watch(listener: (event: MobileWatchEvent) => void): () => void {
      let alive = true, latest = 0, failures = 0;
      let stop: (() => void) | null = null;
      let timer: ReturnType<typeof setTimeout> | undefined;
      let generation = rpc.generation.value;
      const again = () => {
        if (!alive || timer !== undefined || failures >= retryLimit) return;
        timer = setTimeout(() => { timer = undefined; void listen(); }, Math.min(8000, retryMilliseconds * 2 ** Math.min(failures++, 4)));
      };
      const listen = async () => {
        const ticket = ++latest;
        if (timer !== undefined) { clearTimeout(timer); timer = undefined; }
        try {
          const off = await rpc.subscribe(changedEvent, value => {
            const read = readStatus(value);
            if (alive && read) listener({ kind: "status", status: read });
          });
          if (!alive || ticket !== latest) { off(); return; }
          stop?.();
          stop = off;
          failures = 0;
          const read = await status("mobile.status");
          if (alive && ticket === latest) listener({ kind: "status", status: read });
        } catch (error) {
          if (!alive || ticket !== latest) return;
          listener({ kind: "failed", message: failureMessage(error) });
          if (retryable(error)) again();
        }
      };
      void listen();
      const offGeneration = rpc.generation.subscribe(next => {
        if (next === generation) return;
        generation = next;
        failures = 0;
        void listen();
      });
      // A connection that ends takes the listening with it: listen again once it is made again.
      const offConnected = rpc.connected?.subscribe(open => {
        if (!open) { stop = null; return; }
        if (stop === null) { failures = 0; void listen(); }
      });
      return () => {
        alive = false;
        latest++;
        if (timer !== undefined) clearTimeout(timer);
        offGeneration();
        offConnected?.();
        stop?.();
        stop = null;
      };
    },
  };
}

/** The calls of the Mobile tab. */
export type MobileApi = ReturnType<typeof createMobileApi>;
