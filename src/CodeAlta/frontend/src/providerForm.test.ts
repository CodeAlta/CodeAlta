import assert from "node:assert/strict";
import test from "node:test";
import type { GlobalConfigProvider } from "#neoastra";
import { modelsOrderLabel, modelsOrders, providerEdit, providerForm, providerFormDirty, providerProblem, providerStateProblem, runsOwnCli, usesAccountSignIn, validateProviderForm } from "./providerForm";

const types = ["openai-chat", "anthropic", "codex"];
const local: GlobalConfigProvider = { key: "local", type: "openai-chat", enabled: true, displayName: "Local", effectiveName: "Local", model: "model-a",
  reasoningEffort: null, apiUrl: "http://127.0.0.1:9999/v1", effectiveApiUrl: "http://127.0.0.1:9999/v1", apiKeyEnv: null, hasApiKey: true,
  defaults: { displayName: "local", model: null, reasoningEffort: null, apiUrl: null, apiKeyEnv: null }, icon: null, color: null, anthropicApiKey: null,
  permissionMode: null, configDir: null, command: null, environment: null, modelsOrder: null };

test("a form starts from the written values, never from the stored secret", () => {
  const form = providerForm(local, "local", types);
  assert.deepEqual(form, { key: "local", type: "openai-chat", enabled: true, displayName: "Local", model: "model-a", reasoningEffort: "",
    apiUrl: "http://127.0.0.1:9999/v1", apiKeyEnv: "", apiKey: "", clearApiKey: false, makeDefault: true, icon: "", color: "", anthropicApiKey: "", permissionMode: "", configDir: "", environment: "", modelsOrder: "", contextSize: "", plugin: {} });
  assert.equal(providerForm(local, "other", types).makeDefault, false);
  assert.deepEqual(providerForm(null, "local", types), { key: "", type: "openai-chat", enabled: true, displayName: "", model: "", reasoningEffort: "",
    apiUrl: "", apiKeyEnv: "", apiKey: "", clearApiKey: false, makeDefault: false, icon: "", color: "", anthropicApiKey: "", permissionMode: "", configDir: "", environment: "", modelsOrder: "", contextSize: "", plugin: {} });
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
    permissionMode: null, configDir: null, environment: null, modelsOrder: null, contextSize: null });
  assert.equal(providerEdit({ ...form, apiKey: "new-secret" }).apiKey, "new-secret");
  const cleared = providerEdit({ ...form, apiKey: "ignored", clearApiKey: true });
  assert.equal(cleared.apiKey, null);
  assert.equal(cleared.clearApiKey, true);
  assert.equal(usesAccountSignIn("codex"), true);
  assert.equal(usesAccountSignIn("openai-chat"), false);
});

test("the environment of Claude Code is NAME=value lines, sent for it alone", () => {
  const claude: GlobalConfigProvider = { ...local, key: "claude", type: "claude-code", apiUrl: null, effectiveApiUrl: null, hasApiKey: false,
    environment: [{ name: "CLAUDE_CODE_OAUTH_TOKEN", value: "sk-ant-oat01-a=b" }, { name: "FLAG", value: "" }] };
  const form = providerForm(claude, null, types);
  assert.equal(form.environment, "CLAUDE_CODE_OAUTH_TOKEN=sk-ant-oat01-a=b\nFLAG=");
  // A value keeps what follows its first `=`; a name alone has an empty value; blank lines are left out.
  assert.deepEqual(providerEdit({ ...form, environment: " A = x=y \n\nB\n" }).environment, [{ name: "A", value: " x=y" }, { name: "B", value: "" }]);
  assert.deepEqual(providerEdit({ ...form, environment: "" }).environment, [], "an empty field removes the variables");
  assert.equal(providerEdit({ ...form, type: "openai-chat" }).environment, null, "another type takes none");
  assert.equal(validateProviderForm({ ...form, environment: "=token" }, [], "claude"), "A line of the environment has no name before =.");
  assert.equal(validateProviderForm(form, [], "claude"), null);
  assert.equal(providerFormDirty({ ...form, environment: "A=1" }, form), true);
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

test("the size of context is read and sent in tokens, blank for the size of the model", () => {
  const form = providerForm({ ...local, contextSize: 1_000_000 }, "local", types);
  assert.equal(form.contextSize, "1000000");
  assert.equal(providerEdit(form).contextSize, 1_000_000);
  assert.equal(providerEdit({ ...form, contextSize: "" }).contextSize, null);
  assert.equal(providerFormDirty({ ...form, contextSize: "272000" }, form), true);
});

test("the order of the models is read and sent as the file names it, blank for the order of the type", () => {
  const form = providerForm({ ...local, modelsOrder: "name_version" }, "local", types);
  assert.equal(form.modelsOrder, "name_version");
  assert.equal(providerEdit(form).modelsOrder, "name_version");
  assert.equal(providerEdit({ ...form, modelsOrder: "" }).modelsOrder, null);
  assert.equal(providerFormDirty({ ...form, modelsOrder: "name" }, form), true);
  const t = (key: string) => `[${key}]`;
  assert.deepEqual(modelsOrders.map(order => modelsOrderLabel(order, t)), ["[Provider order]", "[By name]", "[By name, newest first]"]);
  assert.equal(modelsOrderLabel("release_date", t), "release_date", "an order of a newer version is shown as it is");
});

test("a failed test says what to do when the host names its reason", () => {
  assert.match(providerProblem("claude-code-signed-out") ?? "", /^\{provider\} is not signed in.*\/login/, "the account is named, not Claude Code");
  assert.match(providerProblem("claude-code-not-found") ?? "", /not found.*Download.*install/);
  assert.match(providerProblem("claude-code-unavailable") ?? "", /did not start/);
  assert.equal(providerProblem("something-else"), null);
  assert.equal(providerProblem(null), null);
  assert.equal(providerProblem(undefined), null, "a host that gives no reason keeps the plain result");
});

test("the folder of the account is read, checked and sent for a provider that runs Claude Code only", () => {
  const claude: GlobalConfigProvider = { ...local, type: "claude-code", apiUrl: null, effectiveApiUrl: null, hasApiKey: false, configDir: "~/.claude-business" };
  const form = providerForm(claude, "local", types);
  assert.equal(form.configDir, "~/.claude-business");
  assert.equal(providerEdit(form).configDir, "~/.claude-business");
  assert.equal(providerFormDirty({ ...form, configDir: "~/.claude-work" }, form), true);
  assert.equal(providerEdit({ ...form, configDir: " " }).configDir, null, "blank is the folder of Claude Code");
  assert.equal(providerEdit({ ...form, type: "anthropic" }).configDir, null, "another type does not take it");
  for (const folder of ["~", "~/x", "~\\x", "/home/me/.claude", "C:\\Users\\me\\.claude", "D:/claude"]) assert.equal(validateProviderForm({ ...form, configDir: folder }, [], "local"), null, folder);
  assert.equal(validateProviderForm({ ...form, configDir: ".claude-work" }, [], "local"), "A folder is a full path or starts with ~.");
  assert.equal(validateProviderForm({ ...form, type: "anthropic", configDir: ".claude-work" }, [], "local"), null, "another type ignores it");
});

test("the form of a provider that is not ready says why above its fields, and what to do", () => {
  const state = (availability: string, reason: string | null = null) => ({ availability, reason });
  // An agent that waits for a sign-in is not broken: the form asks for the sign-in below it.
  assert.deepEqual(providerStateProblem(state("Failed", "acp-signed-out"), true),
    { key: "{provider} is not signed in: sign in below, then test it again.", intent: "warning", signIn: true });
  assert.equal(providerStateProblem(state("Failed", "claude-code-signed-out"), true)?.intent, "warning");
  assert.equal(providerStateProblem(state("Failed", "codex-cli-signed-out"), true)?.signIn, true, "Codex CLI signs in from its form, as an agent does");
  assert.deepEqual(providerStateProblem(state("Failed", "acp-not-installed"), true),
    { key: "The command of {provider} was not found. Install the agent, or the tool that runs it, then test again.", intent: "danger", signIn: false });
  // A failure the window has no words for points to the log, which keeps what the provider said.
  assert.equal(providerStateProblem(state("Failed"), true)?.key, "{provider} did not start. Test it again; Settings > Application Logs says why.");
  assert.equal(providerStateProblem(state("Unsupported"), true)?.intent, "danger");
  // A provider that works, is disabled, or was not tried says nothing.
  assert.equal(providerStateProblem(state("Ready"), true), null);
  assert.equal(providerStateProblem(state("Failed"), false), null);
  assert.equal(providerStateProblem(state("Probing"), true), null);
  assert.equal(providerStateProblem(undefined, true), null);
});
