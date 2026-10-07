// Disposable page; the production component that shows the HTML fragments of plugins.
import { StrictMode, createElement } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import { PluginHtml } from "./PluginHtml";

const root = createRoot(document.getElementById("root")!);
const state = { executed: 0, actions: [] as { action: string; value: string | null; values: Record<string, string> }[] };
const fixture = {
  state,
  // Under StrictMode, as in the application: React then runs each effect of a new component twice.
  render(html: string) {
    flushSync(() => root.render(createElement(StrictMode, null,
      createElement(PluginHtml, { html, onAction: (action, value, values) => state.actions.push({ action, value, values }) }))));
  },
  /** Shows nothing, so that the next fragment is drawn by a new component. */
  clear() { flushSync(() => root.render(null)); },
};
Object.assign(window, { pluginFixture: fixture });
