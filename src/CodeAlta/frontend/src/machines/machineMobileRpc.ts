import type { machines } from "#neoastra";
import { AltaError, type AltaRpc } from "../pluginScript/alta";
import type { MessageKey } from "../localization";
import { managementProblem } from "./machineManagement";

// The page of the phones of a machine this computer manages by pairing (`control.mobile.*`): the Mobile tab draws the machine's own plugin
// through `machines.mobileInvoke`, in the place of the `alta.rpc` of a plugin tab. Plain functions, without the DOM.

/** The calls of the page of the phones of a machine. */
export type MachineMobileApi = Pick<typeof machines, "mobile" | "mobileEnable" | "mobileInvoke"> & Partial<Pick<typeof machines, "openSignInPage">>;

/** How often the tab asks a machine for its status: a node sends no event of its own, and a phone that signs in ends a code. */
export const mobilePollMilliseconds = 3000;

/** The words the page says for a status of a call about the page of the phones; null when it went well. */
export function mobileProblem(status: string): MessageKey | null {
  switch (status) {
    case "mobile.unsupported": return "This machine does not serve the page of the phones: update CodeAlta Node there.";
    case "unavailable": return "The page of the phones is off on this machine.";
    case "policy.refused": return "This machine lets other people in, so it does not serve the page of the phones.";
    default: return managementProblem(status);
  }
}

/** Whether a call that failed with this status may work later: the machine is not reached now. */
export const mobileRetryable = (status: string) => status === "offline" || status === "machine_offline" || status === "connecting";

/**
 * The `alta.rpc` the Mobile tab reads for a machine: `invoke` forwards the call to the machine; `subscribe` asks for its status every
 * `pollMilliseconds` and sends it when it changed, since a node sends no event. A call the machine refuses is an `AltaError` that carries
 * the machine's own words as its message, with the words of the page when it says none.
 */
export function createMachineMobileRpc(api: MachineMobileApi, target: Readonly<{ epoch: string; machineId: string }>,
  describe: (status: string) => string | null, pollMilliseconds = mobilePollMilliseconds): AltaRpc {
  const invoke = async (name: string, input?: unknown, options?: Readonly<{ signal?: AbortSignal }>) => {
    const payload = typeof input === "object" && input !== null && Object.keys(input).length > 0 ? JSON.stringify(input) : null;
    let result;
    try {
      result = await api.mobileInvoke({ expectedEpoch: target.epoch, machineId: target.machineId, method: name, payload },
        { signal: options?.signal });
    } catch (error) {
      throw new AltaError("connection_closed", error instanceof Error && error.message ? error.message : "The machine does not answer.", { retryable: true });
    }
    if (result.status !== "ok" || result.json == null)
      throw new AltaError(result.status === "ok" ? "invalid_response" : result.status, result.message || describe(result.status) || "The machine sent an answer the tab cannot read.", { retryable: mobileRetryable(result.status) });
    return JSON.parse(result.json) as unknown;
  };
  return {
    invoke,
    stream: async () => { throw new Error("The page of a machine has no stream."); },
    subscribe: async (name, handler, options) => {
      const method = name === "mobile.settings.changed" ? "mobile.settings.get" : "mobile.status";
      const abort = new AbortController();
      let alive = true, busy = false, last: string | null = null;
      const timer = setInterval(() => {
        if (!alive || busy || typeof document !== "undefined" && document.hidden) return;
        busy = true;
        invoke(method, undefined, { signal: abort.signal }).then(value => {
          const json = JSON.stringify(value);
          if (alive && json !== last) { last = json; handler(value); }
        }, () => { /* The next read tells why it fails. */ }).finally(() => { busy = false; });
      }, pollMilliseconds);
      const stop = () => { alive = false; clearInterval(timer); abort.abort(); options?.signal?.removeEventListener("abort", stop); };
      if (options?.signal?.aborted) stop();
      else options?.signal?.addEventListener("abort", stop, { once: true });
      return stop;
    },
    generation: { value: 0, subscribe: () => () => { } },
  };
}
