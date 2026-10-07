import assert from "node:assert/strict";
import test from "node:test";
import type { GlobalConfigProvider } from "#neoastra";
import { providerEdit, providerForm, providerFormDirty, runsOwnCli, usesAccountSignIn, validateProviderForm } from "./providerForm";

const types = ["openai-chat", "anthropic", "codex"];
const local: GlobalConfigProvider = { key: "local", type: "openai-chat", enabled: true, displayName: "Local", effectiveName: "Local", model: "model-a",
  reasoningEffort: null, apiUrl: "http://127.0.0.1:9999/v1", effectiveApiUrl: "http://127.0.0.1:9999/v1", apiKeyEnv: null, hasApiKey: true,
  defaults: { displayName: "local", model: null, reasoningEffort: null, apiUrl: null, apiKeyEnv: null } };

test("a form starts from the written values, never from the stored secret", () => {
  const form = providerForm(local, "local", types);
  assert.deepEqual(form, { key: "local", type: "openai-chat", enabled: true, displayName: "Local", model: "model-a", reasoningEffort: "",
    apiUrl: "http://127.0.0.1:9999/v1", apiKeyEnv: "", apiKey: "", clearApiKey: false, makeDefault: true });
  assert.equal(providerForm(local, "other", types).makeDefault, false);
  assert.deepEqual(providerForm(null, "local", types), { key: "", type: "openai-chat", enabled: true, displayName: "", model: "", reasoningEffort: "",
    apiUrl: "", apiKeyEnv: "", apiKey: "", clearApiKey: false, makeDefault: false });
  assert.equal(providerFormDirty(form, form), false);
  assert.equal(providerFormDirty({ ...form, model: "model-b" }, form), true);
});

test("validation names the first problem and accepts a complete form", () => {
  const form = providerForm(local, "local", types);
  const others = [{ key: "local" }, { key: "spare" }];
  assert.equal(validateProviderForm(form, others, "local"), null);
  assert.equal(validateProviderForm({ ...form, key: " " }, others, "local"), "Enter a provider key.");
  assert.equal(validateProviderForm({ ...form, key: "bad key" }, others, "local"), "A provider key uses letters, digits, '-' or '_' (at most 64).");
  assert.equal(validateProviderForm({ ...form, key: "x".repeat(65) }, others, "local"), "A provider key uses letters, digits, '-' or '_' (at most 64).");
  assert.equal(validateProviderForm({ ...form, key: "SPARE" }, others, "local"), "Another provider already uses this key.");
  assert.equal(validateProviderForm({ ...form, key: "local" }, others, null), "Another provider already uses this key.", "a new provider cannot reuse a key");
  assert.equal(validateProviderForm({ ...form, apiUrl: "ftp://x" }, others, "local"), "The API URL must start with http:// or https://.");
  assert.equal(validateProviderForm({ ...form, enabled: false }, others, "local"), "Only an enabled provider can be the default.");
});

test("the wire edit sends blank fields as null and keeps an untouched secret", () => {
  const form = providerForm(local, "local", types);
  assert.deepEqual(providerEdit({ ...form, key: " Local ", model: "  " }), { key: "local", type: "openai-chat", enabled: true, displayName: "Local",
    model: null, reasoningEffort: null, apiUrl: "http://127.0.0.1:9999/v1", apiKeyEnv: null, apiKey: null, clearApiKey: false });
  assert.equal(providerEdit({ ...form, apiKey: "new-secret" }).apiKey, "new-secret");
  const cleared = providerEdit({ ...form, apiKey: "ignored", clearApiKey: true });
  assert.equal(cleared.apiKey, null);
  assert.equal(cleared.clearApiKey, true);
  assert.equal(usesAccountSignIn("codex"), true);
  assert.equal(usesAccountSignIn("openai-chat"), false);
});

test("a provider that runs its own CLI sends no key and no endpoint", () => {
  // The type was changed on a provider that had an endpoint and a stored key: neither belongs to the CLI.
  const form = { ...providerForm(local, "local", types), type: "claude-code", apiKey: "typed-before", apiKeyEnv: "SOME_KEY" };
  const edit = providerEdit(form);
  assert.equal(edit.type, "claude-code");
  assert.equal(edit.apiUrl, null);
  assert.equal(edit.apiKeyEnv, null);
  assert.equal(edit.apiKey, null);
  assert.equal(edit.clearApiKey, true);
  assert.equal(edit.model, "model-a");
  assert.equal(runsOwnCli("claude-code"), true);
  assert.equal(runsOwnCli("anthropic"), false);
  assert.equal(usesAccountSignIn("claude-code"), false);
});
