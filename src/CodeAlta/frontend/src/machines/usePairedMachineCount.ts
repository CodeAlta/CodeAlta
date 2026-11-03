import { useEffect, useState } from "react";
import { machines } from "#neoastra";

/**
 * How many machines this computer is paired with, read again each time the host says the machines changed: the menu of
 * a session offers Move to machine… only when there is one.
 */
export function usePairedMachineCount(epoch: string | null, api: Pick<typeof machines, "list" | "watch"> = machines): number {
  const [count, setCount] = useState(0);
  useEffect(() => {
    if (!epoch) { setCount(0); return; }
    const abort = new AbortController();
    const read = () => api.list({ expectedEpoch: epoch }, { signal: abort.signal })
      .then(value => { if (!abort.signal.aborted) setCount(value.status === "ok" ? value.machines.length : 0); }, () => { /* None is known. */ });
    void read();
    void (async () => {
      try { for await (const _ of await api.watch({ expectedEpoch: epoch }, { signal: abort.signal })) if (!abort.signal.aborted) void read(); }
      catch { /* The bridge is gone: the count stays. */ }
    })();
    return () => abort.abort();
  }, [api, epoch]);
  return count;
}
