import assert from "node:assert/strict";
import test from "node:test";
import type { MachineMovePreview, MachineView } from "#neoastra";
import { isMachineProject, isRemoteSession, machineIntent, machinePageLink, machineRowState, machineState, machineStateLabel, moveProblem, moveReady, moveTargets, orderedMachines,
  pairingLinkOf, pairingProblem, pairingWords, readMachinePageLink, sessionMachineState } from "./machines";

const machine = (change: Partial<MachineView> = {}): MachineView => ({ id: "m1", name: "mac-mini", state: "online", os: "macos", version: "4.0.0",
  address: "mac-mini:2585", fingerprint: "ABCD EFGH", words: "apple river stone cloud amber tiger", sessions: 2, lastSeen: null, problem: null,
  permissions: ["view", "drive"], pairedAt: "2026-11-03T00:00:00Z", paired: true, channel: null, tags: [], working: null, queued: null, sessionLimit: null, ...change });

test("The page of a paired machine is a page of Settings: machines/<id>", () => {
  assert.equal(machinePageLink("mini-id"), "machines/mini-id");
  assert.equal(readMachinePageLink(machinePageLink("mini-id")), "mini-id");
  for (const other of ["machines", "machines/", "machines/a/b", "spaces/x/general", "fleetServer", `machines/${"x".repeat(65)}`]) assert.equal(readMachinePageLink(other), null, other);
});

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

const preview = (change: Partial<MachineMovePreview> = {}): MachineMovePreview => ({ status: "ok", message: null, busy: null, jobs: [], secrets: [], unpushedCommits: 0,
  dirty: false, branch: "main", repositoryKey: "github.com/o/r", targetStatus: "accepted", targetCode: null, targetMessage: null, projectName: "r", projectPath: "/r",
  canClone: false, targetWarnings: [], receiveMoves: true, machineName: "pc", ...change });

test("A session of this computer moves to its machines; one of a machine moves here or to the others", () => {
  const paired = [machine({ id: "m1", name: "mac" }), machine({ id: "m2", name: "build", state: "offline" })];
  assert.deepEqual(moveTargets({ machineId: null }, paired, "pc"), [{ id: "m1", name: "mac", state: "online" }, { id: "m2", name: "build", state: "offline" }]);
  assert.deepEqual(moveTargets({ machineId: "m1" }, paired, "pc").map(target => target.id), [null, "m2"], "This computer first, never the machine it runs on.");
});

test("A move starts only when the session is idle and the target takes it", () => {
  assert.equal(moveReady(preview(), false, false), true);
  assert.equal(moveReady(preview({ busy: "working" }), false, false), false);
  assert.equal(moveReady(preview({ status: "no_remote" }), false, false), false);
  assert.equal(moveReady(preview({ receiveMoves: false }), true, false), false, "This computer receives moves only when the person lets it.");
  assert.equal(moveReady(preview({ receiveMoves: false }), false, false), true, "The setting is about this computer only.");
  const missing = preview({ targetStatus: "refused", targetCode: "project_missing", canClone: true });
  assert.equal(moveReady(missing, false, false), false);
  assert.equal(moveReady(missing, false, true), true, "A target that can clone takes it once the person asks.");
  assert.equal(moveReady(null, false, false), false);
});

test("Each refusal of a move has its words", () => {
  assert.equal(moveProblem("finished"), null);
  assert.equal(moveProblem("ok"), null);
  assert.equal(moveProblem("refused", "working"), "The session is at work: it moves once it is idle.");
  assert.equal(moveProblem("refused", "forbidden"), "This computer may not move sessions to that machine.");
  assert.equal(moveProblem("aborted", "project_missing"), "The machine has no project of this repository.");
  assert.equal(moveProblem("offered", "unreachable"), "The machine does not answer: the move goes on when it is back, and the session stays read-only meanwhile.");
  assert.equal(moveProblem("refused", "something new"), "The session could not move.");
});

test("The row of a machine in the Explorer has the state of its machine, offline when it says none", () => {
  assert.equal(machineRowState({ machineState: "online" }), "online");
  assert.equal(machineRowState({ machineState: "connecting" }), "connecting");
  assert.equal(machineRowState({ machineState: undefined }), "offline");
});
