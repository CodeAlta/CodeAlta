import assert from "node:assert/strict";
import test from "node:test";
import type { MachineNewSessionChoices } from "#neoastra";
import type { SessionSettingsApi } from "../sessionSettings/sessionSettingsApi";
import { automationProjects, machineRunProviders, onMachine, sessionProviderName, sessionRowProviderName } from "./machineProviderNames";

const choices = (status = "ok"): Pick<MachineNewSessionChoices, "status" | "providers"> => ({ status, providers: [
  { key: "claude", name: "Claude (Mac mini)", isDefault: true, defaultModelId: "opus", models: [{ id: "opus", name: "Opus" }, { id: "haiku", name: "Haiku" }] },
  { key: "local", name: "Ollama", isDefault: false, defaultModelId: null, models: [] },
] });

test("A session of another machine names its provider as that machine does, never as a provider of this computer with the same key", () => {
  const remote = { current: { providerKey: "claude" }, providerName: "Claude (Mac mini)", machineProvider: true };
  assert.equal(sessionProviderName("claude", remote, "Claude Business"), "Claude (Mac mini)");
  assert.equal(sessionProviderName("claude", { ...remote, providerName: null }, "Claude Business"), "claude", "its key, not this computer's name");
  // A session of this computer keeps the name of its configuration.
  assert.equal(sessionProviderName("claude", { current: { providerKey: "claude" }, providerName: "Claude" }, "Claude Business"), "Claude Business");
  assert.equal(sessionProviderName("claude", { current: { providerKey: "claude" }, providerName: "Claude" }, null), "Claude");
  assert.equal(sessionProviderName(null, undefined, null), null);
});

test("The row of a session of another machine names its provider as that machine does", () => {
  assert.equal(sessionRowProviderName({ providerKey: "claude-code", machineId: "mac", machineProviderName: "Claude on the build box" }, "Claude here"), "Claude on the build box");
  assert.equal(sessionRowProviderName({ providerKey: "claude-code", machineId: "mac" }, "Claude here"), "claude-code", "its key, never this computer's name");
  assert.equal(sessionRowProviderName({ providerKey: "claude-code" }, "Claude here"), "Claude here", "a session of this computer keeps the name of its configuration");
  assert.equal(sessionRowProviderName({ providerKey: null }, null), null);
});

test("An automation placed on a machine runs with the providers and models of that machine", async () => {
  assert.deepEqual(machineRunProviders(choices()), [
    { id: "claude", name: "Claude (Mac mini)", isDefault: true, defaultModel: "opus", defaultReasoning: null },
    { id: "local", name: "Ollama", isDefault: false, defaultModel: null, defaultReasoning: null },
  ]);
  assert.equal(machineRunProviders(choices("machine_offline")), null, "a machine that cannot say leaves the providers of this computer");
  assert.equal(machineRunProviders(null), null);
  const base: SessionSettingsApi = { key: "here", models: async () => ({ models: [{ id: "local-model", efforts: [], defaultEffort: null }], order: "name" }), prompts: async () => null,
    permissionModes: async () => null, reviewSetting: async () => null, sandbox: async () => null };
  const placed = onMachine(base, "mac", choices());
  assert.notEqual(placed.key, base.key, "catalogs of another machine are read again");
  assert.deepEqual(await placed.models("claude", new AbortController().signal),
    { models: [{ id: "opus", name: "Opus", efforts: [], defaultEffort: null }, { id: "haiku", name: "Haiku", efforts: [], defaultEffort: null }], order: null },
    "the machine says nothing of their order: they are listed as it gives them");
  assert.deepEqual(await placed.models("unknown", new AbortController().signal), { models: [], order: null });
});

test("Runs in lists the projects of this computer, not the rows that stand for machines", () => {
  const projects = [{ id: "api", archived: false }, { id: "machine:mac", archived: false, machineId: "mac" }, { id: "old", archived: true }];
  assert.deepEqual(automationProjects(projects).map(project => project.id), ["api"]);
});
