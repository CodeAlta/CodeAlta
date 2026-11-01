import assert from "node:assert/strict";
import test from "node:test";
import type { SettingsDescriptor } from "#neoastra";
import { builtInSettings, choiceText, isMessageKey, mergeSettings, settingText } from "./settingsCatalog";
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
    "storage.session_retention.unit", "search.semantic", "search.model"];
  for (const id of protectedIds) assert.equal(builtInSettings.find(entry => entry.id === id)?.agents, "read", `${id} is changed by the user alone`);
});

test("the host says which built-in settings are not here, and adds those of the plugins, whose texts are their own", () => {
  const plugin: SettingsDescriptor = { id: "plugins.builds.poll_minutes", label: "Check every", help: "Minutes between two looks.", page: "plugin/source:builds", section: "Builds",
    kind: "integer", choices: [], minimum: 1, maximum: 120, default: "5", owner: "plugin", agents: "readWrite", writable: true, managedBy: null, applies: "now", scopes: ["user"],
    keywords: ["refresh"], plugin: "builds", pluginName: "Builds", available: true };
  const unavailable = { ...plugin, id: "general.keep_awake", label: "Keep computer awake when CodeAlta is running", available: false, plugin: null, pluginName: null };
  const merged = mergeSettings([plugin, unavailable]);
  assert.equal(merged.length, builtInSettings.length + 1);
  assert.equal(merged.find(entry => entry.id === "general.keep_awake")?.available, false);
  const added = merged.at(-1)!;
  assert.deepEqual([added.id, added.builtIn, added.defaultValue, added.pluginName], ["plugins.builds.poll_minutes", false, 5, "Builds"]);
  assert.equal(mergeSettings(null), builtInSettings, "without the host, the built-in settings alone");
});

test("a built-in text is translated, a plugin's text and the name of a language are shown as they are", () => {
  assert.equal(settingText("fr", "Theme", true), "Thème");
  assert.equal(settingText("fr", "Theme", false), "Theme");
  const language = builtInSettings.find(entry => entry.id === "general.language")!;
  assert.equal(choiceText("fr", language.choices.find(choice => choice.value === "de")!, true), "Deutsch");
  assert.equal(choiceText("fr", language.choices.find(choice => choice.value === "auto")!, true), "Automatique");
});
