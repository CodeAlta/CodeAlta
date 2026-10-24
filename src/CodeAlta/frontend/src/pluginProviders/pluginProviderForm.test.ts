import assert from "node:assert/strict";
import test from "node:test";
import type { GlobalConfigPluginProviderType, GlobalConfigProvider } from "#neoastra";
import { providerForm, providerFormDirty, providerProblem, isSignedOut } from "../providerForm";
import { providerBrand } from "../brands";
import { downloadSize, pluginFieldValues, pluginFieldsProblem, pluginSettingValues, pluginType, samePluginFieldValues } from "./pluginProviderForm";
import { terminalCommandLine } from "./terminalCommand";

const acp: GlobalConfigPluginProviderType = {
  type: "acp", name: "ACP agent", tag: "ACP", permissionModes: ["default", "acceptEdits", "bypassPermissions"], hasCatalog: true, catalogTitle: "ACP registry", hasAccount: true,
  settings: [
    { key: "command", label: "Command", kind: "path", description: null, placeholder: "npx", advanced: false, readOnly: false },
    { key: "arguments", label: "Arguments", kind: "list", description: null, placeholder: null, advanced: false, readOnly: false },
    { key: "env", label: "Environment", kind: "map", description: null, placeholder: null, advanced: true, readOnly: false },
    { key: "agent_version", label: "Version", kind: "text", description: null, placeholder: null, advanced: true, readOnly: true },
  ],
};

const copilot: GlobalConfigProvider = {
  key: "github-copilot-cli", type: "acp", enabled: true, displayName: "Copilot", effectiveName: "Copilot", model: null, reasoningEffort: null, apiUrl: null,
  effectiveApiUrl: null, apiKeyEnv: null, hasApiKey: false, defaults: { displayName: null, model: null, reasoningEffort: null, apiUrl: null, apiKeyEnv: null },
  icon: null, color: null, anthropicApiKey: null, permissionMode: null, promptSuggestions: false, configDir: null, command: null,
  plugin: {
    tag: "ACP", iconData: "data:image/svg+xml;base64,PHN2Zy8+",
    values: [
      { key: "command", text: "npx" },
      { key: "arguments", list: ["--yes", "@github/copilot@1.0.103", "--acp"] },
      { key: "env", map: [{ name: "DEBUG", value: "1" }] },
      { key: "agent_version", text: "1.0.103" },
    ],
  },
};

test("the settings of a provider of a plugin are edited as lines", () => {
  const values = pluginFieldValues(copilot);
  assert.deepEqual(values, { command: "npx", arguments: "--yes\n@github/copilot@1.0.103\n--acp", env: "DEBUG=1", agent_version: "1.0.103" });
  assert.deepEqual(providerForm(copilot, null, ["acp"]).plugin, values);
  assert.equal(pluginType([acp], "acp"), acp);
  assert.equal(pluginType([acp], "openai-chat"), null);
});

test("the settings are sent back by kind, without the read-only ones and the blank lines", () => {
  const sent = pluginSettingValues(acp, { command: "  copilot ", arguments: "--acp\n\n  ", env: "A=1\nB = two=2\n\n", agent_version: "9" });
  assert.deepEqual(sent, [
    { key: "command", text: "copilot" },
    { key: "arguments", list: ["--acp"] },
    { key: "env", map: [{ name: "A", value: "1" }, { name: "B", value: " two=2" }] },
  ]);
  assert.deepEqual(pluginSettingValues(acp, {})[0], { key: "command", text: null });
  assert.equal(pluginFieldsProblem(acp, { env: "=1" }), "A line of the environment has no name before =.");
  assert.equal(pluginFieldsProblem(acp, { env: "A=1" }), null);
});

test("a form of a provider of a plugin is dirty only when a value changed", () => {
  const form = providerForm(copilot, null, ["acp"]);
  assert.equal(providerFormDirty({ ...form, plugin: { ...form.plugin } }, form), false);
  assert.equal(providerFormDirty({ ...form, plugin: { ...form.plugin, command: "copilot" } }, form), true);
  assert.equal(samePluginFieldValues({ a: "" }, {}), true);
});

test("an agent that is not signed in or not installed is said so", () => {
  assert.equal(isSignedOut("acp-signed-out"), true);
  assert.match(providerProblem("acp-signed-out")!, /not signed in/);
  assert.match(providerProblem("acp-not-installed")!, /was not found/);
  assert.match(providerProblem("acp-unavailable")!, /did not start/);
});

test("an agent of no known brand is shown with the icon of its plugin, a known one with its logo", () => {
  assert.deepEqual(providerBrand({ key: "cursor", type: "acp", name: "Cursor", iconData: copilot.plugin!.iconData }), { icon: null, image: copilot.plugin!.iconData! });
  assert.deepEqual(providerBrand({ key: "github-copilot-cli", type: "acp", name: "Copilot", iconData: copilot.plugin!.iconData }), { icon: "githubcopilot" });
  // A picture that is not the SVG the host rebuilt is not drawn.
  assert.deepEqual(providerBrand({ key: "cursor", type: "acp", iconData: "javascript:alert(1)" }), { icon: null });
});

test("the sign-in command is written for the shell of the system", () => {
  assert.equal(terminalCommandLine(["C:\\Program Files\\copilot.exe", "login"], [], true), "& 'C:\\Program Files\\copilot.exe' login");
  assert.equal(terminalCommandLine(["npx", "--yes", "@scope/agent@1.0", "--login"], [{ name: "A", value: "x y" }], true), "$env:A='x y'; npx --yes @scope/agent@1.0 --login");
  assert.equal(terminalCommandLine(["/usr/bin/agent", "log in"], [{ name: "A", value: "1" }], false), "A=1 /usr/bin/agent 'log in'");
  assert.equal(terminalCommandLine([], [], false), "");
});

test("a download is counted in KB or MB", () => {
  assert.equal(downloadSize(0), "0 KB");
  assert.equal(downloadSize(2048), "2 KB");
  assert.equal(downloadSize(5 * 1024 * 1024), "5.0 MB");
});
