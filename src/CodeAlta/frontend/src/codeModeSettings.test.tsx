import assert from "node:assert/strict";
import test from "node:test";
import { createElement, type ReactElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { CodeModeSettings } from "./CodeModeSettings";
import { codeModeSettingsMessages } from "./codeModeSettingsMessages";
import { locales, messages, translate } from "./localization";
import { promptProvenanceIcon, promptProvenanceTitle } from "./promptProvenance";
import { ShellLanguageContext } from "./shellLanguage";

const never = () => assert.fail("rendering must not act");
// The host is asked nothing while the page is drawn.
const silent = () => new Promise<never>(() => { });
const render = (element: ReactElement) =>
  renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale: "en", choice: "en", setLanguage: never } }, element));

test("the Code Mode page is read before it is shown", () => {
  const html = render(createElement(CodeModeSettings, { epoch: "e", api: { codeModeSettings: silent, saveCodeModeSettings: never } }));
  assert.match(html, /<span class="eyebrow">Agent &amp; models<\/span><h1>Code Mode<\/h1>/);
  assert.match(html, /Agents write scripts that call their tools, instead of calling them one by one\./);
  assert.match(html, /Loading…/);
  assert.equal(html.includes("settings-code-mode\""), false);
});

test("the texts of the Code Mode page are in every language", () => {
  for (const key of [...Object.keys(codeModeSettingsMessages), "Off", "On", "Mode"]) {
    assert.ok(key in messages, key);
    for (const locale of locales) assert.ok(translate(locale, key as keyof typeof messages).trim(), `${locale}: ${key}`);
  }
});

test("the result of a script that ended in the background is titled Workflow", () => {
  assert.deepEqual(promptProvenanceTitle("workflow"), { key: "Workflow" });
  assert.equal(promptProvenanceIcon("workflow"), "code");
});
