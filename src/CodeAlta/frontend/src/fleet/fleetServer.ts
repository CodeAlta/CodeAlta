// The state of the CodeAlta Servers of this computer (Settings > Fleet > Server, the server of a space) and of the directory of the
// Fleet canvas, as words and orders: the host says states and codes, these functions say what the page shows of them.
import type { FleetDeviceView, FleetMachineView, FleetServerView, FleetSessionView, FleetTelemetryView } from "#neoastra";
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
    case "telemetry.conflict": return "Another owner changed the telemetry meanwhile: try again.";
    case "telemetry.invalid": case "invalid_request": return "The server refused this telemetry setting.";
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

/** A signal the fleet's telemetry sends. */
export type TelemetrySignal = "traces" | "metrics" | "logs";

/** The signals, in the order the page shows them. */
export const telemetrySignals: readonly TelemetrySignal[] = ["traces", "metrics", "logs"];

/** A header of the collector as the page edits it: one held by the server shows masked and keeps its value until a new one is typed. */
export type TelemetryHeaderDraft = Readonly<{ name: string; value: string; masked: string | null }>;

/** The telemetry of a fleet as the page edits it. */
export type TelemetryDraft = Readonly<{
  enabled: boolean;
  endpoint: string;
  protocol: "http/protobuf" | "grpc";
  route: "direct" | "server";
  signals: readonly TelemetrySignal[];
  headers: readonly TelemetryHeaderDraft[];
  environment: string;
}>;

/** The draft of the telemetry the host gave: its values, the headers masked. */
export function telemetryDraft(view: FleetTelemetryView): TelemetryDraft {
  return {
    enabled: view.enabled,
    endpoint: view.endpoint ?? "",
    protocol: view.protocol === "grpc" ? "grpc" : "http/protobuf",
    route: view.route === "server" ? "server" : "direct",
    signals: telemetrySignals.filter(signal => view.signals.includes(signal)),
    headers: view.headers.map(header => ({ name: header.name, value: "", masked: header.masked })),
    environment: view.environment ?? "",
  };
}

const headerName = /^[A-Za-z0-9!#$%&'*+.^_`|~-]+$/u;

/** What is wrong with a draft, in words; null when the host can sign it. */
export function telemetryProblem(draft: TelemetryDraft): MessageKey | null {
  if (draft.enabled && !serverAddressOf(draft.endpoint)) return "This is not the address of a collector.";
  if (draft.enabled && draft.signals.length === 0) return "Choose at least one signal.";
  if (draft.route === "server" && draft.protocol === "grpc") return "The server forwards OTLP over HTTP only.";
  const names = new Set<string>();
  for (const header of draft.headers) {
    const name = header.name.trim().toLowerCase();
    const value = header.value.trim();
    if (!headerName.test(name) || names.has(name) || (!value && !header.masked) || /[,\r\n]/u.test(value)) return "A header needs a name and a value, without a comma.";
    names.add(name);
  }
  return null;
}

/** The headers the host is sent: a header whose masked value was left as it was goes without a value, so that the host keeps it. */
export function telemetryHeaderEdits(draft: TelemetryDraft): { name: string; value: string | null }[] {
  return draft.headers.map(header => ({ name: header.name.trim(), value: header.value.trim() ? header.value.trim() : null }));
}

/** How the telemetry of a fleet reads in one line: off, or its signals and where they go. */
export function telemetrySummary(view: FleetTelemetryView): Readonly<{ key: MessageKey; detail: string | null }> {
  if (!view.enabled) return { key: "Off", detail: null };
  const signals = telemetrySignals.filter(signal => view.signals.includes(signal)).join(", ");
  return { key: view.route === "server" ? "Machines send through this server" : "Each machine sends to the collector", detail: `${signals} → ${view.endpoint ?? ""}` };
}
