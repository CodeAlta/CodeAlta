import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { locales, translate } from "../localization";
import { ShellLanguageContext } from "../shellLanguage";
import { createCanvasHub, type CanvasApi } from "./canvasHub";
import { PluginCanvasSettingsPage } from "./PluginCanvasSettingsPage";

const never = () => assert.fail("drawing must not act");
const hub = createCanvasHub(new Proxy({}, { get: () => never }) as unknown as CanvasApi);

test("the settings page of a plugin is a page of Settings: the group, the title and the description of its canvas, around the canvas", () => {
  for (const locale of locales) {
    const html = renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale, choice: locale, setLanguage: never } },
      createElement(PluginCanvasSettingsPage, { page: { pluginKey: "builtin:mobile", canvasId: "mobile-settings", title: "Mobile", description: "The ports of the view.", icon: "smartphone", plugin: null },
        hub, control: null, onInstance: never, onAbandoned: never, onLeave: never, onOpenSource: never })));
    assert.ok(html.includes(`<span class="eyebrow">${translate(locale, "Extensions")}</span>`), `${locale}: ${html}`);
    assert.ok(html.includes("<h1>Mobile</h1>") && html.includes("<p>The ports of the view.</p>"), "the words of the plugin are shown as they are, in every language");
    assert.ok(html.includes('aria-label="Mobile"'));
    assert.ok(html.includes(`>${translate(locale, "Reload")}<`), "Reload asks the plugin for the page again");
    assert.match(html, /class="canvas-panel" data-phase="loading" data-visible="true" data-active="true"/u, "the canvas is drawn as a tab is, shown while the page is");
  }
  const bare = renderToStaticMarkup(createElement(PluginCanvasSettingsPage, { page: { pluginKey: "k", canvasId: "c", title: "Tools", description: null, icon: null, plugin: null },
    hub, control: null, onInstance: never, onAbandoned: never, onLeave: never, onOpenSource: never }));
  assert.ok(!bare.includes("<p>"), "a canvas without a description has none on its page");
});
