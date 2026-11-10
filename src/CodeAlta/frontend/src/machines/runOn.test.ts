import assert from "node:assert/strict";
import test from "node:test";
import type { MachineNewSessionChoices, MachineView } from "#neoastra";
import { defaultSessionSettings } from "../sessionSettings/sessionSettings";
import { canStartOn, newSessionProblem, noRemoteSelection, remoteSelection, remoteStartBlocker, remoteStartPrompt, remoteStartRequest, runOnMachine, runOnTargets } from "./runOn";

const machine = (change: Partial<MachineView> = {}): MachineView => ({ id: "m1", name: "mac-mini", state: "online", os: "macos", version: "4.0.0",
  address: "mac-mini:2585", fingerprint: "ABCD EFGH", words: "apple river stone cloud amber tiger", sessions: 2, lastSeen: null, problem: null,
  permissions: ["view", "drive", "start"], pairedAt: "2026-11-03T00:00:00Z", paired: true, channel: null, tags: [], working: null, queued: null, sessionLimit: null, ...change });

const choices = (change: Partial<MachineNewSessionChoices> = {}): MachineNewSessionChoices => ({
  status: "ok", machineName: "mac-mini", message: null, suggestedProjectId: "p-alta",
  projects: [{ id: "p-alta", name: "CodeAlta", repositoryKey: "github.com/codealta/codealta" }, { id: "p-notes", name: "notes", repositoryKey: null }],
  providers: [
    { key: "claude", name: "Claude Code", isDefault: false, defaultModelId: "opus", models: [model("opus", "Opus"), model("sonnet", "Sonnet")], ...modes },
    { key: "codex", name: "Codex", isDefault: true, defaultModelId: "gpt-mini", models: [model("gpt", "GPT"), model("gpt-mini", "GPT mini")], ...modes },
  ],
  prompts: [{ id: "default", name: "Default" }, { id: "plan", name: "Plan" }], defaultPromptId: "default", sandboxProfiles: ["workspace"],
  ...change });
function model(id: string, name: string) { return { id, name, efforts: ["Low", "High"], defaultEffort: "High" }; }
const modes = { permissionModes: ["default", "acceptEdits"], defaultPermissionMode: "default", sandbox: null };
const settings = (change: Partial<typeof defaultSessionSettings> = {}) => ({ ...defaultSessionSettings, ...change });

test("Run on offers this machine, then the online machines that let this computer start sessions", () => {
  const targets = runOnTargets([machine({ id: "b", name: "zeta" }), machine({ id: "a", name: "alpha" }), machine({ id: "off", state: "offline" }),
    machine({ id: "view", permissions: ["view", "drive"] }), machine({ id: "wait", state: "connecting" })], "This machine");
  const idle = { working: null, queued: null, sessionLimit: null };
  assert.deepEqual(targets, [{ id: null, name: "This machine" }, { id: "a", name: "alpha", load: idle }, { id: "b", name: "zeta", load: idle }]);
  // A machine that says its load gives it to the chip: 3 of 3 at work, 2 queued.
  assert.deepEqual(runOnTargets([machine({ id: "full", working: 3, queued: 2, sessionLimit: 3 })], "This machine")[1].load, { working: 3, queued: 2, sessionLimit: 3 });
  assert.deepEqual(runOnTargets([], "This machine"), [{ id: null, name: "This machine" }], "Without a machine that can take it, the chip is not shown.");
  assert.equal(canStartOn(machine({ permissions: ["view"] })), false);
});

test("The machine of the chip: the one of the row of a machine, else the chosen one while it can still take the session", () => {
  const machines = [machine({ id: "m1" }), machine({ id: "m2", state: "offline" }), machine({ id: "m3", permissions: ["view"] })];
  assert.equal(runOnMachine(null, machines, null), null, "This machine by default.");
  assert.equal(runOnMachine("m1", machines, null), "m1");
  assert.equal(runOnMachine("m2", machines, null), null, "A machine that went offline gives the session back to this machine.");
  assert.equal(runOnMachine("m3", machines, null), null, "A machine that no longer lets this computer start sessions is not kept.");
  assert.equal(runOnMachine("gone", machines, null), null, "A forgotten machine is not kept.");
  assert.equal(runOnMachine(null, machines, "m2"), "m2", "The row of a machine starts its sessions there, whatever its state: the start says why it cannot.");
});

test("The project follows the machine: the one of the same repository, else the person chooses one", () => {
  assert.deepEqual(remoteSelection(choices(), noRemoteSelection), { projectId: "p-alta", settings: defaultSessionSettings });
  assert.deepEqual(remoteSelection(choices({ suggestedProjectId: null }), noRemoteSelection).projectId, undefined, "Without a suggestion, the person chooses a project.");
  assert.deepEqual(remoteSelection(choices({ suggestedProjectId: "gone" }), noRemoteSelection).projectId, undefined);
  assert.deepEqual(remoteSelection(null, noRemoteSelection), noRemoteSelection, "Nothing is chosen while the machine is read.");
});

test("What the person chose is kept while the machine still offers its provider", () => {
  const chosen = settings({ providerId: "claude", modelId: "sonnet", reasoningEffort: "Low", permissionMode: "acceptEdits", sandbox: "workspace", agentPromptId: "plan" });
  assert.deepEqual(remoteSelection(choices(), { projectId: "p-notes", settings: chosen }), { projectId: "p-notes", settings: chosen });
  assert.deepEqual(remoteSelection(choices(), { projectId: null, settings: chosen }).projectId, null, "A chat stays a chat.");
  // A provider the machine no longer has goes back to its default, with its model, effort and mode; the rest stays.
  assert.deepEqual(remoteSelection(choices(), { projectId: "removed", settings: { ...chosen, providerId: "removed" } }),
    { projectId: "p-alta", settings: settings({ sandbox: "workspace", agentPromptId: "plan" }) });
});

test("A session is started on a machine once it is read, has a ready provider and a project is chosen", () => {
  const ready = remoteSelection(choices(), noRemoteSelection);
  assert.equal(remoteStartBlocker(choices(), ready, "codex"), null);
  assert.equal(remoteStartBlocker(choices(), { ...ready, projectId: null }, "codex"), null, "A chat needs no project.");
  assert.equal(remoteStartBlocker(null, ready, null), "Reading the machine…");
  assert.equal(remoteStartBlocker(choices(), { ...ready, projectId: undefined }, "codex"), "Choose a project of the machine.");
  assert.equal(remoteStartBlocker(choices({ providers: [] }), ready, null), "The machine has no provider ready.");
  assert.equal(remoteStartBlocker(choices(), ready, "removed"), "The machine has no provider ready.");
  assert.equal(remoteStartBlocker(choices({ status: "not_allowed" }), ready, "codex"), "This computer may not start sessions on that machine.");
  assert.equal(remoteStartBlocker(choices({ status: "offline" }), ready, "codex"), "The machine does not answer.");
});

test("A start on a machine asks it for what the Agent chip shows, and leaves the rest to the machine", () => {
  const shown = { providerId: "codex", modelId: "gpt-mini", reasoningEffort: "High" };
  const none = { providerKey: null, modelId: null, reasoningEffort: null, agentPromptId: null, permissionMode: null, sandbox: null, subSessions: null };
  assert.deepEqual(remoteStartRequest(noRemoteSelection, shown), none, "the defaults of the machine");
  assert.deepEqual(remoteStartRequest({ projectId: null, settings: settings({ agentPromptId: "plan" }) }, shown), { ...none, agentPromptId: "plan" });
  // A choice of the mode or the sandbox names the provider, the model and the effort it was shown with.
  assert.deepEqual(remoteStartRequest({ projectId: null, settings: settings({ permissionMode: "acceptEdits", sandbox: "workspace" }) }, shown),
    { ...none, providerKey: "codex", modelId: "gpt-mini", reasoningEffort: "High", permissionMode: "acceptEdits", sandbox: "workspace" });
  assert.deepEqual(remoteStartRequest({ projectId: null, settings: settings({ providerId: "claude", modelId: "opus", reasoningEffort: "Low" }) },
    { providerId: "claude", modelId: "opus", reasoningEffort: "Low" }), { ...none, providerKey: "claude", modelId: "opus", reasoningEffort: "Low" });
  assert.deepEqual(remoteStartRequest({ projectId: null, settings: settings({ options: { subSessions: false } }) }, shown),
    { ...none, subSessions: false }, "the options of the session");
});

test("Why a start on a machine failed, in words", () => {
  assert.equal(newSessionProblem("forbidden"), "This computer may not start sessions on that machine.");
  assert.equal(newSessionProblem("machine_offline"), "The machine does not answer.");
  assert.equal(newSessionProblem("unauthorized"), "The machine refused this computer.");
  assert.equal(newSessionProblem("not_found"), "This machine is no longer paired.");
  assert.equal(newSessionProblem("unavailable"), "The machine no longer has that project or provider.");
  assert.equal(newSessionProblem("node.stopping"), "The machine is stopping.");
  assert.equal(newSessionProblem(null), "The session could not be started on the machine.");
});

test("A first prompt that a budget of the machine holds is not one that was not sent: it waits for Continue anyway", () => {
  assert.equal(remoteStartPrompt("ok"), "sent");
  assert.equal(remoteStartPrompt("held"), "held");
  assert.equal(remoteStartPrompt("queued"), "queued");
  assert.equal(remoteStartPrompt("budget_reached"), "not_sent");
  assert.equal(remoteStartPrompt(null), "not_sent");
});
