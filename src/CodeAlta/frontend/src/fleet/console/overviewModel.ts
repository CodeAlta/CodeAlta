import type { FleetServerView, MachineView } from "#neoastra";
import { machineState } from "../../machines/machines";
import { serverConnection } from "../fleetServer";

/** A part of the fleet to set up, as the Overview offers it: what it opens, whether it is set up, and whether it can be yet. */
export type FleetStep = Readonly<{
  id: "machines" | "server" | "people" | "policies" | "secrets" | "budgets";
  page: "machines" | "fleetServer" | "fleetAccess" | "fleetPolicies" | "fleetSecrets" | "fleetBudgets";
  done: boolean;
  /** Whether it can be set up now: the parts of an organization need a server, and this computer to administer it. */
  ready: boolean;
  /** What it waits for while it is not ready: a server, or the computer of an administrator (this one is a member). */
  waitsFor: "server" | "administrator" | null;
}>;

/** Whether this computer has a fleet at all: a machine, or a server. */
export const hasFleet = (machines: readonly MachineView[], servers: readonly FleetServerView[]) => machines.length > 0 || servers.length > 0;

/**
 * The parts of the fleet in the order a person sets them up: machines and a server first, then what an organization
 * adds on its server (people, policies, secrets, budgets), which wait for it.
 */
export function fleetSteps(machines: readonly MachineView[], servers: readonly FleetServerView[]): FleetStep[] {
  const linked = servers.some(server => server.link === "approved");
  // The pages of an organization administer it from a computer that controls it; a member's computer has nothing to set there.
  const administered = servers.some(server => server.link === "approved" && server.controller);
  const waitsFor = administered ? null : linked ? "administrator" : "server";
  // What an organization has set up is not read here: its parts are offered, never counted as done.
  const organization = (id: "people" | "policies" | "secrets" | "budgets", page: FleetStep["page"]): FleetStep => ({ id, page, done: false, ready: administered, waitsFor });
  return [
    { id: "machines", page: "machines", done: machines.length > 0, ready: true, waitsFor: null },
    { id: "server", page: "fleetServer", done: linked, ready: true, waitsFor: null },
    organization("people", "fleetAccess"), organization("policies", "fleetPolicies"), organization("secrets", "fleetSecrets"), organization("budgets", "fleetBudgets"),
  ];
}

/** How the machines stand: how many in all, online, reached through the server, and the sessions they listed. */
export function machineTotals(machines: readonly MachineView[]) {
  const online = machines.filter(machine => machineState(machine) === "online").length;
  return {
    total: machines.length,
    online,
    sessions: machines.reduce((sum, machine) => sum + Math.max(0, machine.sessions), 0),
    throughServer: machines.filter(machine => !machine.paired).length,
  };
}

/** Something of the fleet that needs the person: what it is about, and the page that fixes it. */
export type FleetAttention = Readonly<{
  kind: "stopped" | "stoppedPart" | "server" | "unauthorized" | "offline";
  /** The machine or the server it is about. */
  name: string;
  detail: string | null;
  page: string;
  severity: "danger" | "warning";
}>;

const kindRank: Readonly<Record<FleetAttention["kind"], number>> = { stopped: 0, stoppedPart: 1, server: 2, unauthorized: 3, offline: 4 };

/**
 * What needs the person, the most pressing first: a server whose kill switch stopped the work, a server this computer is
 * not connected to, a machine that refuses this computer, a machine that does not answer. Machines still connecting are
 * not counted: they are on their way.
 */
export function fleetAttention(machines: readonly MachineView[], servers: readonly FleetServerView[]): FleetAttention[] {
  const items: FleetAttention[] = [];
  for (const server of servers) {
    const name = server.organization ?? server.url;
    if (server.stop) items.push({ kind: server.stop.scope === "fleet" ? "stopped" : "stoppedPart", name, detail: server.stop.reason ?? null, page: "fleetPolicies", severity: "danger" });
    const connection = serverConnection(server);
    if (connection !== "online" && connection !== "connecting")
      items.push({ kind: "server", name, detail: server.problem ?? null, page: "fleetServer", severity: connection === "pending" ? "warning" : "danger" });
  }

  for (const machine of machines) {
    const state = machineState(machine);
    if (state === "unauthorized") items.push({ kind: "unauthorized", name: machine.name, detail: machine.problem ?? null, page: `machines/${machine.id}`, severity: "danger" });
    else if (state === "offline") items.push({ kind: "offline", name: machine.name, detail: machine.problem ?? null, page: `machines/${machine.id}`, severity: "warning" });
  }

  // The most pressing first, whatever the order of the lists: what is stopped, then a server, a machine that refuses, one that is gone.
  return items.map((item, index) => ({ item, index })).sort((a, b) => kindRank[a.item.kind] - kindRank[b.item.kind] || a.index - b.index).map(entry => entry.item);
}

/** A space as Fleet sees it: the server its sessions publish to, if any. */
export type SpaceFleet<S> = Readonly<{ space: S; server: FleetServerView | null }>;

/** Each space with the server it publishes to (the first that names it); a space of none works on this computer and its machines only. */
export function spacesFleet<S extends Readonly<{ id: string }>>(spaces: readonly S[], servers: readonly FleetServerView[]): SpaceFleet<S>[] {
  return spaces.map(space => ({ space, server: servers.find(server => server.spaces.includes(space.id)) ?? null }));
}
