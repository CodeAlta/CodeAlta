import { useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import type { StorageMaintenance, StorageRetentionMaintenance } from "#neoastra";
import type { MaintenanceApi } from "./maintenance";
import { useStorageMaintenance } from "./useStorageMaintenance";

function createFixture() {
  const streams: { kind: string; epoch: string; signal: AbortSignal; emit: (state: string) => void }[] = [];
  function open<T>(kind: string, epoch: string, signal: AbortSignal, value: (state: string) => T): Promise<AsyncIterable<T>> {
    const queue: T[] = [];
    let ended = false;
    let wake = () => {};
    signal.addEventListener("abort", () => { ended = true; wake(); }, { once: true });
    streams.push({ kind, epoch, signal, emit: state => { queue.push(value(state)); wake(); } });
    return Promise.resolve((async function* () {
      while (!ended) {
        if (!queue.length) await new Promise<void>(resolve => { wake = resolve; });
        while (!ended && queue.length) yield queue.shift()!;
      }
    })());
  }
  const api: MaintenanceApi = {
    watchMaintenance: async (request, options) => open<StorageMaintenance>("scratchpad", request.expectedEpoch!, options!.signal!, state => ({
      state, removedFiles: "2", removedDirectories: "1", skippedEntries: "0", reasons: [], startedAt: null, completedAt: null,
    })),
    watchRetention: async (request, options) => open<StorageRetentionMaintenance>("retention", request.expectedEpoch!, options!.signal!, state => ({
      state, examined: 0, deleted: 0, skipped: 0, reason: state === "skipped" ? "external_admission_unqualified" : null,
    })),
  };
  const root = createRoot(document.getElementById("root")!);
  const fixture = { streams, epoch: (_value: string) => {}, available: (_value: boolean) => {}, page: (_shown: boolean) => {}, unmount: () => root.unmount() };
  function App() {
    const [epoch, setEpoch] = useState("first");
    const [available, setAvailable] = useState(true);
    const [shown, setShown] = useState(true);
    const status = useStorageMaintenance(api, epoch, "en", available);
    useEffect(() => { fixture.epoch = setEpoch; fixture.available = setAvailable; fixture.page = setShown; }, []);
    return <><pre id="maintenance-snapshot">{JSON.stringify(status)}</pre>{shown && <div id="storage-page">Storage</div>}</>;
  }
  root.render(<App />);
  return fixture;
}

Object.assign(window, { maintenanceFixture: createFixture() });
