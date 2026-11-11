import assert from "node:assert/strict";
import test from "node:test";
import { defaultProviderListPreferences } from "./providerListOrder";
import { matchingProviders, providerPickerDetail, sectionProviders } from "./providerPicker";

const providers = [
  { id: "xiaomi", name: "xiaomi", type: "openai" },
  { id: "codex-cli", name: "codex-cli", type: "codex-cli" },
  { id: "claude-max5", name: "Claude Max", type: "claude-code" },
  { id: "antigravity-acp", name: "antigravity-acp", type: "acp" },
  { id: "model10", name: "Model 10", type: "openai" },
  { id: "model9", name: "Model 9", type: "openai" },
];
const ids = (list: readonly { id: string }[]) => list.map(provider => provider.id);

test("the provider list is sorted by name, then by key, the favorites of Settings first", () => {
  assert.deepEqual(sectionProviders(providers, "", defaultProviderListPreferences).favorites, []);
  assert.deepEqual(ids(sectionProviders(providers, "", defaultProviderListPreferences).others),
    ["antigravity-acp", "claude-max5", "codex-cli", "model9", "model10", "xiaomi"], "by the name Settings gives, numbers in their order");
  const sections = sectionProviders(providers, "", { ...defaultProviderListPreferences, favorites: ["xiaomi", "codex-cli"] });
  assert.deepEqual([ids(sections.favorites), ids(sections.others)], [["codex-cli", "xiaomi"], ["antigravity-acp", "claude-max5", "model9", "model10"]]);
  const same = [{ id: "b", name: "Same" }, { id: "a", name: "same" }];
  assert.deepEqual(ids(sectionProviders(same, "", defaultProviderListPreferences).others), ["a", "b"], "the same name: by key");
});

test("a search keeps the providers whose name, key or type holds every word", () => {
  assert.equal(matchingProviders(providers, "  "), providers);
  assert.deepEqual(ids(matchingProviders(providers, "CLAUDE")), ["claude-max5"], "by its type and its name");
  assert.deepEqual(ids(matchingProviders(providers, "max5")), ["claude-max5"], "by its key");
  assert.deepEqual(ids(matchingProviders(providers, "openai model")), ["model10", "model9"]);
  assert.deepEqual(ids(matchingProviders(providers, "gemini")), []);
  assert.deepEqual(ids(sectionProviders(providers, "codex", { ...defaultProviderListPreferences, favorites: ["codex-cli"] }).favorites), ["codex-cli"]);
});

test("beside its name, a provider shows its key and its type when they say more", () => {
  assert.equal(providerPickerDetail({ id: "claude-max5", name: "Claude Max", type: "claude-code" }), "claude-max5 · claude-code");
  assert.equal(providerPickerDetail({ id: "xiaomi", name: "xiaomi", type: "openai" }), "openai");
  assert.equal(providerPickerDetail({ id: "codex-cli", name: "codex-cli", type: "codex-cli" }), null);
  assert.equal(providerPickerDetail({ id: "codex", name: "Codex", type: "CODEX" }), null);
  assert.equal(providerPickerDetail({ id: "local", name: " ", type: null }), null, "a blank name is the key");
});
