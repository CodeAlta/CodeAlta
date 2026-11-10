import assert from "node:assert/strict";
import test from "node:test";
import type { MachineNewSessionChoices } from "#neoastra";
import type { SessionSettingsApi } from "../sessionSettings/sessionSettingsApi";
import { automationProjects, machineRunProviders, onMachine, sessionProviderName, sessionRowProviderName } from "./machineProviderNames";

// An older machine says only its providers and models.
const older = { efforts: null, defaultEffort: null }, olderProvider = { permissionModes: null, defaultPermissionMode: null, sandbox: null };
const choices = (status = "ok"): Pick<MachineNewSessionChoices, "status" | "providers"> => ({ status, providers: [
  { key: "claude", name: "Claude (Mac mini)", isDefault: true, defaultModelId: "opus", models: [{ id: "opus", name: "Opus", ...older }, { id: "haiku", name: "Haiku", ...older }], ...olderProvider },
  { key: "local", name: "Ollama", isDefault: false, defaultModelId: null, models: [], ...olderProvider },
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
  const here = { prompts: [{ id: "local", name: "Local" }], defaultPromptId: "local" };
  const base: SessionSettingsApi = { key: "here", models: async () => ({ models: [{ id: "local-model", efforts: [], defaultEffort: null }], order: "name" }), prompts: async () => here,
    permissionModes: async () => ({ modes: [{ id: "default" }], defaultMode: "default" }), reviewSetting: async () => true, sandbox: async () => null };
  const signal = new AbortController().signal;
  const placed = onMachine(base, "mac", choices());
  assert.notEqual(placed.key, base.key, "catalogs of another machine are read again");
  assert.deepEqual(await placed.models("claude", signal),
    { models: [{ id: "opus", name: "Opus", efforts: [], defaultEffort: null, effortsComplete: null }, { id: "haiku", name: "Haiku", efforts: [], defaultEffort: null, effortsComplete: null }], order: null },
    "the machine says nothing of their order: they are listed as it gives them");
  assert.deepEqual(await placed.models("unknown", signal), { models: [], order: null });
  // What an older machine does not say is read from this computer for an automation, and left out of a new session.
  assert.deepEqual(await placed.prompts(signal), here);
  assert.equal(await placed.reviewSetting(signal), true);
  assert.equal(await onMachine(null, "mac", choices()).prompts(signal), null);
  assert.equal(await onMachine(null, "mac", choices()).permissionModes("claude", signal), null);
  assert.equal(await onMachine(null, "mac", choices()).reviewSetting(signal), null, "the review of a session of another machine is not chosen here");
});

test("A machine that says them gives its efforts, agent prompts, permission modes and sandbox profiles", async () => {
  const signal = new AbortController().signal;
  const machine = onMachine(null, "mac", {
    providers: [{ key: "claude", name: "Claude", isDefault: true, defaultModelId: "opus", models: [{ id: "opus", name: "Opus", efforts: ["Low", "High"], defaultEffort: "High" }],
      permissionModes: ["default", "acceptEdits"], defaultPermissionMode: "default", sandbox: "workspace" }],
    prompts: [{ id: "default", name: "Default" }, { id: "review", name: "Review" }], defaultPromptId: "default", sandboxProfiles: ["workspace", "read-only"],
  });
  assert.deepEqual(await machine.models("claude", signal), { models: [{ id: "opus", name: "Opus", efforts: ["Low", "High"], defaultEffort: "High", effortsComplete: true }], order: null });
  assert.deepEqual(await machine.prompts(signal), { prompts: [{ id: "default", name: "Default" }, { id: "review", name: "Review" }], defaultPromptId: "default" });
  assert.deepEqual(await machine.permissionModes("claude", signal), { modes: [{ id: "default" }, { id: "acceptEdits" }], defaultMode: "default" });
  assert.deepEqual(await machine.sandbox("claude", "opus", signal),
    { status: "ok", own: null, problem: null, profile: "workspace", source: "machine", choices: ["off", "workspace", "read-only"] });
  const unsandboxed = onMachine(null, "mac", { providers: [], sandboxProfiles: [] });
  assert.equal(await unsandboxed.sandbox("claude", null, signal), null, "a machine that cannot start a sandbox offers none");
});

test("Runs in lists the projects of this computer, not the rows that stand for machines", () => {
  const projects = [{ id: "api", archived: false }, { id: "machine:mac", archived: false, machineId: "mac" }, { id: "old", archived: true }];
  assert.deepEqual(automationProjects(projects).map(project => project.id), ["api"]);
});
