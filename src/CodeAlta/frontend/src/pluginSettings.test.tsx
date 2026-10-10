import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { CanvasItem, PluginsEntry } from "#neoastra";
import { locales, translate } from "./localization";
import { PluginProblems, PluginRows, pluginCanvases, pluginFailure, pluginRows } from "./PluginSettings";
import { ShellLanguageContext } from "./shellLanguage";

const never = () => assert.fail("rendering must not act");
const entry = (id: string, change: Partial<PluginsEntry> = {}): PluginsEntry => ({
  id, name: id, description: null, kind: "Source", scope: "Global", state: "Enabled", enabled: true, enabledGlobal: null, enabledProject: null,
  runtime: "running", runtimeMessage: null, folder: `plugin:global:${id}`, path: `/home/.alta/plugins/${id}`, loadable: true, changed: false, errors: null, ...change,
});
const english = (key: Parameters<typeof translate>[1]) => translate("en", key);
function render(listed: readonly PluginsEntry[], change: { locale?: typeof locales[number]; edit?: boolean; remove?: boolean; disabled?: boolean } = {}) {
  const locale = change.locale ?? "en";
  return renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale, choice: locale, setLanguage: never } },
    createElement(PluginRows, { rows: pluginRows(listed, key => translate(locale, key)), disabled: change.disabled ?? false, onToggle: never, onReload: never,
      onEdit: change.edit === false ? undefined : never, onDelete: change.remove === false ? undefined : never, platform: "windows", onReveal: never })));
}
const cards = (html: string) => html.split('<div class="bp6-card').slice(1);
const card = (html: string, name: string) => cards(html).find(value => value.includes(`<strong>${name}</strong>`)) ?? assert.fail(`no row for ${name}`);

test("the plugins that ship with CodeAlta come first and are on until configuration says otherwise", () => {
  const rows = pluginRows([entry("notes", { name: "Notes", description: "Keeps notes." }), entry("bare", { name: "" }),
    entry("git", { kind: "BuiltIn", enabled: false, folder: null, path: null, loadable: false })], english);
  assert.deepEqual(rows.map(row => [row.id, row.name, row.enabled, row.builtIn]),
    [["mcp", "MCP", true, true], ["git", "Git", false, true], ["jira", "Jira", true, true], ["statistics", "Statistics", true, true], ["landing", "Landing page", true, true], ["ui", "UI tools", true, true], ["notes", "Notes", true, false], ["bare", "bare", true, false]]);
  assert.equal(rows[0].entry, null);
  assert.equal(rows[1].entry?.id, "git");
  assert.equal(rows[6].description, "Keeps notes.");
  assert.deepEqual(pluginRows([], english).map(row => row.id), ["mcp", "git", "jira", "statistics", "landing", "ui"]);
});

test("a source plugin with the id of a plugin that ships with CodeAlta keeps its own row and its actions", () => {
  const rows = pluginRows([entry("git", { enabledGlobal: false, enabled: false, state: "Disabled", runtime: null })], english);
  assert.deepEqual(rows.map(row => [row.id, row.builtIn, row.entry?.kind ?? null, row.enabled]),
    [["mcp", true, null, true], ["git", true, null, false], ["jira", true, null, true], ["statistics", true, null, true], ["landing", true, null, true], ["ui", true, null, true], ["git", false, "Source", false]]);
  const html = render([entry("git")]);
  assert.equal(cards(html).filter(value => value.includes("<strong>git</strong>") && value.includes('aria-label="Edit git"')).length, 1, html);
  assert.ok(!card(html, "Git").includes("Edit Git"), html);
});

test("what could not be read is named above the list, with its path and what was said of it, in every language", () => {
  const problems = [
    { kind: "config", path: "/work/app/.alta/config.toml", message: "(2,1): expected ]", scope: "Project" },
    { kind: "folder", path: "/home/.alta/plugins", message: "Access denied", scope: "Global" },
    { kind: "name", path: "/home/.alta/plugins/my plugin", message: null, scope: "Global" },
    { kind: "runtime", path: null, message: "The process cannot access the file", scope: null },
  ];
  for (const locale of locales) {
    const html = renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale, choice: locale, setLanguage: never } },
      createElement(PluginProblems, { problems, omitted: 3 })));
    assert.equal(html.split('class="plugin-failure"').length - 1, 5, html);
    for (const text of [
      translate(locale, "The configuration file {path} could not be read.", { path: "/work/app/.alta/config.toml" }) + " (2,1): expected ]",
      translate(locale, "The plugin folder {path} could not be read.", { path: "/home/.alta/plugins" }) + " Access denied",
      translate(locale, "{path} is not listed: the name of its folder is not a plugin id.", { path: "/home/.alta/plugins/my plugin" }),
      translate(locale, "The state of the running plugins could not be read."),
      translate(locale, "{count} more are not listed.", { count: 3 }),
    ]) assert.ok(html.includes(text), `${locale}: ${text}\n${html}`);
  }
  assert.equal(renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale: "en", choice: "en", setLanguage: never } },
    createElement(PluginProblems, { problems: [], omitted: 0 }))), "");
});

test("a source plugin that is on is built again and opened in the code editor from its row, in every language", () => {
  for (const locale of locales) {
    const html = render([entry("notes", { name: "Notes" }), entry("off", { enabled: false, state: "Disabled", runtime: null }),
      entry("local", { scope: "Project", folder: "plugin:project:p:local", loadable: false })], { locale });
    const notes = card(html, "Notes");
    assert.ok(notes.includes(`aria-label="${translate(locale, "Reload {name}", { name: "Notes" })}"`) && notes.includes(`title="${translate(locale, "Build and reload")}"`), notes);
    assert.ok(notes.includes(`aria-label="${translate(locale, "Edit {name}", { name: "Notes" })}"`) && notes.includes(`title="${translate(locale, "Edit in the code editor")}"`), notes);
    assert.ok(notes.includes(`>${translate(locale, "User")}<`), notes);
    // A plugin that is turned off is edited, and is not built: turning it on builds it.
    const off = card(html, "off");
    assert.ok(off.includes(translate(locale, "Edit {name}", { name: "off" })) && !off.includes(translate(locale, "Reload {name}", { name: "off" })), off);
    // A plugin of a project the application was not started in is edited, and is not loaded here.
    const local = card(html, "local");
    assert.ok(local.includes(`>${translate(locale, "Project")}<`) && local.includes(translate(locale, "Edit {name}", { name: "local" })), local);
    assert.ok(!local.includes(translate(locale, "Reload {name}", { name: "local" })), local);
    // The plugins that ship with CodeAlta have a switch and nothing else.
    const mcp = card(html, "MCP");
    assert.ok(mcp.includes(`>${translate(locale, "Built-in")}<`) && mcp.includes(translate(locale, "Enable {name}", { name: "MCP" })), mcp);
    assert.ok(!mcp.includes(translate(locale, "Edit {name}", { name: "MCP" })) && !mcp.includes(translate(locale, "Reload {name}", { name: "MCP" })), mcp);
  }
});

test("a source plugin is removed from its row with a red button, and a plugin that ships with CodeAlta is not, in every language", () => {
  for (const locale of locales) {
    const html = render([entry("notes", { name: "Notes" }), entry("local", { scope: "Project", folder: "plugin:project:p:local", loadable: false }), entry("git", { kind: "Config", folder: null, path: null })], { locale });
    for (const name of ["Notes", "local"]) {
      const row = card(html, name);
      assert.match(row, new RegExp(`<button[^>]*bp6-intent-danger[^>]*aria-label="${translate(locale, "Remove {name}", { name })}"|<button[^>]*aria-label="${translate(locale, "Remove {name}", { name })}"[^>]*bp6-intent-danger`), row);
      assert.ok(row.includes(`title="${translate(locale, "Remove")}"`), row);
    }
    for (const name of ["Git", "MCP"]) assert.ok(!card(html, name).includes("bp6-intent-danger"), card(html, name));
  }
  assert.ok(!render([entry("notes")], { remove: false }).includes("bp6-intent-danger"), "A page that removes nothing has no red button.");
  assert.match(card(render([entry("notes")], { disabled: true }), "notes"), /<button[^>]*disabled=""[^>]*aria-label="Remove notes"|<button[^>]*aria-label="Remove notes"[^>]*disabled=""/);
  // The row of a source plugin says where its folder is, and shows it in the file manager.
  const notes = card(render([entry("notes")]), "notes");
  assert.ok(notes.includes('<code title="/home/.alta/plugins/notes">') && notes.includes('title="Copy path"') && notes.includes('title="Reveal in File Explorer"'), notes);
  assert.ok(!card(render([entry("notes")]), "MCP").includes("settings-file-location"));
});

test("a row says what the running application did with its plugin and what the compiler reported", () => {
  const html = render([
    entry("fine"),
    entry("edited", { changed: true }),
    entry("broken", { changed: true, errors: ["plugin.cs(12,5): error CS0103: The name 'x' does not exist", "plugin.cs(20,1): error CS1002: ; expected"], runtimeMessage: "Plugin build failed." }),
    entry("dead", { state: "Failed", runtime: "failed", runtimeMessage: "Plugin activation failed: <boom>", changed: true }),
    entry("new", { runtime: "stopped" }),
    entry("terminal", { runtime: "unsupported" }),
  ]);
  const fine = card(html, "fine");
  assert.ok(!fine.includes("plugin-failure") && !fine.includes("Source changed") && !fine.includes("Not started"), fine);
  assert.ok(card(html, "edited").includes(">Source changed<"));
  // The version that runs keeps running: the row shows each error of the build, where it is, and not the summary.
  const broken = card(html, "broken");
  assert.equal(broken.split('class="plugin-failure"').length - 1, 2, broken);
  assert.ok(broken.includes("plugin.cs(12,5): error CS0103: The name &#x27;x&#x27; does not exist") && !broken.includes("Plugin build failed."), broken);
  assert.ok(broken.includes(">Source changed<") && broken.includes('aria-label="Reload broken"'), broken);
  // A plugin that did not start says why once, and is not said to have a changed source: nothing of it runs.
  const dead = card(html, "dead");
  assert.ok(dead.includes("Plugin activation failed: &lt;boom&gt;") && dead.includes(">Failed<") && !dead.includes("Source changed"), dead);
  assert.equal(dead.split(">Failed<").length - 1, 1, dead);
  assert.ok(card(html, "new").includes(">Not started<"));
  assert.ok(card(html, "terminal").includes(">Not supported in the desktop application<"));
});

test("a page that opens no editor has no edit button, and a change under way disables the others", () => {
  const closed = render([entry("notes")], { edit: false });
  assert.ok(!closed.includes("Edit notes") && closed.includes("Reload notes"), closed);
  const busy = card(render([entry("notes")], { disabled: true }), "notes");
  assert.match(busy, /<button[^>]*disabled=""[^>]*aria-label="Reload notes"|<button[^>]*aria-label="Reload notes"[^>]*disabled=""/);
  assert.match(busy, /<input[^>]*disabled=""[^>]*aria-label="Enable notes"|<input[^>]*aria-label="Enable notes"[^>]*disabled=""/);
});

test("a change that did not succeed says why in the words of plugins", () => {
  assert.equal(pluginFailure("ok"), null);
  assert.deepEqual(pluginFailure("build_failed", "plugin.cs(3,1): error CS1002: ; expected"),
    { key: "The plugin was not built. The version that was running keeps running.", intent: "danger", detail: "plugin.cs(3,1): error CS1002: ; expected" });
  assert.deepEqual(pluginFailure("start_failed"), { key: "The plugin was built and did not start.", intent: "danger", detail: null });
  assert.deepEqual(pluginFailure("not_loaded"), { key: "CodeAlta loads the plugins of the project it was started in.", intent: "warning" });
  assert.deepEqual(pluginFailure("disabled"), { key: "The plugin is turned off.", intent: "warning" });
  assert.deepEqual(pluginFailure("exists"), { key: "A plugin with this id already exists.", intent: "warning" });
  assert.deepEqual(pluginFailure("unknown"), pluginFailure("not_found"));
  // Every notice of the page is a message of the application, in each of its languages.
  assert.deepEqual(pluginFailure("trash_failed"), { key: "It could not be moved to the Trash.", intent: "danger" });
  assert.deepEqual(pluginFailure("trash_unavailable"), pluginFailure("trash_failed"));
  for (const status of ["build_failed", "start_failed", "not_loaded", "disabled", "exists", "unknown", "unavailable", "stale", "write_failed", "invalid_request", "trash_failed"]) {
    const notice = pluginFailure(status);
    assert.ok(notice, status);
    for (const locale of locales) assert.ok(translate(locale, notice.key).length > 0, `${status} in ${locale}`);
  }
});

const canvas = (id: string, plugin: Partial<CanvasItem>): CanvasItem => ({ pluginKey: "k", plugin: "P", package: null, id, title: id[0].toUpperCase() + id.slice(1), description: null, icon: null,
  iconData: null, scope: "Application", input: false, actions: 0, describes: false, ...plugin });

test("a row lists the canvases its plugin declares, with their scope, before anything is opened", () => {
  const declared = [canvas("board", { pluginKey: "global:notes", package: "plugin:global:notes", scope: "Project" }), canvas("run", { pluginKey: "global:notes", package: "plugin:global:notes", scope: "Session" }),
    canvas("stats", { pluginKey: "builtin:statistics", plugin: "Statistics" }), canvas("other", { pluginKey: "global:other", package: "plugin:global:other" })];
  const rows = pluginRows([entry("notes", { name: "Notes" }), entry("other", { name: "Other", state: "Disabled", enabled: false, runtime: null })], english);
  const names = (id: string, builtIn = false) => pluginCanvases(rows.find(row => row.id === id && row.builtIn === builtIn)!, declared).map(value => value.id);
  assert.deepEqual(names("notes"), ["board", "run"], "A source plugin is named by its folder.");
  assert.deepEqual(names("statistics", true), ["stats"], "A plugin that ships with CodeAlta is named by its key.");
  assert.deepEqual(names("git", true), []);
  assert.deepEqual(pluginCanvases(rows.find(row => row.id === "notes")!, []), [], "A plugin that does not run declares none.");
  for (const locale of locales) {
    const html = renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale, choice: locale, setLanguage: never } },
      createElement(PluginRows, { rows, canvases: declared, disabled: false, onToggle: never, onReload: never, platform: "windows", onReveal: never })));
    const notes = cards(html).find(value => value.includes("<strong>Notes</strong>")) ?? assert.fail("no row for Notes");
    assert.equal((notes.match(/class="plugin-canvas"/gu) ?? []).length, 2, locale);
    for (const [title, scope] of [["Board", "Project"], ["Run", "Session"]]) {
      assert.ok(notes.includes(translate(locale, "Canvas: {title} ({scope})", { title, scope: translate(locale, scope as "Project" | "Session") })), `${locale} ${title}`);
    }
  }
  const html = render([entry("notes")]);
  assert.ok(!html.includes("plugin-canvas"), "Nothing is listed where nothing is declared.");
});

test("the landing page is a plugin that ships with CodeAlta: its row has its switch, its canvas and its description, in every language", () => {
  // Without a word of configuration the page is on, as the other plugins that ship with CodeAlta.
  const landing = pluginRows([], english).find(row => row.id === "landing") ?? assert.fail("no row for the landing page");
  assert.deepEqual([landing.name, landing.enabled, landing.builtIn, landing.entry], ["Landing page", true, true, null]);
  // [plugins.landing] enabled = false: the host lists the id, and the row is the same one, turned off.
  const configured = [entry("landing", { kind: "BuiltIn", enabled: false, enabledGlobal: false, state: "Disabled", runtime: null, folder: null, path: null, loadable: false })];
  const off = pluginRows(configured, english);
  assert.deepEqual(off.filter(row => row.id === "landing").map(row => [row.enabled, row.builtIn, row.entry?.id ?? null]), [[false, true, "landing"]], "one row, not a second one for the id of configuration");
  const declared = [canvas("landing", { pluginKey: "builtin:landing", plugin: "Landing page", title: "Welcome" }), canvas("stats", { pluginKey: "builtin:statistics", plugin: "Statistics" })];
  assert.deepEqual(pluginCanvases(landing, declared).map(value => value.id), ["landing"]);
  for (const locale of locales) {
    const draw = (listed: readonly PluginsEntry[]) => card(renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale, choice: locale, setLanguage: never } },
      createElement(PluginRows, { rows: pluginRows(listed, key => translate(locale, key)), canvases: declared, disabled: false, onToggle: never, onReload: never, onEdit: never, onDelete: never,
        platform: "windows", onReveal: never }))), "Landing page");
    const on = draw([]);
    assert.ok(on.includes(translate(locale, "The welcome page: recent sessions and projects, the documentation and the cards of plugins.")), `${locale}: ${on}`);
    assert.ok(on.includes(`>${translate(locale, "Built-in")}<`), on);
    assert.ok(on.includes(translate(locale, "Canvas: {title} ({scope})", { title: "Welcome", scope: translate(locale, "Application") })), on);
    // A switch and nothing else: a plugin that ships with CodeAlta is neither edited nor removed.
    const toggle = new RegExp(`<input[^>]*aria-label="${translate(locale, "Enable {name}", { name: "Landing page" })}"[^>]*>`, "u");
    assert.match(on, toggle);
    assert.ok(/checked=""/u.test(toggle.exec(on)![0]), on);
    assert.ok(!/checked=""/u.test(toggle.exec(draw(configured))![0]), "turned off by configuration");
    assert.ok(!on.includes("bp6-intent-danger") && !on.includes(translate(locale, "Edit {name}", { name: "Landing page" })), on);
  }
  if (locales.length > 1) assert.notEqual(translate(locales[1], "The welcome page: recent sessions and projects, the documentation and the cards of plugins."), english("The welcome page: recent sessions and projects, the documentation and the cards of plugins."));
});
