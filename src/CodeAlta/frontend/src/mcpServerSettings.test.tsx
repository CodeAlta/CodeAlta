import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { SettingsFileLocation as HostLocation } from "#neoastra";
import { locales, translate } from "./localization";
import { McpServerReadOnlyNote } from "./McpServerSettings";
import { ShellLanguageContext } from "./shellLanguage";

const never = () => assert.fail("rendering must not act");
const file = (change: Partial<HostLocation> = {}): HostLocation =>
  ({ kind: "mcp", scope: "Project", id: "Common", path: "/work/app/.mcp.json", folder: false, exists: true, canOpen: true, readOnly: false, ...change });
const render = (locale: typeof locales[number], location: HostLocation | undefined, canOpenInWindow = true, shadowed = false, source: string | null = ".mcp.json") => renderToStaticMarkup(
  createElement(ShellLanguageContext.Provider, { value: { locale, choice: locale, setLanguage: never } },
    createElement(McpServerReadOnlyNote, { server: { source, shadowed }, file: location, files: { locations: [], platform: "windows", open: canOpenInWindow ? never : undefined, reveal: never }, disabled: false })));
const escaped = (text: string) => text.replace(/&/g, "&amp;").replace(/'/g, "&#x27;").replace(/"/g, "&quot;");
const imported = "This server comes from another tool's file. CodeAlta does not change that file. To remove the server, delete its entry there.";
const builtIn = "This built-in server cannot be removed.";
const shadowedMessage = "Another definition takes precedence, so this one has no effect.";
const disable = (locale: typeof locales[number]) => translate(locale, "To stop using it, turn off its {enabled} switch in the list.", { enabled: translate(locale, "Enabled") });

test("a server of another tool says why it cannot be removed, and shows the file to delete it from, in every language", () => {
  for (const locale of locales) {
    const html = render(locale, file());
    assert.ok(html.includes(escaped(translate(locale, imported))), html);
    assert.ok(html.includes(escaped(disable(locale))), html);
    assert.ok(html.includes('<code title="/work/app/.mcp.json">') && html.includes(".mcp.json</span>"), html);
    assert.ok(html.includes(`title="${translate(locale, "Edit in the code editor")}"`), html);
  }
});

test("the note has no button to open a file that cannot be opened, or when the window opens none", () => {
  assert.ok(!render("en", file({ canOpen: false })).includes("Edit in the code editor"));
  assert.ok(!render("en", file(), false).includes("Edit in the code editor"));
  // The file is not listed: the reason is still given.
  const html = render("en", undefined);
  assert.ok(html.includes("CodeAlta does not change") && !html.includes("<code"), html);
});

test("an overridden definition has no effect, so the note does not suggest turning it off", () => {
  for (const locale of locales) {
    const shadowed = render(locale, file(), true, true);
    assert.ok(shadowed.includes(escaped(translate(locale, shadowedMessage))) && !shadowed.includes(escaped(disable(locale))), shadowed);
  }
  assert.ok(render("en", file()).includes("bp6-icon"), "the icon takes the place Blueprint keeps for it");
});

test("a built-in server has no file to edit, whether or not it is overridden", () => {
  for (const locale of locales) for (const shadowed of [false, true]) {
    const html = render(locale, undefined, true, shadowed, null);
    assert.ok(html.includes(escaped(translate(locale, builtIn))), html);
    assert.ok(html.includes(escaped(shadowed ? translate(locale, shadowedMessage) : disable(locale))), html);
    assert.ok(!html.includes("<code") && !html.includes(escaped(translate(locale, imported))), html);
  }
});
