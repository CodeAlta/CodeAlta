import assert from "node:assert/strict";
import test from "node:test";
import { defaultProviderListPreferences, groupProviders, orderProviders, persistProviderListPreferences, renameFavoriteProvider, restoreProviderListPreferences,
  setFavoriteProvider, type ProviderListPreferences } from "./providerListOrder";

const providers = [{ key: "anthropic", enabled: false }, { key: "codex", enabled: true }, { key: "copilot", enabled: false },
  { key: "claude-code", enabled: true }, { key: "openai", enabled: false }];
const keys = (list: readonly { key: string }[]) => list.map(provider => provider.key);

test("the enabled providers come first by default, each group in the order of the configuration", () => {
  assert.equal(defaultProviderListPreferences.enabledFirst, true);
  assert.deepEqual(groupProviders(providers, defaultProviderListPreferences).map(group => [group.kind, keys(group.providers)]),
    [["enabled", ["codex", "claude-code"]], ["disabled", ["anthropic", "copilot", "openai"]]]);
  // Without the option, the list is the configuration.
  assert.deepEqual(groupProviders(providers, { ...defaultProviderListPreferences, enabledFirst: false }).map(group => [group.kind, keys(group.providers)]),
    [["others", keys(providers)]]);
});

test("the favorite providers come before all others, the enabled ones first", () => {
  let preferences: ProviderListPreferences = defaultProviderListPreferences;
  preferences = setFavoriteProvider(preferences, "OpenAI", true);
  preferences = setFavoriteProvider(preferences, "claude-code", true);
  assert.deepEqual(groupProviders(providers, preferences).map(group => [group.kind, keys(group.providers)]),
    [["favorites", ["claude-code", "openai"]], ["enabled", ["codex"]], ["disabled", ["anthropic", "copilot"]]]);
  assert.deepEqual(keys(orderProviders(providers, { ...preferences, enabledFirst: false })), ["claude-code", "openai", "anthropic", "codex", "copilot"]);
  // A group left empty is not listed.
  assert.deepEqual(groupProviders(providers.filter(provider => provider.key !== "codex"), preferences).map(group => group.kind), ["favorites", "disabled"]);
  // Twice the same change returns the same preferences.
  assert.equal(setFavoriteProvider(preferences, "openai", true), preferences);
  assert.deepEqual(setFavoriteProvider(preferences, "openai", false).favorites, ["claude-code"]);
});

test("a favorite provider given another key stays a favorite", () => {
  const preferences = setFavoriteProvider(defaultProviderListPreferences, "codex", true);
  assert.deepEqual(renameFavoriteProvider(preferences, "codex", "codex-work").favorites, ["codex-work"]);
  assert.equal(renameFavoriteProvider(preferences, "openai", "openai-2"), preferences);
});

test("the preferences are kept between starts, and what cannot be read gives the defaults", () => {
  let stored = "";
  const preferences = { enabledFirst: false, favorites: ["codex"] };
  assert.equal(persistProviderListPreferences(value => { stored = value; }, preferences), true);
  assert.deepEqual(restoreProviderListPreferences(() => stored), preferences);
  assert.equal(restoreProviderListPreferences(() => null), defaultProviderListPreferences);
  assert.equal(restoreProviderListPreferences(() => "{"), defaultProviderListPreferences);
  assert.equal(restoreProviderListPreferences(() => { throw new Error("no storage"); }), defaultProviderListPreferences);
  assert.deepEqual(restoreProviderListPreferences(() => JSON.stringify({ favorites: ["Codex", 3, "", "codex"] })), { enabledFirst: true, favorites: ["codex"] });
  assert.equal(persistProviderListPreferences(() => { throw new Error("full"); }, preferences), false);
});
