import assert from "node:assert/strict";
import test from "node:test";
import type { MachineDeviceView, MachineProviderStatusView } from "#neoastra";
import { agentSandboxChoices, agentsAllowed, confirmsBypass, confirmsLoss, devicePermissions, hasPermission, managementProblem, managesMachine, newProviderKey, orderedDevices, permissionLabel,
  permissionsEditable, projectSource, providerFix, providerProblem, providerState, providerStateLabel, updateSummary } from "./machineManagement";

const provider = (change: Partial<MachineProviderStatusView> = {}): MachineProviderStatusView => ({ key: "openai", name: "OpenAI", type: "openai-responses", enabled: true,
  state: "not_ready", reason: "key_missing", detail: null, credential: "key", keySecret: null, signIn: false, isDefault: false, model: null, apiUrl: null, ...change });

test("a provider the machine skipped says why, with the secret or the variable it waits for, instead of only in its log", () => {
  assert.deepEqual(providerProblem(provider({ reason: "secret_missing", detail: "PROVIDER_OPENAI_API_KEY" })),
    { key: "Its key is the secret {name} of the machine, which is not set.", values: { name: "PROVIDER_OPENAI_API_KEY" } });
  assert.deepEqual(providerProblem(provider({ detail: "OPENAI_API_KEY" })), { key: "It has no key: the variable {name} is not set on the machine.", values: { name: "OPENAI_API_KEY" } });
  assert.deepEqual(providerProblem(provider()), { key: "It has no key." });
  assert.deepEqual(providerProblem(provider({ reason: "sign_in_needed", credential: "sign_in" })), { key: "It is not signed in on the machine." });
  assert.deepEqual(providerProblem(provider({ state: "failed", reason: "start_failed", detail: "401 Unauthorized" })), { key: "It did not start: {detail}", values: { detail: "401 Unauthorized" } });
  assert.equal(providerProblem(provider({ state: "ready", reason: null })), null);
  assert.equal(providerProblem(provider({ state: "preparing", reason: null })), null);
  assert.equal(providerState(provider({ state: "something new" })), "not_ready");
  assert.equal(providerStateLabel("preparing"), "Preparing…");
});

test("each provider offers what makes it ready: its key, its sign-in with a code, or nothing for its own login on the machine", () => {
  assert.equal(providerFix(provider()), "key");
  assert.equal(providerFix(provider({ credential: "sign_in", signIn: true })), "sign_in");
  assert.equal(providerFix(provider({ credential: "sign_in", signIn: false })), null, "Codex signs in with a browser on the machine");
  assert.equal(providerFix(provider({ credential: "cli" })), null);
  assert.equal(newProviderKey("anthropic", ["openai"]), "anthropic");
  assert.equal(newProviderKey("anthropic", ["Anthropic", "anthropic-2"]), "anthropic-3");
  assert.equal(managementProblem("provider.sign_in_unsupported"), "This provider does not sign in with a code: sign in on the machine itself.");
});

const device = (change: Partial<MachineDeviceView> = {}): MachineDeviceView => ({ id: "d1", name: "laptop", fingerprint: "ABCD EFGH IJKL", permissions: ["view", "drive", "start"],
  pairedAt: "2026-11-01T00:00:00Z", revokedAt: null, channels: 0, isThisComputer: false, ...change });

test("The six permissions have their order, a name and what they let a device do", () => {
  assert.deepEqual([...devicePermissions], ["view", "drive", "start", "move", "automate", "manage"]);
  assert.equal(permissionLabel("manage").name, "Manage");
  assert.equal(permissionLabel("automate").description, "Run its automations there.");
  assert.ok(hasPermission(device(), "drive"));
  assert.ok(!hasPermission(device(), "move"));
});

test("This computer manages a machine when the machine says so", () => {
  assert.ok(managesMachine({ permissions: ["view", "manage"] }));
  assert.ok(!managesMachine({ permissions: ["view", "drive", "start"] }));
});

test("A revoked device keeps its switches still, and giving up managing from here asks first", () => {
  assert.ok(permissionsEditable(device()));
  assert.ok(!permissionsEditable(device({ revokedAt: "2026-11-02T00:00:00Z" })));
  assert.ok(confirmsLoss(device({ isThisComputer: true }), "manage"));
  assert.ok(!confirmsLoss(device({ isThisComputer: true }), "move"));
  assert.ok(!confirmsLoss(device(), "manage"), "another device loses it with one click, as any other permission");
});

test("Agents of other machines are off until a space lets them in, and only turning on their bypass asks first", () => {
  assert.ok(!agentsAllowed({ spaces: [] }));
  assert.ok(!agentsAllowed({ spaces: [{ id: "default", name: "Default", allowed: false }] }));
  assert.ok(agentsAllowed({ spaces: [{ id: "default", name: "Default", allowed: false }, { id: "work", name: "Work", allowed: true }] }));
  assert.ok(confirmsBypass(true));
  assert.ok(!confirmsBypass(false), "taking it back is one click");
});

test("The sandbox of agents' sessions is their parent's first, then the profiles of the machine and the one its policy names", () => {
  assert.deepEqual(agentSandboxChoices({ sandboxes: ["workspace", "strict"], sandbox: null }), ["", "workspace", "strict"]);
  assert.deepEqual(agentSandboxChoices({ sandboxes: ["workspace", "strict"], sandbox: "Strict" }), ["", "workspace", "strict"], "a name in another case is the same profile");
  assert.deepEqual(agentSandboxChoices({ sandboxes: ["workspace", "strict"], sandbox: "ci" }), ["", "workspace", "strict", "ci"]);
  assert.deepEqual(agentSandboxChoices({ sandboxes: [], sandbox: null }), [""]);
});

test("Devices are listed this computer first, then by pairing, the revoked ones last", () => {
  const ordered = orderedDevices([
    device({ id: "old", pairedAt: "2026-10-01T00:00:00Z", revokedAt: "2026-10-05T00:00:00Z" }),
    device({ id: "b", pairedAt: "2026-11-02T00:00:00Z" }),
    device({ id: "a", pairedAt: "2026-11-01T00:00:00Z" }),
    device({ id: "me", pairedAt: "2026-11-03T00:00:00Z", isThisComputer: true }),
  ]);
  assert.deepEqual(ordered.map(value => value.id), ["me", "a", "b", "old"]);
});

test("What is typed to add a project is a repository to clone or a full path of a folder", () => {
  assert.deepEqual(projectSource(" https://github.com/owner/repo "), { kind: "clone", value: "https://github.com/owner/repo" });
  assert.deepEqual(projectSource("git@github.com:owner/repo.git"), { kind: "clone", value: "git@github.com:owner/repo.git" });
  assert.deepEqual(projectSource("/home/me/code/app"), { kind: "folder", value: "/home/me/code/app" });
  assert.deepEqual(projectSource("C:\\code\\app"), { kind: "folder", value: "C:\\code\\app" });
  assert.equal(projectSource("app"), null, "a relative folder names nothing on the machine");
  assert.equal(projectSource("   "), null);
});

test("Each refusal of the machine has its words, and each update its summary", () => {
  assert.equal(managementProblem("ok"), null);
  assert.equal(managementProblem("not_manager"), "This computer does not manage this machine.");
  assert.equal(managementProblem("machine_offline"), "The machine does not answer: it is offline.");
  assert.equal(managementProblem("project.has_sessions"), "The project still has sessions there: archive or delete them first.");
  assert.equal(managementProblem("something.new"), "The machine could not do it.");
  assert.equal(updateSummary({ status: "available", target: "4.0.1", running: "4.0.0" }), "Version {version} is available.");
  assert.equal(updateSummary({ status: "staged", target: "4.0.1", running: "4.0.0" }), "It switches to {version} once no session works.");
  assert.equal(updateSummary({ status: "up_to_date", target: null, running: "4.0.0" }), "Up to date.");
});
