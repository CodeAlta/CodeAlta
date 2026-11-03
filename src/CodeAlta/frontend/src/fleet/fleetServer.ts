// The state of the CodeAlta Servers of this computer (Settings > Fleet > Server, the server of a space) and of the directory of the
// Fleet canvas, as words and orders: the host says states and codes, these functions say what the page shows of them.
import type { FleetDeviceView, FleetMachineView, FleetServerView, FleetSessionView, FleetTelemetryView, FleetUpdateCampaignView, FleetUpdatePolicyView } from "#neoastra";
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
    case "update.conflict": return "A campaign runs, or the fleet is pinned: read the updates again.";
    case "update.invalid": return "The server refused this update.";
    case "update.not_found": case "update.unsupported_runtime": return "The feed has no such version of CodeAlta Node and Server.";
    case "update.feed_unavailable": case "update.invalid_source": return "The feed does not answer.";
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

/** What a refusal of an update says: those of the server, a request the host refused being an update the server would refuse. */
export function updateProblem(status: string): MessageKey | null {
  return status === "invalid_request" ? "The server refused this update." : serverProblem(status);
}

const campaignStates: Record<string, MessageKey> = {
  scheduled: "Scheduled", updating_nodes: "Updating the machines", updating_server: "Updating the server", done: "Done", partly_done: "Partly done",
  failed: "Failed", cancelled: "Cancelled",
};

/** The state of a campaign in words; an unknown one reads as its code would not: "Updating the machines". */
export function campaignStateLabel(state: string): MessageKey {
  return campaignStates[state] ?? "Updating the machines";
}

/** The color of the state of a campaign. */
export function campaignStateIntent(state: string): "success" | "warning" | "danger" | "primary" | "none" {
  switch (state) {
    case "done": return "success";
    case "partly_done": return "warning";
    case "failed": return "danger";
    case "cancelled": return "none";
    default: return "primary";
  }
}

const machineStates: Record<string, MessageKey> = {
  pending: "Pending", instructed: "Asked", waiting: "Waiting for its time", downloading: "Downloading", switching: "Switching when idle", updated: "Updated",
  failed: "Failed", rolled_back: "Rolled back", refused: "Refused", cancelled: "Cancelled", superseded: "Superseded",
};

/** Where a machine stands in a campaign, in words. */
export function machineStateLabel(state: string): MessageKey {
  return machineStates[state] ?? "Pending";
}

/** The color of where a machine stands. */
export function machineStateIntent(state: string): "success" | "warning" | "danger" | "primary" | "none" {
  switch (state) {
    case "updated": return "success";
    case "failed": case "rolled_back": case "refused": return "danger";
    case "pending": case "cancelled": case "superseded": return "none";
    default: return "primary";
  }
}

/** Whether a campaign can still be cancelled: before the server's step. */
export function canCancelCampaign(campaign: Pick<FleetUpdateCampaignView, "state">): boolean {
  return campaign.state === "scheduled" || campaign.state === "updating_nodes";
}

/** How many machines of a campaign run its version, of how many. */
export function campaignProgress(campaign: Pick<FleetUpdateCampaignView, "machines">): Readonly<{ updated: number; total: number }> {
  const counted = campaign.machines.filter(machine => machine.state !== "superseded");
  return { updated: counted.filter(machine => machine.state === "updated").length, total: counted.length };
}

/** The time zone of this computer, as the person chooses times in it. */
export function localTimeZone(): string {
  try { return Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC"; } catch { return "UTC"; }
}

/** The update of the fleet a person asks, as the page edits it. */
export type UpdateDraft = Readonly<{
  version: string;
  feed: string;
  when: "now" | "at";
  /** The value of a datetime-local field: a date and a time of this computer's time zone. */
  at: string;
  /** The machines chosen; null for all. */
  machines: readonly string[] | null;
}>;

/** A new update of the fleet: the latest version, of nuget.org, now, on every machine. */
export const emptyUpdateDraft: UpdateDraft = { version: "", feed: "", when: "now", at: "", machines: null };

/** The time of a draft in UTC (ISO 8601), from the date and the time of this computer; null for now or a time that does not read. */
export function updateStart(draft: Pick<UpdateDraft, "when" | "at">): string | null {
  if (draft.when !== "at" || !draft.at) return null;
  const time = new Date(draft.at);
  return Number.isNaN(time.getTime()) ? null : time.toISOString();
}

const versionPattern = /^\d+\.\d+\.\d+(\.\d+)?(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$/u;

/** Whether a text is a version (major.minor.patch, an optional prerelease). */
export function isVersion(text: string): boolean {
  return versionPattern.test(text.trim());
}

/** What is wrong with an update draft, in words; null when the host can sign it. */
export function updateDraftProblem(draft: UpdateDraft, now: Date = new Date()): MessageKey | null {
  if (draft.version.trim() && !isVersion(draft.version)) return "This is not a version.";
  if (draft.when === "at") {
    const start = updateStart(draft);
    if (!start) return "Choose a date and a time.";
    if (new Date(start).getTime() < now.getTime() - 60_000) return "Choose a time to come.";
  }
  if (draft.machines && draft.machines.length === 0) return "Choose at least one machine.";
  return null;
}

/** The update policy of a fleet as the page edits it. */
export type PolicyDraft = Readonly<{ mode: "manual" | "pinned" | "automatic"; pinnedVersion: string; feed: string; window: string; timeZone: string }>;

/** The draft of the policy the host gave; a new window takes this computer's time zone. */
export function policyDraft(policy: FleetUpdatePolicyView, timeZone: string = localTimeZone()): PolicyDraft {
  const mode = policy.mode === "pinned" || policy.mode === "automatic" ? policy.mode : "manual";
  return { mode, pinnedVersion: policy.pinnedVersion ?? "", feed: policy.feed ?? "", window: policy.window ?? "", timeZone: policy.timeZone ?? timeZone };
}

/** What is wrong with a policy draft, in words; null when the host can sign it. */
export function policyDraftProblem(draft: PolicyDraft): MessageKey | null {
  if (draft.mode === "pinned" && !isVersion(draft.pinnedVersion)) return "This is not a version.";
  if (draft.mode === "automatic" && draft.window && !/^([01]\d|2[0-3]):[0-5]\d$/u.test(draft.window)) return "The window starts at a time of the day (HH:mm).";
  return null;
}

/** How the policy of a fleet reads in one line. */
export function policySummary(policy: FleetUpdatePolicyView): Readonly<{ key: MessageKey; detail: string | null }> {
  switch (policy.mode) {
    case "pinned": return { key: "Pinned", detail: policy.pinnedVersion };
    case "automatic": return { key: "Automatic", detail: [policy.feed ?? "nuget.org", policy.window ? `${policy.window} ${policy.timeZone ?? "UTC"}` : null].filter(Boolean).join(" · ") };
    default: return { key: "Manual", detail: null };
  }
}
