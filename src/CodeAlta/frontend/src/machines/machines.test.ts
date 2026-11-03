import assert from "node:assert/strict";
import test from "node:test";
import type { MachineView } from "#neoastra";
import { isMachineProject, isRemoteSession, machineIntent, machineState, machineStateLabel, orderedMachines, pairingLinkOf, pairingProblem,
  pairingWords, sessionMachineState } from "./machines";

const machine = (change: Partial<MachineView> = {}): MachineView => ({ id: "m1", name: "mac-mini", state: "online", os: "macos", version: "4.0.0",
  address: "mac-mini:2585", fingerprint: "ABCD EFGH", words: "apple river stone cloud amber tiger", sessions: 2, lastSeen: null, problem: null,
  permissions: ["view", "drive"], pairedAt: "2026-11-03T00:00:00Z", ...change });

test("A machine's state is one of the four the host reports, offline otherwise", () => {
  assert.equal(machineState(machine()), "online");
  assert.equal(machineState(machine({ state: "unauthorized" })), "unauthorized");
  assert.equal(machineState(machine({ state: "something else" })), "offline");
  assert.equal(machineStateLabel("connecting"), "Connecting…");
  assert.equal(machineStateLabel("offline"), "Offline");
  assert.equal(machineIntent("online"), "success");
  assert.equal(machineIntent("unauthorized"), "danger");
  assert.equal(machineIntent("offline"), "none");
});

test("A pasted pairing link is trimmed, and anything else is no link", () => {
  assert.equal(pairingLinkOf("  codealta-pair://v1?n=mac  \n"), "codealta-pair://v1?n=mac");
  assert.equal(pairingLinkOf("https://example.com"), "");
  assert.equal(pairingLinkOf("codealta-pair://two words"), "");
  assert.equal(pairingLinkOf(""), "");
});

test("Each refusal of a pairing has its words", () => {
  assert.equal(pairingProblem("ok"), null);
  assert.equal(pairingProblem("expired"), "This pairing link has expired.");
  assert.equal(pairingProblem("link_refused"), "The machine refused this link: ask it for a new one.");
  assert.equal(pairingProblem("unreachable"), "The machine does not answer.");
  assert.equal(pairingProblem("stale_epoch"), "Machines unavailable");
  assert.deepEqual(pairingWords(" apple  river stone "), ["apple", "river", "stone"]);
  assert.deepEqual(pairingWords(null), []);
});

test("Machines are listed online first, then by name", () => {
  const listed = orderedMachines([machine({ id: "b", name: "build", state: "offline" }), machine({ id: "z", name: "zeta" }), machine({ id: "a", name: "alpha" })]);
  assert.deepEqual(listed.map(value => value.id), ["a", "z", "b"]);
});

test("A remote session names its machine; a session of this computer names none", () => {
  assert.equal(isRemoteSession({ machineId: "m1" }), true);
  assert.equal(isRemoteSession({ machineId: null }), false);
  assert.equal(isRemoteSession(undefined), false);
  assert.equal(isMachineProject({ machineId: "m1" }), true);
  assert.equal(isMachineProject({}), false);
  assert.equal(sessionMachineState({ machineId: "m1" }, [machine()]), "online");
  assert.equal(sessionMachineState({ machineId: "gone" }, [machine()]), "offline", "A machine that is no longer paired shows offline.");
  assert.equal(sessionMachineState({ machineId: null }, [machine()]), null);
});
