import assert from "node:assert/strict";
import test from "node:test";
import type { MachineNewSessionChoices, MachineView } from "#neoastra";
import { canStartOn, newSessionProblem, noRemoteSelection, remoteSelection, remoteStartBlocker, remoteStartPrompt, runOnMachine, runOnTargets } from "./runOn";

const machine = (change: Partial<MachineView> = {}): MachineView => ({ id: "m1", name: "mac-mini", state: "online", os: "macos", version: "4.0.0",
  address: "mac-mini:2585", fingerprint: "ABCD EFGH", words: "apple river stone cloud amber tiger", sessions: 2, lastSeen: null, problem: null,
  permissions: ["view", "drive", "start"], pairedAt: "2026-11-03T00:00:00Z", paired: true, channel: null, tags: [], working: null, queued: null, sessionLimit: null, ...change });

const choices = (change: Partial<MachineNewSessionChoices> = {}): MachineNewSessionChoices => ({
  status: "ok", machineName: "mac-mini", message: null, suggestedProjectId: "p-alta",
  projects: [{ id: "p-alta", name: "CodeAlta", repositoryKey: "github.com/codealta/codealta" }, { id: "p-notes", name: "notes", repositoryKey: null }],
  providers: [
    { key: "claude", name: "Claude Code", isDefault: false, defaultModelId: "opus", models: [{ id: "opus", name: "Opus" }, { id: "sonnet", name: "Sonnet" }] },
    { key: "codex", name: "Codex", isDefault: true, defaultModelId: "gpt-mini", models: [{ id: "gpt", name: "GPT" }, { id: "gpt-mini", name: "GPT mini" }] },
  ],
  ...change });

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

test("The selection follows the machine: the project of the same repository, the default provider and its default model", () => {
  assert.deepEqual(remoteSelection(choices(), noRemoteSelection), { projectId: "p-alta", providerKey: "codex", modelId: "gpt-mini" });
  assert.deepEqual(remoteSelection(choices({ suggestedProjectId: null }), noRemoteSelection).projectId, undefined, "Without a suggestion, the person chooses a project.");
  assert.deepEqual(remoteSelection(choices({ suggestedProjectId: "gone" }), noRemoteSelection).projectId, undefined);
  assert.deepEqual(remoteSelection(null, noRemoteSelection), noRemoteSelection, "Nothing is chosen while the machine is read.");
  const noDefault = choices({ providers: [{ key: "x", name: "X", isDefault: false, defaultModelId: "missing", models: [{ id: "m", name: "M" }] }] });
  assert.deepEqual(remoteSelection(noDefault, noRemoteSelection), { projectId: "p-alta", providerKey: "x", modelId: "m" }, "Else the first provider and its first model.");
  const noModels = choices({ providers: [{ key: "x", name: "X", isDefault: true, defaultModelId: null, models: [] }] });
  assert.equal(remoteSelection(noModels, noRemoteSelection).modelId, null, "A provider that lists no model chooses its own.");
});

test("What the person chose is kept while the machine still offers it", () => {
  assert.deepEqual(remoteSelection(choices(), { projectId: "p-notes", providerKey: "claude", modelId: "sonnet" }),
    { projectId: "p-notes", providerKey: "claude", modelId: "sonnet" });
  assert.deepEqual(remoteSelection(choices(), { projectId: null, providerKey: "claude", modelId: null }),
    { projectId: null, providerKey: "claude", modelId: "opus" }, "A chat stays a chat; a provider just chosen takes its default model.");
  assert.deepEqual(remoteSelection(choices(), { projectId: "removed", providerKey: "removed", modelId: "removed" }),
    { projectId: "p-alta", providerKey: "codex", modelId: "gpt-mini" }, "What the machine no longer has falls back to its defaults.");
  assert.equal(remoteSelection(choices(), { projectId: "p-alta", providerKey: "codex", modelId: "opus" }).modelId, "gpt-mini",
    "A model of another provider is not kept.");
});

test("A session is started on a machine once it is read, has a ready provider and a project is chosen", () => {
  const ready = remoteSelection(choices(), noRemoteSelection);
  assert.equal(remoteStartBlocker(choices(), ready), null);
  assert.equal(remoteStartBlocker(choices(), { ...ready, projectId: null }), null, "A chat needs no project.");
  assert.equal(remoteStartBlocker(null, ready), "Reading the machine…");
  assert.equal(remoteStartBlocker(choices(), { ...ready, projectId: undefined }), "Choose a project of the machine.");
  assert.equal(remoteStartBlocker(choices({ providers: [] }), { ...ready, providerKey: null }), "The machine has no provider ready.");
  assert.equal(remoteStartBlocker(choices({ status: "not_allowed" }), ready), "This computer may not start sessions on that machine.");
  assert.equal(remoteStartBlocker(choices({ status: "offline" }), ready), "The machine does not answer.");
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
