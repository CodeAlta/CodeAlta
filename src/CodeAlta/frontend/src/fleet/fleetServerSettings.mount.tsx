// Disposable page: Settings > Fleet > Server on a host that holds one server of this computer, the owner's, whose fleet sends
// its telemetry through it. Driven by fleetServerSettings.browser.test.ts through `fleetFixture`: what the page asks is recorded.
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import type { FleetDeviceView, FleetServerDeviceRequest, FleetServerView, FleetTelemetryRequest, FleetTelemetryView, FleetUpdateCampaignView, FleetUpdatePolicyRequest,
  FleetUpdateStartRequest, FleetUpdatesView } from "#neoastra";
import { ShellLanguageContext } from "../shellLanguage";
import { FleetServerSettings, type FleetServerApi } from "./FleetServerSettings";

document.documentElement.classList.add("bp6-dark");

const calls: string[] = [];
// The owner has no recovery key yet; a laptop of theirs waits for its approval.
let recoveryKey = false;
const server = (): FleetServerView => ({ id: "home", url: "https://fleet.example", organization: "Home", link: "approved", connection: "online", problem: null, controller: true,
  spaces: [], recoveryKey });
const recoveryWords = "acid acorn actor agent alarm album alien alpha amber anchor angel ankle apple apron arena arrow atlas attic autumn award bacon badge bagel baker "
  + "bamboo banjo barn basil basket beach beacon bear";
const pending: FleetDeviceView = { id: "laptop", name: "Laptop", kind: "controller", status: "pending", online: false, words: "otter river robin saddle salmon pearl",
  createdAt: "2026-11-03T08:00:00Z" };
let devices: FleetDeviceView[] = [pending];
// The clipboard of the page is recorded, as headless Edge refuses to write the real one.
Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText: async (text: string) => { calls.push(JSON.stringify({ copied: text })); } } });
let telemetry: FleetTelemetryView = { enabled: true, endpoint: "https://otlp.example:4318", protocol: "http/protobuf", route: "server", signals: ["traces", "metrics"],
  headers: [{ name: "x-api-key", masked: "••••6789" }], environment: null, updatedAt: null };
// Two nodes, one offline; no campaign until the page starts one.
let updates: FleetUpdatesView = { serverVersion: "4.0.1", serverPending: null, serverLastOutcome: null,
  policy: { mode: "manual", pinnedVersion: null, feed: null, window: null, timeZone: null, lastCheckAt: null, lastCheckMessage: null }, campaigns: [],
  machines: [{ id: "mac", name: "Mac mini", version: "4.0.1", online: true }, { id: "linux", name: "Linux box", version: "4.0.1", online: false }] };
const campaign = (request: FleetUpdateStartRequest): FleetUpdateCampaignView => ({ id: "c1", sequence: 1, state: request.notBefore ? "scheduled" : "updating_nodes",
  targetVersion: request.version ?? "4.0.2", feed: request.feed ?? "https://api.nuget.org/v3/index.json", notBefore: request.notBefore, timeZone: request.timeZone,
  automatic: false, rollback: false, serverState: "pending", serverMessage: null, createdAt: "2026-11-03T08:00:00Z",
  machines: updates.machines.filter(machine => !request.machines || request.machines.includes(machine.id)).map(machine => ({ id: machine.id, name: machine.name,
    state: machine.online ? "switching" : "pending", running: machine.version, message: null, online: machine.online })) });
const unavailable = async () => { throw new Error("not in this fixture"); };
const api: FleetServerApi = {
  status: async () => ({ status: "ok", servers: [server()], deviceId: "device-1", words: "apple river stone" }),
  devices: async () => ({ status: "ok", devices, message: null }),
  approve: async (request: FleetServerDeviceRequest) => {
    calls.push(JSON.stringify({ approve: request.deviceId, words: request.words }));
    devices = devices.map(device => device.id === request.deviceId ? { ...device, status: "approved" } : device);
    return { status: "ok", device: devices.find(device => device.id === request.deviceId) ?? null, message: null };
  },
  createRecoveryKey: async () => {
    calls.push(JSON.stringify({ recoveryKey: "made" }));
    recoveryKey = true;
    return { status: "ok", words: recoveryWords, message: null };
  },
  telemetry: async () => ({ status: "ok", telemetry, message: null }),
  setTelemetry: async (request: FleetTelemetryRequest) => {
    calls.push(JSON.stringify({ enabled: request.enabled, endpoint: request.endpoint, route: request.route, signals: request.signals, headers: request.headers }));
    telemetry = { ...telemetry, enabled: request.enabled, signals: [...request.signals] };
    return { status: "ok", telemetry, message: null };
  },
  updates: async () => ({ status: "ok", updates, message: null }),
  startUpdate: async (request: FleetUpdateStartRequest) => {
    calls.push(JSON.stringify({ update: { version: request.version, feed: request.feed, notBefore: request.notBefore, timeZone: request.timeZone, machines: request.machines } }));
    updates = { ...updates, campaigns: [campaign(request)] };
    return { status: "ok", campaign: updates.campaigns[0], message: null };
  },
  cancelUpdate: async (request: { campaignId: string }) => {
    calls.push(JSON.stringify({ cancel: request.campaignId }));
    updates = { ...updates, campaigns: updates.campaigns.map(item => ({ ...item, state: "cancelled", serverState: "cancelled",
      machines: item.machines.map(machine => ({ ...machine, state: "cancelled" })) })) };
    return { status: "ok", campaign: updates.campaigns[0], message: null };
  },
  setUpdatePolicy: async (request: FleetUpdatePolicyRequest) => {
    calls.push(JSON.stringify({ policy: { mode: request.mode, pinnedVersion: request.pinnedVersion, window: request.window } }));
    updates = { ...updates, policy: { ...updates.policy, mode: request.mode, pinnedVersion: request.pinnedVersion, window: request.window, timeZone: request.timeZone } };
    return { status: "ok", message: null };
  },
  watch: async () => (async function* () { })(),
  create: unavailable, join: unavailable, leave: unavailable, createToken: unavailable, revoke: unavailable,
};
Object.assign(window, { fleetFixture: { calls } });

createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => {} }}>
    <div className="ide-shell settings-dialog-content" style={{ padding: 12, height: "100%", boxSizing: "border-box" }}>
      <FleetServerSettings epoch="12345678-1234-1234-1234-123456789abc" api={api} />
    </div>
  </ShellLanguageContext.Provider>
</StrictMode>);
