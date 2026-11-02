// Disposable page; the settings page of the Mobile plugin over calls whose plugin the test plays.
import { StrictMode, createElement } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import type { MobileSettingsView } from "./model";
import { MobileSettings, type MobileSettingsApi } from "./MobileSettings";

const root = createRoot(document.getElementById("root")!);
const state = { calls: [] as string[], settings: {
  providers: ["tailscale", "devtunnel"], provider: { id: "tailscale", state: "serving", detail: "Tailscale serves it" }, port: 2584, httpsPort: 2584, host: null,
  address: "https://desk.tail1234.ts.net:2584", server: "listening", serverDetail: null, error: null,
} as MobileSettingsView, refuse: null as string | null };
const listeners = new Set<(settings: MobileSettingsView) => void>();
const api: MobileSettingsApi = {
  get: async () => { state.calls.push("get"); return state.settings; },
  set: async change => {
    state.calls.push(change.provider === "tailscale" ? `set:${change.port}:${change.httpsPort}` : `set:${change.provider}:${change.port}`);
    if (state.refuse) return { ...state.settings, error: state.refuse };
    state.settings = {
      ...state.settings, port: change.port, httpsPort: change.httpsPort ?? state.settings.httpsPort, address: null,
      provider: { id: change.provider, state: "pending", detail: "Publishing the mobile view…" },
    };
    return state.settings;
  },
  reset: async () => { state.calls.push("reset"); return state.settings; },
  changes: listener => { listeners.add(listener); return () => { listeners.delete(listener); }; },
};

const fixture = {
  state,
  /** Under StrictMode, as in the application. */
  render() { flushSync(() => root.render(createElement(StrictMode, null, createElement("div", { className: "settings-dialog-content" }, createElement(MobileSettings, { api }))))); },
  /** The plugin says its settings changed. */
  change(settings: Partial<MobileSettingsView>) { state.settings = { ...state.settings, ...settings }; for (const listener of [...listeners]) listener(state.settings); },
};
Object.assign(window, { mobileSettingsFixture: fixture });
