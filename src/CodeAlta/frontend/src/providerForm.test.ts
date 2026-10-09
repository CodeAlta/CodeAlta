import assert from "node:assert/strict";
import test from "node:test";
import type { GlobalConfigProvider } from "#neoastra";
import { providerEdit, providerForm, providerFormDirty, providerProblem, runsOwnCli, usesAccountSignIn, validateProviderForm } from "./providerForm";

const types = ["openai-chat", "anthropic", "codex"];
const local: GlobalConfigProvider = { key: "local", type: "openai-chat", enabled: true, displayName: "Local", effectiveName: "Local", model: "model-a",
  reasoningEffort: null, apiUrl: "http://127.0.0.1:9999/v1", effectiveApiUrl: "http://127.0.0.1:9999/v1", apiKeyEnv: null, hasApiKey: true,
  defaults: { displayName: "local", model: null, reasoningEffort: null, apiUrl: null, apiKeyEnv: null }, icon: null, color: null, anthropicApiKey: null,
  permissionMode: null };

test("a form starts from the written values, never from the stored secret", () => {
  const form = providerForm(local, "local", types);
  assert.deepEqual(form, { key: "local", type: "openai-chat", enabled: true, displayName: "Local", model: "model-a", reasoningEffort: "",
    apiUrl: "http://127.0.0.1:9999/v1", apiKeyEnv: "", apiKey: "", clearApiKey: false, makeDefault: true, icon: "", color: "", anthropicApiKey: "", permissionMode: "" });
  assert.equal(providerForm(local, "other", types).makeDefault, false);
  assert.deepEqual(providerForm(null, "local", types), { key: "", type: "openai-chat", enabled: true, displayName: "", model: "", reasoningEffort: "",
    apiUrl: "", apiKeyEnv: "", apiKey: "", clearApiKey: false, makeDefault: false, icon: "", color: "", anthropicApiKey: "", permissionMode: "" });
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
  assert.equal(validateProviderForm({ ...form, color: "red" }, others, "local"), "A color is written as #rgb or #rrggbb.");
  assert.equal(validateProviderForm({ ...form, color: " #0AF " }, others, "local"), null);
});

test("the wire edit sends blank fields as null and keeps an untouched secret", () => {
  const form = providerForm(local, "local", types);
  assert.deepEqual(providerEdit({ ...form, key: " Local ", model: "  " }), { key: "local", type: "openai-chat", enabled: true, displayName: "Local",
    model: null, reasoningEffort: null, apiUrl: "http://127.0.0.1:9999/v1", apiKeyEnv: null, apiKey: null, clearApiKey: false, icon: null, color: null, anthropicApiKey: null,
    permissionMode: null });
  assert.equal(providerEdit({ ...form, apiKey: "new-secret" }).apiKey, "new-secret");
  const cleared = providerEdit({ ...form, apiKey: "ignored", clearApiKey: true });
  assert.equal(cleared.apiKey, null);
  assert.equal(cleared.clearApiKey, true);
  assert.equal(usesAccountSignIn("codex"), true);
  assert.equal(usesAccountSignIn("openai-chat"), false);
});

test("the icon and its color are written as chosen, and left out when they follow the provider", () => {
  const chosen: GlobalConfigProvider = { ...local, icon: "mistral", color: "#FA520F" };
  const form = providerForm(chosen, "local", types);
  assert.deepEqual([form.icon, form.color], ["mistral", "#FA520F"]);
  assert.deepEqual([providerEdit(form).icon, providerEdit(form).color], ["mistral", "#FA520F"]);
  assert.equal(providerFormDirty({ ...form, icon: "rocket" }, form), true);
  const cleared = providerEdit({ ...form, icon: " ", color: "" });
  assert.deepEqual([cleared.icon, cleared.color], [null, null]);
  assert.equal(providerEdit({ ...form, icon: " Rocket " }).icon, "rocket");
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

test("the choice for ANTHROPIC_API_KEY is read and sent for a provider that runs Claude Code only", () => {
  const claude: GlobalConfigProvider = { ...local, type: "claude-code", apiUrl: null, effectiveApiUrl: null, hasApiKey: false, anthropicApiKey: "ignore" };
  const form = providerForm(claude, "local", types);
  assert.equal(form.anthropicApiKey, "ignore");
  assert.equal(providerEdit(form).anthropicApiKey, "ignore");
  assert.equal(providerFormDirty({ ...form, anthropicApiKey: "use" }, form), true);
  assert.equal(providerEdit({ ...form, anthropicApiKey: "" }).anthropicApiKey, null, "blank follows the answer Claude Code saved");
  assert.equal(providerEdit({ ...form, type: "anthropic" }).anthropicApiKey, null, "another type does not take it");
});

test("the permission mode is read, sent for a CLI provider and left out for every other type", () => {
  const cli: GlobalConfigProvider = { ...local, type: "claude-code", permissionMode: "auto" };
  const form = providerForm(cli, "local", types);
  assert.equal(form.permissionMode, "auto");
  assert.equal(providerEdit(form).permissionMode, "auto");
  assert.equal(providerFormDirty({ ...form, permissionMode: "plan" }, form), true);
  // Blank means "leave it to the settings of the CLI".
  assert.equal(providerEdit({ ...form, permissionMode: "  " }).permissionMode, null);
  // A mode left in the form by a type that was changed away from the CLI is not sent: the file refuses it there.
  assert.equal(providerEdit({ ...form, type: "anthropic" }).permissionMode, null);
  // A provider that has no mode reads as blank, not null.
  assert.equal(providerForm({ ...cli, permissionMode: null }, "local", types).permissionMode, "");
});

test("a failed test says what to do when the host names its reason", () => {
  assert.match(providerProblem("claude-code-signed-out") ?? "", /\/login/);
  assert.match(providerProblem("claude-code-not-found") ?? "", /not found.*Install/);
  assert.match(providerProblem("claude-code-unavailable") ?? "", /did not start/);
  assert.equal(providerProblem("something-else"), null);
  assert.equal(providerProblem(null), null);
  assert.equal(providerProblem(undefined), null, "a host that gives no reason keeps the plain result");
});
