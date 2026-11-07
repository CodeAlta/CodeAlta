import { test } from "node:test";
import assert from "node:assert/strict";
import type { FleetServerView, MachineView } from "#neoastra";
import { fleetAttention, fleetSteps, hasFleet, machineTotals, spacesFleet } from "./overviewModel";

const machine = (id: string, state: string, more: Partial<MachineView> = {}): MachineView => ({ id, name: id, state, os: "linux", version: "4.1.0", address: null,
  fingerprint: "ABCD", words: "a b c d e f", sessions: 2, lastSeen: null, problem: null, permissions: [], pairedAt: "2026-11-01T00:00:00Z", paired: true, channel: null, ...more });
const server = (more: Partial<FleetServerView> = {}): FleetServerView => ({ id: "s1", url: "https://fleet.example", organization: "Acme", link: "approved", connection: "online",
  problem: null, controller: true, spaces: ["work"], recoveryKey: null, shareUrl: null, stop: null, ...more });

test("Without a machine or a server, the steps start with both, and the parts of an organization wait for a server", () => {
  assert.equal(hasFleet([], []), false);
  const steps = fleetSteps([], []);
  assert.deepEqual(steps.map(step => [step.id, step.done, step.ready]), [["machines", false, true], ["server", false, true], ["people", false, false],
    ["policies", false, false], ["secrets", false, false], ["budgets", false, false]]);
  const linked = fleetSteps([machine("m", "online")], [server()]);
  assert.deepEqual(linked.filter(step => step.done).map(step => step.id), ["machines", "server"]);
  assert.ok(linked.every(step => step.ready), "a server opens every part");
  assert.equal(fleetSteps([], [server({ link: "pending" })])[2].ready, false, "a server that has not approved this computer opens nothing yet");
});

test("The totals count machines online, their sessions, and those reached through the server", () => {
  assert.deepEqual(machineTotals([machine("a", "online"), machine("b", "offline", { paired: false, sessions: 1 }), machine("c", "connecting")]),
    { total: 3, online: 1, sessions: 5, throughServer: 1 });
});

test("What needs attention: a stopped fleet first, a server not connected, then machines that refuse or do not answer", () => {
  const items = fleetAttention([machine("connecting", "connecting"), machine("gone", "offline"), machine("locked", "unauthorized")],
    [server({ connection: "offline", stop: { scope: "fleet", reason: "Incident", machines: 0, teams: 0, since: null, number: 3 } })]);
  assert.deepEqual(items.map(item => [item.kind, item.name, item.severity]), [["stopped", "Acme", "danger"], ["server", "Acme", "danger"],
    ["offline", "gone", "warning"], ["unauthorized", "locked", "danger"]]);
  assert.equal(items[2].page, "machines/gone", "the row opens the page of the machine");
  assert.deepEqual(fleetAttention([machine("a", "online")], [server()]), [], "a fleet that runs needs nothing");
});

test("Each space says the server it publishes to, or none", () => {
  const rows = spacesFleet([{ id: "default" }, { id: "work" }], [server()]);
  assert.deepEqual(rows.map(row => [row.space.id, row.server?.organization ?? null]), [["default", null], ["work", "Acme"]]);
});
