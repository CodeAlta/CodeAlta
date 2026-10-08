import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { SettingsFileLocation as HostLocation } from "#neoastra";
import { locales, translate } from "./localization";
import { SettingsFileLocation, SettingsFileLocations, revealLabel, type SettingsFiles } from "./SettingsFileLocation";
import { ShellLanguageContext } from "./shellLanguage";

const never = () => assert.fail("rendering must not act");
function render(element: ReturnType<typeof createElement>, locale: typeof locales[number] = "en") {
  return renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale, choice: locale, setLanguage: never } }, element));
}
const location = (change: Partial<HostLocation> = {}): HostLocation =>
  ({ kind: "skills", scope: "Global", id: "UserAlta", path: "/home/me/.alta/skills", folder: true, exists: true, canOpen: true, readOnly: false, ...change });
const buttons = (html: string) => [...html.matchAll(/<button[^>]*title="([^"]*)"/g)].map(match => match[1]);

test("a file of Settings shows its whole path, and opens, copies and shows it from three quiet buttons, in every language", () => {
  const path = "C:\\Users\\me\\.alta\\prompts\\agents\\reviewer.prompt.md";
  for (const locale of locales) {
    const html = render(createElement(SettingsFileLocation, { path, platform: "windows", onOpen: never, onReveal: never }), locale);
    // The path is the text and the tooltip: cut at its start by the layout, never by the page.
    assert.ok(html.includes(`<code title="${path}"><bdi>${path}</bdi></code>`), html);
    assert.deepEqual(buttons(html), [translate(locale, "Edit in the code editor"), translate(locale, "Copy path"), translate(locale, "Reveal in File Explorer")], html);
    assert.ok(html.includes(`aria-label="${translate(locale, "Copy path")}: ${path}"`), html);
  }
});

test("the file manager is named after the system, and what is only read is opened and not edited", () => {
  assert.deepEqual([revealLabel("windows"), revealLabel("macos"), revealLabel("linux"), revealLabel(null), revealLabel(undefined)],
    ["Reveal in File Explorer", "Reveal in Finder", "Open containing folder", null, null]);
  assert.deepEqual(buttons(render(createElement(SettingsFileLocation, { path: "/app/content/prompts", readOnly: true, platform: "macos", onOpen: never, onReveal: never }))),
    ["Open in the code editor", "Copy path", "Reveal in Finder"]);
  // No system to show it with, and nothing to open it: the path is still copied.
  assert.deepEqual(buttons(render(createElement(SettingsFileLocation, { path: "/a/b", platform: null, onReveal: never }))), ["Copy path"]);
  // A file that is not there yet is not shown in the file manager.
  const missing = render(createElement(SettingsFileLocation, { path: "/work/.alta/mcp.json", missing: true, platform: "linux", onReveal: never }));
  assert.ok(missing.includes('data-missing="true"') && !missing.includes("Open containing folder"), missing);
  assert.ok(render(createElement(SettingsFileLocation, { path: "/a/b", openLabel: "Edit notes", onOpen: never })).includes('aria-label="Edit notes"'));
});

test("the files of a page are listed with the scope they belong to, and one that cannot be opened has no button to open it", () => {
  const files: SettingsFiles = { platform: "windows", open: never, reveal: never, locations: [
    location(),
    location({ scope: "Project", id: "ProjectAlta", path: "/work/app/.alta/skills", exists: false }),
    location({ kind: "mcp", scope: "Project", id: "CodeAlta", path: "/work/app/.alta/mcp.json", folder: false, exists: false, canOpen: false }),
    location({ kind: "prompts", scope: "BuiltIn", id: "CodeAlta", path: "/app/content/prompts", readOnly: true }),
  ] };
  for (const locale of locales) {
    const html = render(createElement(SettingsFileLocations, { files }), locale);
    const rows = html.split('class="settings-file-location"').slice(1);
    assert.equal(rows.length, 4, html);
    assert.ok(html.includes(`aria-label="${translate(locale, "Files")}"`), html);
    assert.ok(rows[0].includes(`>${translate(locale, "Global")}<`) && rows[1].includes(`>${translate(locale, "Project")}<`) && rows[3].includes(`>${translate(locale, "Built-in")}<`), html);
    // A folder CodeAlta creates is opened before it exists; a file that is not there is only named.
    assert.deepEqual(buttons(rows[1]), [translate(locale, "Edit in the code editor"), translate(locale, "Copy path")], rows[1]);
    assert.deepEqual(buttons(rows[2]), [translate(locale, "Copy path")], rows[2]);
    assert.deepEqual(buttons(rows[3]), [translate(locale, "Open in the code editor"), translate(locale, "Copy path"), translate(locale, "Reveal in File Explorer")], rows[3]);
  }
  // A window that opens no editor still says where the files are.
  assert.deepEqual(buttons(render(createElement(SettingsFileLocations, { files: { ...files, open: undefined, locations: [location()] } }))), ["Copy path", "Reveal in File Explorer"]);
  assert.equal(render(createElement(SettingsFileLocations, { files: { ...files, locations: [] } })), "");
});
