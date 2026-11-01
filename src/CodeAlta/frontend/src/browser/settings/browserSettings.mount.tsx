// Disposable page; the settings page of the Browser plugin over calls whose plugin the test plays.
import { StrictMode, createElement } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import type { BrowserSettingsView } from "./model";
import { BrowserSettings, type BrowserSettingsApi } from "./BrowserSettings";

const root = createRoot(document.getElementById("root")!);
const edge = { id: "edge", name: "Microsoft Edge", version: "156.0.3485.54", engine: "chromium" as const, path: "C:/Edge/msedge.exe", isDefault: true,
  lastUsed: "2026-11-01T10:05:00Z", measurable: true, supportsUserProfile: true };
const firefox = { id: "firefox", name: "Firefox", version: "150.0", engine: "firefox" as const, path: "C:/Firefox/firefox.exe", isDefault: false,
  lastUsed: null, measurable: true, supportsUserProfile: true };
const state = { calls: [] as string[], settings: {
  browsers: [edge, firefox], browser: "auto", engine: null, profile: "isolated", headless: false, evaluateScript: true,
  user: { enabled: false, allowedSites: ["github.com"], blockedSites: [] },
  choice: { id: "edge", name: "Microsoft Edge", source: "auto", reason: "the default browser, used just now" },
  automatic: { id: "edge", name: "Microsoft Edge", source: "auto", reason: "the default browser, used just now" },
  refusal: null, persistentProfiles: ["work"], error: null, notice: null,
} as BrowserSettingsView };
const listeners = new Set<(settings: BrowserSettingsView) => void>();
const api: BrowserSettingsApi = {
  get: async () => { state.calls.push("get"); return state.settings; },
  set: async change => {
    state.calls.push(`set:${JSON.stringify(change)}`);
    state.settings = {
      ...state.settings,
      browser: change.browser ?? state.settings.browser, profile: change.profile ?? state.settings.profile,
      headless: change.headless ?? state.settings.headless, evaluateScript: change.evaluateScript ?? state.settings.evaluateScript,
      user: {
        enabled: change.userProfileEnabled ?? state.settings.user.enabled,
        allowedSites: change.allowedSites ?? state.settings.user.allowedSites, blockedSites: change.blockedSites ?? state.settings.user.blockedSites,
      },
      choice: change.browser === "firefox" ? { id: "firefox", name: "Firefox", source: "settings", reason: "preferred in Settings" } : state.settings.choice,
      error: null, notice: null,
    };
    return state.settings;
  },
  open: async name => { state.calls.push(`open:${name}`); return { ...state.settings, notice: `Microsoft Edge opened the profile ${name}.` }; },
  remove: async name => {
    state.calls.push(`remove:${name}`);
    state.settings = { ...state.settings, persistentProfiles: state.settings.persistentProfiles.filter(item => item !== name) };
    return state.settings;
  },
  changes: listener => { listeners.add(listener); return () => { listeners.delete(listener); }; },
};

const fixture = {
  state,
  /** Under StrictMode, as in the application. */
  render() { flushSync(() => root.render(createElement(StrictMode, null, createElement("div", { className: "settings-dialog-content" }, createElement(BrowserSettings, { api }))))); },
};
Object.assign(window, { browserSettingsFixture: fixture });
