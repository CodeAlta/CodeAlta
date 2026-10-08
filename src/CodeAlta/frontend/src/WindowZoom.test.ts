import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { DesktopShellPreferences } from "#neoastra";
import { locales, translate } from "./localization";
import { ShellLanguageContext } from "./shellLanguage";
import { WindowZoom, zoomWindow } from "./WindowZoom";

const preferences = (zoom: number, status = "ok"): DesktopShellPreferences => ({ status, onClose: "ask", canKeepRunning: true, platform: "windows", entryAdded: false,
  sessionWidth: 100, sessionWidths: null, trayIcon: true, zoom });

test("a zoom command asks the host for its step, and the answer is the zoom the window shows", async () => {
  const asked: number[] = [], applied: number[] = [];
  const host = async (request: { direction: number }) => { asked.push(request.direction); return preferences(100 + 10 * request.direction); };
  await zoomWindow("zoomIn", host, value => applied.push(value.zoom));
  await zoomWindow("zoomOut", host, value => applied.push(value.zoom));
  await zoomWindow("resetZoom", host, value => applied.push(value.zoom));
  assert.deepEqual([asked, applied], [[1, -1, 0], [110, 90, 100]]);
  // A window without a shell answers that it has none: nothing is shown as its zoom.
  await zoomWindow("zoomIn", async () => preferences(100, "unavailable"), value => applied.push(value.zoom));
  assert.equal(applied.length, 3);
  // A host that does not answer is the caller's to report.
  await assert.rejects(zoomWindow("zoomIn", async () => { throw new Error("gone"); }, value => applied.push(value.zoom)), /gone/);
  assert.equal(applied.length, 3);
});

test("the title bar shows the zoom of the window as a percentage, named in every language", () => {
  for (const locale of locales) {
    const html = renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale, choice: locale, setLanguage: () => assert.fail("rendering must not dispatch") } },
      createElement(WindowZoom, { zoom: 125, run: () => assert.fail("rendering must not run a command") })));
    assert.ok(html.includes(`aria-label="${translate(locale, "Zoom")}: 125%"`) && html.includes(`title="${translate(locale, "Zoom")}"`) && html.includes(">125%<"), html);
  }
});
