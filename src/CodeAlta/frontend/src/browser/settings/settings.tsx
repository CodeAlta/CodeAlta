import { useEffect, useMemo, useState } from "react";
import type { AltaRpc } from "../../pluginScript/alta";
import { useAlta } from "../../pluginScript/PluginScript";
import { readBrowserSettings, type BrowserSettingsView } from "./model";
import { BrowserSettings, type BrowserSettingsApi } from "./BrowserSettings";

// The module of the settings page of the Browser plugin, the one it names with `PluginScript.App("browser-settings")`: Settings > Extensions >
// Browser draws it in the frame of its pages, and it talks to the plugin with `alta.rpc`.

const unreadable = () => new Error("The plugin gave no settings.");

/** The calls of the page over `alta.rpc`. An answer that is not settings fails the call. */
export function createBrowserSettingsApi(rpc: AltaRpc): BrowserSettingsApi {
  const read = (value: unknown): BrowserSettingsView => readBrowserSettings(value) ?? (() => { throw unreadable(); })();
  return {
    get: async signal => read(await rpc.invoke("browser.settings.get", {}, { signal })),
    set: async change => read(await rpc.invoke("browser.settings.set", change)),
    open: async (name, browser) => read(await rpc.invoke("browser.profile.open", { name, browser })),
    remove: async name => read(await rpc.invoke("browser.profile.delete", { name, browser: null })),
    changes: listener => {
      const controller = new AbortController();
      void rpc.subscribe("browser.settings.changed", value => { const settings = readBrowserSettings(value); if (settings) listener(settings); }, { signal: controller.signal })
        .catch(() => { /* The connection ended: the page subscribes again with its next connection. */ });
      return () => controller.abort();
    },
  };
}

/** The settings page of the Browser plugin, for Settings to draw. */
export default function Settings() {
  const alta = useAlta();
  // What `subscribe` gave ends with the connection: the page asks again a moment after the connection ended, and when it connected again.
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
  const api = useMemo(() => createBrowserSettingsApi(alta.rpc), [alta, round]);
  return <BrowserSettings api={api} />;
}
