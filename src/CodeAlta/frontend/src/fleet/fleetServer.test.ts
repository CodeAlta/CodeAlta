import test from "node:test";
import assert from "node:assert/strict";
import type { FleetDeviceView, FleetMachineView, FleetServerView, FleetSessionView, FleetTelemetryView } from "#neoastra";
import { fleetGroups, fleetSummary, joinCommand, keyWords, orderedDevices, serverAddressOf, serverConnection, serverConnectionIntent, serverConnectionLabel, serverOfSpace,
  serverProblem, sessionModel, sessionState, sessionStateLabel, telemetryDraft, telemetryHeaderEdits, telemetryProblem, telemetrySummary } from "./fleetServer";
import { changedSessionOptions, storedSessionOptions } from "../sessionSettings/sessionOptions";

const machine = (id: string, look: Partial<FleetMachineView> = {}): FleetMachineView => ({ id, name: id, kind: "node", os: "macos", version: "4.0.0", online: true,
  presence: "active", activeSessions: 0, isThisComputer: false, isPaired: false, providers: [], sessions: 0, isReachable: false, ...look });
const session = (id: string, machineId: string, state: string, updatedAt: string, look: Partial<FleetSessionView> = {}): FleetSessionView => ({ id, machineId, state,
  providerType: "anthropic", model: "claude-opus-5-5", createdAt: "2026-11-03T08:00:00Z", updatedAt, turns: 0, tokens: 0, canOpen: false, title: null, ...look });
const server = (id: string, look: Partial<FleetServerView> = {}): FleetServerView => ({ id, url: `https://${id}.example`, organization: null, link: "approved",
  connection: "online", problem: null, controller: true, spaces: [], ...look });

test("the connection of a server reads as words and an intent; what the host does not say is offline", () => {
  assert.equal(serverConnection(server("a", { connection: "pending" })), "pending");
  assert.equal(serverConnection(server("a", { connection: "something new" })), "offline");
  assert.equal(serverConnectionLabel("pending"), "Waiting for approval");
  assert.equal(serverConnectionLabel("online"), "Online");
  assert.deepEqual(["online", "pending", "revoked", "offline"].map(value => serverConnectionIntent(value as never)), ["success", "warning", "danger", "none"]);
});

test("an address of a server is http or https, with a host and no query; its final slash goes", () => {
  assert.equal(serverAddressOf(" http://127.0.0.1:2586/ "), "http://127.0.0.1:2586");
  assert.equal(serverAddressOf("https://fleet.example/prefix"), "https://fleet.example/prefix");
  for (const wrong of ["", "fleet.example", "ftp://fleet.example", "https://fleet.example/?a=1", "https://user@fleet.example", "not a url"]) assert.equal(serverAddressOf(wrong), "", wrong);
});

test("the refusals of a server say what to do; an unknown one says the operation did not complete", () => {
  assert.equal(serverProblem("ok"), null);
  assert.equal(serverProblem("setup.invalid_code"), "This is not the setup code the server waits for.");
  assert.equal(serverProblem("setup.loopback_only"), "Set the server up from the computer it runs on.");
  assert.equal(serverProblem("enrollment.token_used"), "This enrollment code was used already.");
  assert.equal(serverProblem("unreachable"), "The server does not answer.");
  assert.equal(serverProblem("whatever"), "The operation did not complete.");
});

test("the devices that wait for an approval come first, the latest first; then the approved, then the revoked", () => {
  const device = (id: string, status: string, createdAt: string): FleetDeviceView => ({ id, name: id, kind: "node", status, online: false, words: "", createdAt });
  const ordered = orderedDevices([device("old-approved", "approved", "2026-11-01"), device("revoked", "revoked", "2026-11-03"), device("pending-1", "pending", "2026-11-02"),
    device("pending-2", "pending", "2026-11-03")]);
  assert.deepEqual(ordered.map(item => item.id), ["pending-2", "pending-1", "old-approved", "revoked"]);
});

test("a machine joins with the command of its token, and the words of a key split", () => {
  assert.equal(joinCommand("https://fleet.example", "aet_x"), "altanode join https://fleet.example --token aet_x");
  assert.deepEqual(keyWords("  apple  river stone "), ["apple", "river", "stone"]);
  assert.deepEqual(keyWords(null), []);
});

test("the canvas groups the sessions by machine: this computer first, then those online; those that wait first in each", () => {
  const machines = [machine("zeta", { online: false }), machine("mac", { name: "Mac mini" }), machine("here", { name: "Desktop", isThisComputer: true }), machine("alpha")];
  const sessions = [session("s-idle", "mac", "idle", "2026-11-03T10:00:00Z"), session("s-waits", "mac", "requires_action", "2026-11-03T09:00:00Z"),
    session("s-runs", "mac", "running", "2026-11-03T09:30:00Z"), session("s-old", "mac", "archived", "2026-11-01T09:00:00Z"), session("s-here", "here", "idle", "2026-11-03T08:00:00Z")];
  const groups = fleetGroups(machines, sessions);
  assert.deepEqual(groups.map(group => group.machine.id), ["here", "alpha", "mac", "zeta"]);
  assert.deepEqual(groups.find(group => group.machine.id === "mac")!.sessions.map(item => item.id), ["s-waits", "s-runs", "s-idle"], "archived sessions are left out");
  assert.deepEqual(fleetGroups(machines, sessions, true).find(group => group.machine.id === "mac")!.sessions.map(item => item.id), ["s-waits", "s-runs", "s-idle", "s-old"]);
  assert.deepEqual(fleetSummary(machines, sessions), { online: 3, machines: 4, working: 1, waiting: 1 });
});

test("a session reads its state and model in words; an unknown state is idle", () => {
  assert.equal(sessionState({ state: "requires_action" }), "requires_action");
  assert.equal(sessionState({ state: "new" }), "idle");
  assert.equal(sessionStateLabel("requires_action"), "Waits for you");
  assert.equal(sessionStateLabel("running"), "Working");
  assert.equal(sessionModel({ model: "gpt-6.1", providerType: "openai" }), "gpt-6.1 · openai");
  assert.equal(sessionModel({ model: null, providerType: "openai" }), "openai");
  assert.equal(sessionModel({ model: null, providerType: null }), "");
});

const telemetry = (look: Partial<FleetTelemetryView> = {}): FleetTelemetryView => ({ enabled: true, endpoint: "https://otlp.example:4318", protocol: "http/protobuf",
  route: "server", signals: ["metrics", "traces"], headers: [{ name: "x-api-key", masked: "••••3f2a" }], environment: null, updatedAt: null, ...look });

test("the telemetry of a fleet reads in one line: off, or its signals and where they go", () => {
  assert.deepEqual(telemetrySummary(telemetry({ enabled: false })), { key: "Off", detail: null });
  assert.deepEqual(telemetrySummary(telemetry()), { key: "Machines send through this server", detail: "traces, metrics → https://otlp.example:4318" });
  assert.equal(telemetrySummary(telemetry({ route: "direct" })).key, "Each machine sends to the collector");
});

test("a header held by the server stays masked and keeps its value until a new one is typed", () => {
  const draft = telemetryDraft(telemetry());
  assert.deepEqual(draft.signals, ["traces", "metrics"], "the signals in their order");
  assert.deepEqual(draft.headers, [{ name: "x-api-key", value: "", masked: "••••3f2a" }], "the page never holds the secret");
  assert.deepEqual(telemetryHeaderEdits(draft), [{ name: "x-api-key", value: null }], "an untouched header is sent without a value: the host keeps it");
  assert.deepEqual(telemetryHeaderEdits({ ...draft, headers: [{ name: " x-api-key ", value: " new ", masked: "••••3f2a" }] }), [{ name: "x-api-key", value: "new" }]);
});

test("a telemetry setting names a collector and a signal, the server route speaks HTTP, and headers are names and values", () => {
  const draft = telemetryDraft(telemetry());
  assert.equal(telemetryProblem(draft), null);
  assert.equal(telemetryProblem({ ...draft, enabled: false, endpoint: "" }), null, "a setting that is off needs no collector");
  assert.equal(telemetryProblem({ ...draft, endpoint: "otlp.example:4318" }), "This is not the address of a collector.");
  assert.equal(telemetryProblem({ ...draft, signals: [] }), "Choose at least one signal.");
  assert.equal(telemetryProblem({ ...draft, protocol: "grpc" }), "The server forwards OTLP over HTTP only.");
  assert.equal(telemetryProblem({ ...draft, route: "direct", protocol: "grpc" }), null);
  for (const header of [{ name: "", value: "v", masked: null }, { name: "x key", value: "v", masked: null }, { name: "x-key", value: "", masked: null },
    { name: "x-key", value: "a,b", masked: null }]) assert.equal(telemetryProblem({ ...draft, headers: [header] }), "A header needs a name and a value, without a comma.");
  assert.equal(telemetryProblem({ ...draft, headers: [{ name: "x-key", value: "a", masked: null }, { name: "X-Key", value: "b", masked: null }] }),
    "A header needs a name and a value, without a comma.", "a name is there once");
  assert.equal(serverProblem("telemetry.conflict"), "Another owner changed the telemetry meanwhile: try again.");
});

test("a space publishes to the server that lists it", () => {
  const servers = [server("home", { spaces: ["default"] }), server("work", { spaces: ["contoso"] })];
  assert.equal(serverOfSpace(servers, "contoso")?.id, "work");
  assert.equal(serverOfSpace(servers, "other"), null);
});

test("a Private session records its options and counts as a changed option", () => {
  assert.deepEqual(storedSessionOptions({ subSessions: true, private: true }), { subSessions: true, private: true });
  assert.equal(storedSessionOptions({ subSessions: true, private: false }), null, "the defaults record nothing");
  assert.equal(changedSessionOptions({ subSessions: true, private: true }), 1);
});
