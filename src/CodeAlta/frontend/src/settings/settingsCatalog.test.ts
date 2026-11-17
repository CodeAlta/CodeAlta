import assert from "node:assert/strict";
import test from "node:test";
import type { SettingsDescriptor } from "#neoastra";
import { builtInSettings, choiceText, isBuiltInPluginKey, isMessageKey, mergeSettings, pluginHtmlText, pluginShortText, settingText } from "./settingsCatalog";
import { settingsGroups, settingsPages } from "./settingsPages";
import { windowSettingIds } from "./windowSettings";

test("every text of the catalog is a text of the window, so the search finds a setting in every language", () => {
  const missing: string[] = [];
  const check = (where: string, text: string | null | undefined) => { if (text && !isMessageKey(text)) missing.push(`${where}: ${text}`); };
  for (const group of settingsGroups) {
    check("group", group.group);
    for (const page of group.pages) check(`page ${page.id}`, page.label);
  }
  for (const entry of builtInSettings) {
    check(`${entry.id} label`, entry.label);
    check(`${entry.id} help`, entry.help);
    check(`${entry.id} section`, entry.section);
    for (const choice of entry.choices) if (!choice.literal) check(`${entry.id} choice ${choice.value}`, choice.label);
  }
  assert.deepEqual(missing, []);
});

test("every setting is on a page of Settings, has a unique id, and has a default where it holds one value", () => {
  const pages = new Set(settingsPages.map(page => page.id as string));
  const ids = new Set<string>();
  for (const entry of builtInSettings) {
    assert.ok(pages.has(entry.page), `${entry.id} is on ${entry.page}, which is no page of Settings`);
    assert.ok(!ids.has(entry.id), `${entry.id} is declared twice`);
    ids.add(entry.id);
    assert.match(entry.id, /^[a-z][a-z0-9_]*(\.[a-z0-9_]+)+$/, `${entry.id} is a dotted id`);
    assert.ok(entry.agents === "readWrite" || entry.agents === "read", `${entry.id} says what agents may do`);
    if (entry.kind === "collection") assert.equal(entry.defaultValue, null, `${entry.id} is a list of items, without a value`);
    if (entry.kind === "choice" && entry.choices.length && entry.defaultValue !== null)
      assert.ok(entry.choices.some(choice => choice.value === entry.defaultValue), `the default of ${entry.id} is one of its choices`);
  }
});

test("the settings the window keeps are exactly those its bindings name", () => {
  const kept = builtInSettings.filter(entry => entry.owner === "window").map(entry => entry.id).sort();
  assert.deepEqual(kept, [...windowSettingIds].sort());
});

test("what protects the user is read only for agents", () => {
  const protectedIds = ["permissions.default_mode", "permissions.created_sessions", "permissions.confirm_session_delete", "permissions.confirm_project_archive",
    "permissions.confirm_plugin_stop", "tools.disabled", "review.before_commit", "sandbox.all_sessions", "code_mode.mode", "mcp_host.server", "mcp_host.tailnet",
    "general.reconnect_remote_control", "storage.clear_scratchpad_on_startup", "storage.session_retention.enabled", "storage.session_retention.amount",
    "storage.session_retention.unit", "search.semantic", "search.model", "external_writing.attribution", "external_writing.instruction"];
  for (const id of protectedIds) assert.equal(builtInSettings.find(entry => entry.id === id)?.agents, "read", `${id} is changed by the user alone`);
});

test("the host says which built-in settings are not here, and adds those of the plugins, whose texts are their own", () => {
  const plugin: SettingsDescriptor = { id: "plugins.builds.poll_minutes", label: "Check every", help: "Minutes between two looks.", page: "plugin/source:builds", section: "Builds",
    kind: "integer", choices: [], minimum: "1", maximum: "120", default: "5", owner: "plugin", agents: "readWrite", writable: true, managedBy: null, applies: "now", scopes: ["user"],
    keywords: ["refresh"], plugin: "builds", pluginName: "Builds", available: true, project: null };
  const unavailable = { ...plugin, id: "general.keep_awake", label: "Keep computer awake when CodeAlta is running", available: false, plugin: null, pluginName: null };
  const merged = mergeSettings([plugin, unavailable]);
  assert.equal(merged.length, builtInSettings.length + 1);
  assert.equal(merged.find(entry => entry.id === "general.keep_awake")?.available, false);
  const added = merged.at(-1)!;
  assert.deepEqual([added.id, added.builtIn, added.defaultValue, added.pluginName, added.minimum, added.maximum], ["plugins.builds.poll_minutes", false, 5, "Builds", 1, 120]);
  assert.equal(mergeSettings(null), builtInSettings, "without the host, the built-in settings alone");
});

test("a bound that is a fraction stays a fraction, and a plugin of a project names its project", () => {
  const ratio: SettingsDescriptor = { id: "plugins.builds.ratio", label: "Ratio", help: null, page: "plugin/source:builds", section: null, kind: "number", choices: [],
    minimum: "0.1", maximum: "0.4", default: "0.2", owner: "plugin", agents: "readWrite", writable: true, managedBy: null, applies: "now", scopes: ["project"],
    keywords: [], plugin: "builds", pluginName: "Builds", available: true, project: "p1" };
  const entry = mergeSettings([ratio]).at(-1)!;
  assert.deepEqual([entry.minimum, entry.maximum, entry.project], [0.1, 0.4, "p1"]);
});

test("the texts of a plugin that ships with CodeAlta are translated where they can be, those of another plugin are its own", () => {
  const shipped: SettingsDescriptor = { id: "plugins.pseudonymizer.hide_places", label: "Places", help: "A help no dictionary has.", page: "plugin/builtin:pseudonymizer",
    section: "What is hidden", kind: "choice", choices: [{ value: "french", label: "French" }], minimum: null, maximum: null, default: "true", owner: "plugin", agents: "read",
    writable: true, managedBy: null, applies: "now", scopes: ["user"], keywords: [], plugin: "pseudonymizer", pluginName: "Pseudonymizer", available: true, project: null };
  const other = { ...shipped, id: "plugins.mine.places", page: "plugin/source:mine", plugin: "mine", pluginName: "Mine" };
  const [ours, theirs] = mergeSettings([shipped, other]).slice(-2);
  assert.deepEqual([settingText("fr", ours.label, ours.builtIn), settingText("fr", ours.section!, ours.builtIn), settingText("fr", ours.help!, ours.builtIn)],
    ["Lieux", "Ce qui est masqué", "A help no dictionary has."], "a text without a translation stays as it is written");
  assert.equal(choiceText("fr", ours.choices[0], ours.builtIn), "Français");
  assert.deepEqual([settingText("fr", theirs.label, theirs.builtIn), choiceText("fr", theirs.choices[0], theirs.builtIn)], ["Places", "French"]);
});

test("a built-in text is translated, a plugin's text and the name of a language are shown as they are", () => {
  assert.equal(settingText("fr", "Theme", true), "Thème");
  assert.equal(settingText("fr", "Theme", false), "Theme");
  const language = builtInSettings.find(entry => entry.id === "general.language")!;
  assert.equal(choiceText("fr", language.choices.find(choice => choice.value === "de")!, true), "Deutsch");
  assert.equal(choiceText("fr", language.choices.find(choice => choice.value === "auto")!, true), "Automatique");
});

test("the dialog of a built-in plugin is translated, its data is not", () => {
  const html = "<p>The provider read these pseudonyms; the values stay on this machine, encrypted.</p><h3>Pseudonyms</h3>"
    + "<table><thead><tr><th>Pseudonym</th><th>Value</th><th>Times sent</th></tr></thead>"
    + "<tbody><tr><td><code>[LIEU_001]</code></td><td>Value</td><td>2</td></tr></tbody></table>";
  assert.equal(pluginHtmlText("fr", html, true),
    "<p>Le fournisseur a lu ces pseudonymes ; les valeurs restent sur cette machine, chiffrées.</p><h3>Pseudonymes</h3>"
    + "<table><thead><tr><th>Pseudonyme</th><th>Valeur</th><th>Nombre d'envois</th></tr></thead>"
    + "<tbody><tr><td><code>[LIEU_001]</code></td><td>Value</td><td>2</td></tr></tbody></table>", "a value a user wrote stays as it is, even a text of the window");
  assert.equal(pluginHtmlText("fr", html, false), html, "the texts of a plugin of the user are shown as written");
  assert.equal(pluginHtmlText("fr", "<p> Your text </p>", true), "<p> Ton texte </p>", "the spaces around a text stay");
  assert.deepEqual([isBuiltInPluginKey("builtin:pseudonymizer"), isBuiltInPluginKey("my-plugin"), isBuiltInPluginKey(null)], [true, false, false]);
});

test("the status of a built-in plugin is translated part by part, its numbers kept", () => {
  assert.equal(pluginShortText("fr", "3/3 · 1 unavailable · tools not loaded", true), "3/3 · 1 indisponibles · outils non chargés");
  assert.equal(pluginShortText("fr", "31 values hidden", true), "31 valeurs masquées");
  assert.equal(pluginShortText("fr", "downloading the model 42 %", true), "téléchargement du modèle 42 %");
  assert.equal(pluginShortText("fr", "31 values hidden", false), "31 values hidden", "the status of a plugin of the user is shown as written");
  assert.equal(pluginShortText("fr", "Pseudonymizer", true), "Pseudonymizer", "a name stays");
});
