import { test } from "node:test";
import assert from "node:assert/strict";
import type { FleetServerView, MachineView } from "#neoastra";
import { fleetAttention, fleetSteps, hasFleet, machineTotals, spacesFleet } from "./overviewModel";

const machine = (id: string, state: string, more: Partial<MachineView> = {}): MachineView => ({ id, name: id, state, os: "linux", version: "4.1.0", address: null,
  fingerprint: "ABCD", words: "a b c d e f", sessions: 2, lastSeen: null, problem: null, permissions: [], pairedAt: "2026-11-01T00:00:00Z", paired: true, channel: null, tags: [], working: null, queued: null, sessionLimit: null, serversToJoin: [], ...more });
const server = (more: Partial<FleetServerView> = {}): FleetServerView => ({ id: "s1", url: "https://fleet.example", organization: "Acme", link: "approved", connection: "online",
  problem: null, controller: true, spaces: ["work"], recoveryKey: null, shareUrl: null, stop: null, ...more });

test("Without a machine or a server, the steps start with both, and the parts of an organization wait for a server", () => {
  assert.equal(hasFleet([], []), false);
  const steps = fleetSteps([], []);
  assert.deepEqual(steps.map(step => [step.id, step.done, step.ready, step.waitsFor]), [["machines", false, true, null], ["server", false, true, null], ["people", false, false, "server"],
    ["policies", false, false, "server"], ["secrets", false, false, "server"], ["budgets", false, false, "server"]]);
  const linked = fleetSteps([machine("m", "online")], [server()]);
  assert.deepEqual(linked.filter(step => step.done).map(step => step.id), ["machines", "server"]);
  assert.ok(linked.every(step => step.ready), "a server opens every part");
  assert.equal(fleetSteps([], [server({ link: "pending" })])[2].ready, false, "a server that has not approved this computer opens nothing yet");
  const member = fleetSteps([], [server({ controller: false })]);
  assert.deepEqual([member[1].done, member[2].ready, member[2].waitsFor], [true, false, "administrator"], "a member's computer is linked, but administers nothing");
});

test("The totals count machines online, their sessions, and those reached through the server", () => {
  assert.deepEqual(machineTotals([machine("a", "online"), machine("b", "offline", { paired: false, sessions: 1 }), machine("c", "connecting")]),
    { total: 3, online: 1, sessions: 5, throughServer: 1 });
});

test("What needs attention: a stopped fleet first, a server not connected, then machines that refuse or do not answer", () => {
  const items = fleetAttention([machine("connecting", "connecting"), machine("gone", "offline"), machine("locked", "unauthorized")],
    [server({ connection: "offline", stop: { scope: "fleet", reason: "Incident", machines: 0, teams: 0, since: null, number: 3 } })]);
  assert.deepEqual(items.map(item => [item.kind, item.name, item.severity]), [["stopped", "Acme", "danger"], ["server", "Acme", "danger"],
    ["unauthorized", "locked", "danger"], ["offline", "gone", "warning"]], "a machine that refuses comes before one that is gone, whatever their order");
  assert.equal(items[3].page, "machines/gone", "the row opens the page of the machine");
  assert.equal(fleetAttention([], [server({ stop: { scope: "machines", reason: null, machines: 2, teams: 0, since: null, number: 4 } })])[0].kind, "stoppedPart", "a stop of some machines is not the whole fleet's");
  assert.deepEqual(fleetAttention([machine("a", "online")], [server()]), [], "a fleet that runs needs nothing");
});

test("Each space says the server it publishes to, or none", () => {
  const rows = spacesFleet([{ id: "default" }, { id: "work" }], [server()]);
  assert.deepEqual(rows.map(row => [row.space.id, row.server?.organization ?? null]), [["default", null], ["work", "Acme"]]);
});
