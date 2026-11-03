// The state of the CodeAlta Servers of this computer (Settings > Fleet > Server, the server of a space) and of the directory of the
// Fleet canvas, as words and orders: the host says states and codes, these functions say what the page shows of them.
import type { FleetDeviceView, FleetMachineView, FleetServerView, FleetSessionView } from "#neoastra";
import type { MessageKey } from "../localization";

/** How the control connection of a server stands, as the host reports it; an unknown value is offline. */
export type ServerConnection = "online" | "connecting" | "offline" | "pending" | "unauthenticated" | "revoked";

/** The connection of a server view. */
export function serverConnection(server: Pick<FleetServerView, "connection">): ServerConnection {
  switch (server.connection) {
    case "online": case "connecting": case "pending": case "unauthenticated": case "revoked": return server.connection;
    default: return "offline";
  }
}

/** The words of a connection. */
export function serverConnectionLabel(connection: ServerConnection): MessageKey {
  switch (connection) {
    case "online": return "Online";
    case "connecting": return "Connecting…";
    case "pending": return "Waiting for approval";
    case "unauthenticated": return "Not known to the server";
    case "revoked": return "Revoked";
    default: return "Offline";
  }
}

/** The Blueprint intent of the tag of a connection. */
export function serverConnectionIntent(connection: ServerConnection): "success" | "warning" | "danger" | "none" {
  return connection === "online" ? "success" : connection === "connecting" || connection === "pending" ? "warning"
    : connection === "unauthenticated" || connection === "revoked" ? "danger" : "none";
}

/** Whether a text can be the address of a server: http:// or https://, a host, no query. */
export function serverAddressOf(text: string): string {
  const value = text.trim();
  if (!value || value.length > 2048) return "";
  try {
    const url = new URL(value);
    if ((url.protocol !== "http:" && url.protocol !== "https:") || !url.hostname || url.search || url.hash || url.username) return "";
    return value.replace(/\/+$/u, "");
  } catch { return ""; }
}

/** Why a setup, a join or another call to a server failed, in words; null when it did not. */
export function serverProblem(status: string): MessageKey | null {
  switch (status) {
    case "ok": return null;
    case "invalid_url": return "This is not the address of a server.";
    case "already_linked": return "This computer is linked to that server already.";
    case "unreachable": return "The server does not answer.";
    case "setup.invalid_code": return "This is not the setup code the server waits for.";
    case "setup.code_expired": return "The setup code expired: altaserver setup-code makes a new one.";
    case "setup.loopback_only": return "Set the server up from the computer it runs on.";
    case "setup.done": return "The server has an owner already: join it with an enrollment code.";
    case "enrollment.invalid_token": return "The server does not know this enrollment code.";
    case "enrollment.token_used": return "This enrollment code was used already.";
    case "enrollment.token_expired": return "This enrollment code expired.";
    case "device.exists": return "This computer is registered with the server already.";
    case "forbidden": return "Only the owner's computer can do this.";
    case "pending_approval": return "This computer waits for its approval.";
    case "device_list.conflict": return "Another device changed the list meanwhile: try again.";
    default: return "The operation did not complete.";
  }
}

/** The devices of a server as the page lists them: those that wait first, then the approved, then the revoked; the latest first in each. */
export function orderedDevices(devices: readonly FleetDeviceView[]): FleetDeviceView[] {
  const rank = (device: FleetDeviceView) => device.status === "pending" ? 0 : device.status === "approved" ? 1 : 2;
  return [...devices].sort((left, right) => rank(left) - rank(right) || right.createdAt.localeCompare(left.createdAt) || left.id.localeCompare(right.id));
}

/** The command a machine runs to join a server with a token. */
export function joinCommand(url: string, token: string): string {
  return `altanode join ${url} --token ${token}`;
}

/** The words of the key of a device, cut in two lines of three, as the device prints them on one. */
export function keyWords(words: string | null | undefined): string[] {
  return (words ?? "").split(/\s+/u).filter(Boolean);
}

/** The state of a published session, as a session list says it. */
export type FleetSessionState = "running" | "requires_action" | "idle" | "archived";

/** The state of a session view; an unknown value is idle. */
export function sessionState(session: Pick<FleetSessionView, "state">): FleetSessionState {
  return session.state === "running" || session.state === "requires_action" || session.state === "archived" ? session.state : "idle";
}

/** The words of a session state. */
export function sessionStateLabel(state: FleetSessionState): MessageKey {
  return state === "running" ? "Working" : state === "requires_action" ? "Waits for you" : state === "archived" ? "Archived" : "Idle";
}

/** A machine of the directory and its sessions, as the canvas draws them. */
export type FleetMachineGroup = Readonly<{ machine: FleetMachineView; sessions: readonly FleetSessionView[] }>;

/**
 * The machines of the directory with their sessions: this computer first, then those online, then by name; in each, the sessions that
 * wait for a person first, then those at work, then the latest. Archived sessions are left out unless asked.
 */
export function fleetGroups(machines: readonly FleetMachineView[], sessions: readonly FleetSessionView[], archived = false): FleetMachineGroup[] {
  const rank: Record<FleetSessionState, number> = { requires_action: 0, running: 1, idle: 2, archived: 3 };
  const byMachine = new Map<string, FleetSessionView[]>();
  for (const session of sessions) {
    if (!archived && sessionState(session) === "archived") continue;
    const list = byMachine.get(session.machineId) ?? [];
    list.push(session);
    byMachine.set(session.machineId, list);
  }
  const order = (machine: FleetMachineView) => machine.isThisComputer ? 0 : machine.online ? 1 : 2;
  return [...machines].sort((left, right) => order(left) - order(right) || left.name.localeCompare(right.name) || left.id.localeCompare(right.id))
    .map(machine => ({ machine, sessions: (byMachine.get(machine.id) ?? []).sort((left, right) =>
      rank[sessionState(left)] - rank[sessionState(right)] || right.updatedAt.localeCompare(left.updatedAt) || left.id.localeCompare(right.id)) }));
}

/** What the head of the canvas counts: machines online, sessions at work, sessions that wait for a person. */
export function fleetSummary(machines: readonly FleetMachineView[], sessions: readonly FleetSessionView[]): Readonly<{ online: number; machines: number; working: number; waiting: number }> {
  return {
    online: machines.filter(machine => machine.online).length,
    machines: machines.length,
    working: sessions.filter(session => sessionState(session) === "running").length,
    waiting: sessions.filter(session => sessionState(session) === "requires_action").length,
  };
}

/** The model of a session in a few words: its model, else its provider type, else nothing. */
export function sessionModel(session: Pick<FleetSessionView, "model" | "providerType">): string {
  return session.model && session.providerType ? `${session.model} · ${session.providerType}` : session.model ?? session.providerType ?? "";
}

/** The server a space publishes to, among the servers of this computer; null when none. */
export function serverOfSpace(servers: readonly FleetServerView[], spaceId: string): FleetServerView | null {
  return servers.find(server => server.spaces.includes(spaceId)) ?? null;
}
