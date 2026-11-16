import { useEffect, useMemo, useRef, useState } from "react";
import type { MobileApi } from "./api";
import type { MobileCanvasActions } from "./MobileCanvas";
import type { MobileStatus } from "./status";

/** What the Mobile tab asks of the window that holds it: the tab of the window and the page of a machine hold it the same way. */
export type MobileCanvasHost = Readonly<{
  /** Opens a page of the web in the browser of the computer. */
  openLink(url: string): void;
  /** Tells that the address could not be copied. */
  copyFailed(): void;
}>;

/**
 * Follows the status of a Mobile plugin through its calls while `active`, and gives what the tab draws: the status, why the plugin does not
 * answer, and its actions. `pairing` adds the action that asks a host without a window for a code.
 */
export function useMobileCanvas(api: MobileApi, host: MobileCanvasHost, active: boolean, pairing = false) {
  const [status, setStatus] = useState<MobileStatus | null>(null);
  const [failure, setFailure] = useState<string | null>(null);
  const [watching, setWatching] = useState(0);
  // A tab that is not shown follows nothing; shown again, it listens again and reads the status.
  useEffect(() => {
    if (!active) return;
    return api.watch(event => {
      if (event.kind === "status") { setStatus(event.status); setFailure(null); } else setFailure(event.message);
    });
  }, [api, active, watching]);
  // What the actions reach is read when they run.
  const latest = useRef(host);
  latest.current = host;
  const actions = useMemo<MobileCanvasActions>(() => {
    // A call that answers with the status: the status shown, or why it failed.
    const run = async (call: () => Promise<MobileStatus>) => {
      try { setStatus(await call()); setFailure(null); }
      catch (error) { setFailure(error instanceof Error ? error.message : String(error)); }
    };
    return {
      retry: () => run(api.retry),
      copy: async text => {
        try { await navigator.clipboard.writeText(text); return true; }
        catch { latest.current.copyFailed(); return false; }
      },
      openLink: url => latest.current.openLink(url),
      reload: () => setWatching(value => value + 1),
      act: action => run(() => api.act(action)),
      otherMachines: enabled => run(() => api.otherMachines(enabled)),
      ...pairing ? { pair: () => run(api.pair) } : {},
    };
  }, [api, pairing]);
  return { status, failure, actions };
}
