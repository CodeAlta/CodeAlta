// Disposable page; the production window of the details of a timeline card, for the card of a turn of the Statistics plugin as
// the host gives it. Driven by sessionScope.browser.test.ts through `window.cardFixture`: what a command of the card is run for is recorded.
import { StrictMode, createElement } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import type { SessionPluginEvent } from "#neoastra";
import { pluginEventItem } from "../pluginEvents";
import { PluginPaneContext, PluginUiContext, noPluginContributions, type PluginPane, type PluginUiValue } from "../pluginUi";
import { TimelineDetails } from "../TimelineDetails";

const markdown = "| Metric | Value |\n| --- | ---: |\n| Duration | 4.0s |\n| Tool calls | 2 |";
// What `StatisticsPlugin.LinkedDetailsHtml` writes: the Markdown of the details as text, then the button of the session.
const html = `<div class="alta-column"><div class="alta-markdown">${markdown.replaceAll("&", "&amp;").replaceAll("<", "&lt;")}</div>`
  + `<div class="alta-row"><button type="button" data-alta-command="statistics-session">Session statistics</button></div></div>`;
const card: SessionPluginEvent = {
  eventId: "statistics:card-session:run-1", pluginId: "statistics", timestamp: "2026-10-09T10:00:04Z", markdown: "**Turn statistics** · 4.0s · tools 2 calls / 1.5s",
  details: [{ header: "Detailed statistics", markdown, html }], html: null, script: null, scriptProblem: null,
};

const root = createRoot(document.getElementById("root")!);
const state = { ran: [] as { name: string; pluginKey: string | null; pane: Partial<PluginPane> | null }[], closed: 0 };
const ui: PluginUiValue = {
  epoch: "epoch", projectId: "p-selected", contributions: noPluginContributions, run: () => { },
  runNamed: (name, pluginKey, pane) => { state.ran.push({ name, pluginKey, pane: pane ?? null }); },
};

const fixture = {
  state,
  /** Opens the details of the card: in the timeline of a session (`pane`), or with no pane around it. */
  open(pane: Partial<PluginPane> | null) {
    const details = createElement(TimelineDetails, { item: pluginEventItem(card), current: () => true, onClose: () => { state.closed++; fixture.clear(); } });
    flushSync(() => root.render(createElement(StrictMode, null,
      createElement(PluginUiContext.Provider, { value: ui }, pane ? createElement(PluginPaneContext.Provider, { value: pane }, details) : details))));
  },
  clear() { flushSync(() => root.render(null)); },
};
Object.assign(window, { cardFixture: fixture });
document.documentElement.classList.add("bp6-dark");
document.documentElement.dataset.theme = "dark";
