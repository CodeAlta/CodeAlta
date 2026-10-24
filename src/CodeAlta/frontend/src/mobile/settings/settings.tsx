import { useEffect, useMemo, useState } from "react";
import type { AltaRpc } from "../../pluginScript/alta";
import { useAlta } from "../../pluginScript/PluginScript";
import { readMobileSettings, type MobileSettingsView } from "./model";
import { MobileSettings, type MobileSettingsApi } from "./MobileSettings";

// The module of the settings page of the Mobile plugin, the one it names with `PluginScript.App("mobile-settings")`: Settings > Extensions >
// Mobile draws it in the frame of its pages, and it talks to the plugin with `alta.rpc`.

const unreadable = () => new Error("The plugin gave no settings.");

/** The calls of the page over `alta.rpc`. An answer that is not settings fails the call. */
export function createMobileSettingsApi(rpc: AltaRpc): MobileSettingsApi {
  const read = (value: unknown): MobileSettingsView => readMobileSettings(value) ?? (() => { throw unreadable(); })();
  return {
    get: async signal => read(await rpc.invoke("mobile.settings.get", {}, { signal })),
    set: async ports => read(await rpc.invoke("mobile.settings.set", ports)),
    reset: async () => read(await rpc.invoke("mobile.reset", {})),
    changes: listener => {
      const controller = new AbortController();
      void rpc.subscribe("mobile.settings.changed", value => { const settings = readMobileSettings(value); if (settings) listener(settings); }, { signal: controller.signal })
        .catch(() => { /* The connection ended: the page subscribes again with its next connection. */ });
      return () => controller.abort();
    },
  };
}

/** The settings page of the Mobile plugin, for Settings to draw. */
export default function Settings() {
  const alta = useAlta();
  // What `subscribe` gave ends with the connection: a page that only listens would not hear the plugin again after a reload of the plugin
  // or of the page, so it asks again (which connects again) a moment after the connection ended, and when it connected again.
  const [round, setRound] = useState(0);
  useEffect(() => alta.rpc.generation.subscribe(() => setRound(value => value + 1)), [alta]);
  useEffect(() => {
    const connected = alta.rpc.connected;
    if (!connected) return undefined;
    let timer: ReturnType<typeof setTimeout> | null = null;
    const stop = connected.subscribe(() => {
      if (connected.value || timer !== null) return;
      timer = setTimeout(() => { timer = null; setRound(value => value + 1); }, 1000);
    });
    return () => { stop(); if (timer !== null) clearTimeout(timer); };
  }, [alta]);
  // A new round makes a new api: the page reads the settings and subscribes again.
  const api = useMemo(() => createMobileSettingsApi(alta.rpc), [alta, round]);
  return <MobileSettings api={api} />;
}
