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
const render = (locale: typeof locales[number], location: HostLocation | undefined, canOpenInWindow = true, shadowed = false) => renderToStaticMarkup(
  createElement(ShellLanguageContext.Provider, { value: { locale, choice: locale, setLanguage: never } },
    createElement(McpServerReadOnlyNote, { server: { source: ".mcp.json", shadowed }, file: location, files: { locations: [], platform: "windows", open: canOpenInWindow ? never : undefined, reveal: never }, disabled: false })));
const escaped = (text: string) => text.replace(/&/g, "&amp;").replace(/'/g, "&#x27;").replace(/"/g, "&quot;");
const message = "This server is defined in the file of another tool, which CodeAlta does not change. Delete it from that file to remove it. To stop using it, turn it off with the Enabled switch of its row in the list.";
const shadowedMessage = "This server is defined in the file of another tool, which CodeAlta does not change. Another definition of the same name is in use, so this one has no effect. Delete it from that file to remove it.";

test("a server of another tool says why it cannot be removed, and shows the file to delete it from, in every language", () => {
  for (const locale of locales) {
    const html = render(locale, file());
    assert.ok(html.includes(escaped(translate(locale, message))), html);
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

test("the note points to the switch of the row in the list, and says that a server in the shadow has no effect to turn off", () => {
  for (const locale of locales) {
    // The switch of the form saves a definition in the file of CodeAlta: the note names the one of the list, as the Enabled label of the page does.
    assert.ok(render(locale, file()).includes(escaped(translate(locale, message))));
    assert.ok(translate(locale, message).includes(translate(locale, "Enabled")), locale);
    const shadowed = render(locale, file(), true, true);
    assert.ok(shadowed.includes(escaped(translate(locale, shadowedMessage))) && !shadowed.includes(escaped(translate(locale, message))), shadowed);
  }
  assert.notEqual(translate("fr", message), message);
  assert.ok(render("en", file()).includes("bp6-icon"), "the icon takes the place Blueprint keeps for it");
});
