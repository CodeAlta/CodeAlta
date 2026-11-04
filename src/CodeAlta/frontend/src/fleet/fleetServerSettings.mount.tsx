// Disposable page: Settings > Fleet > Server on a host that holds one server of this computer, the owner's, whose fleet sends
// its telemetry through it. Driven by fleetServerSettings.browser.test.ts through `fleetFixture`: what the page asks is recorded.
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import type { FleetDeviceView, FleetServerDeviceRequest, FleetServerRecoverRequest, FleetServerView, FleetSignInLinkRequest, FleetSignInLoginView, FleetSignInSettingsRequest,
  FleetSignInSettingsView, FleetTelemetryRequest, FleetTelemetryView, FleetUpdateCampaignView, FleetUpdatePolicyRequest, FleetUpdateStartRequest, FleetUpdatesView } from "#neoastra";
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
// Passkeys, Microsoft and GitHub on, Google not registered by the project yet, no company sign-in; the owner linked nothing.
let signIn: FleetSignInSettingsView = { methods: [
  { id: "company", name: "Company sign-in", enabled: false, available: false }, { id: "passkey", name: "Passkey", enabled: true, available: true },
  { id: "microsoft", name: "Microsoft", enabled: true, available: true }, { id: "google", name: "Google", enabled: true, available: false },
  { id: "github", name: "GitHub", enabled: true, available: true }], issuer: null, clientId: null, hasClientSecret: false, displayName: null, groupsClaim: "groups",
  requireSso: false, version: 0, updatedAt: null, allowedDomains: [], allowedTenants: [] };
let logins: FleetSignInLoginView[] = [];
const unavailable = async () => { throw new Error("not in this fixture"); };
// Left, the page is empty; the recovery key of the person links it again.
let linked = true;
const api: FleetServerApi = {
  status: async () => ({ status: "ok", servers: linked ? [server()] : [], deviceId: "device-1", words: "apple river stone", personName: "Alex Example" }),
  leave: async () => { calls.push(JSON.stringify({ leave: "home" })); linked = false; return { status: "ok", message: null }; },
  recover: async (request: FleetServerRecoverRequest) => {
    calls.push(JSON.stringify({ recover: request.url, words: request.words }));
    // A word that is no word of a recovery key: the host says its position only.
    const typo = request.words.split(/\s+/u).filter(Boolean).findIndex(word => word === "zzzz");
    if (typo >= 0) return { status: "recovery.unknown_word", server: null, message: String(typo + 1), recoveryWords: null };
    if (request.words !== recoveryWords) return { status: "recovery.refused", server: null, message: null, recoveryWords: null };
    linked = true;
    recoveryKey = true;
    return { status: "ok", server: server(), message: null, recoveryWords: null };
  },
  devices: async () => ({ status: "ok", devices, message: null }),
  approve: async (request: FleetServerDeviceRequest) => {
    calls.push(JSON.stringify({ approve: request.deviceId, words: request.words, kind: request.kind }));
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
  signInSettings: async () => ({ status: "ok", settings: signIn, logins, message: null }),
  setSignInSettings: async (request: FleetSignInSettingsRequest) => {
    calls.push(JSON.stringify({ signIn: { version: request.version, enabled: request.enabled, issuer: request.issuer, clientId: request.clientId, secret: request.clientSecret,
      name: request.displayName, requireSso: request.requireSso, domains: request.allowedDomains, confirmWith: request.confirmWith } }));
    // As the server: a change of the company sign-in waits for the owner to sign in again.
    if (request.issuer !== signIn.issuer && !request.confirmWith) return { status: "sign_in.confirmation_required", settings: null, logins: [], message: null };
    signIn = { ...signIn, version: signIn.version + 1, issuer: request.issuer, clientId: request.clientId, hasClientSecret: !!request.clientSecret, displayName: request.displayName,
      requireSso: request.requireSso, allowedDomains: request.allowedDomains ?? [], methods: signIn.methods.map(method => ({ ...method, enabled: request.enabled.includes(method.id),
        name: method.id === "company" ? request.displayName ?? method.name : method.name, available: method.id === "company" ? !!request.issuer : method.available })) };
    return { status: "ok", settings: signIn, logins: [], message: null };
  },
  // GitHub gives a code and waits for the person, until Cancel; the others link at once.
  startSignInCode: async () => ({ status: "ok", message: null, code: { handle: "0123456789abcdef0123456789abcdef", userCode: "WDJB-MJHT",
    url: "https://github.com/login/device", expiresAt: "2026-11-04T12:15:00Z" } }),
  linkSignIn: async (request: FleetSignInLinkRequest, options?: { signal?: AbortSignal }) => {
    calls.push(JSON.stringify({ linkSignIn: request.method }));
    if (request.method === "github") {
      while (!options?.signal?.aborted) await new Promise(resolve => setTimeout(resolve, 20));
      calls.push(JSON.stringify({ linkCancelled: request.method }));
      return { status: "sign_in.cancelled", message: null };
    }
    logins = [...logins, { method: request.method, name: request.method === "passkey" ? "Passkey on Windows" : "alex@example.com", createdAt: "2026-11-04T08:00:00Z", lastUsedAt: null }];
    return { status: "ok", message: null };
  },
  openSignInPage: async (request: { handle: string }) => { calls.push(JSON.stringify({ openPage: request.handle })); return { status: "ok", message: null }; },
  watch: async () => (async function* () { })(),
  createToken: async () => { calls.push(JSON.stringify({ token: "home" })); return { status: "ok", token: "JOIN-5678", expiresAt: null, message: null }; },
  create: unavailable, join: unavailable, revoke: unavailable, signInMethods: unavailable, joinWithSignIn: unavailable,
};
Object.assign(window, { fleetFixture: { calls } });

createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => {} }}>
    <div className="ide-shell settings-dialog-content" style={{ padding: 12, height: "100%", boxSizing: "border-box" }}>
      <FleetServerSettings epoch="12345678-1234-1234-1234-123456789abc" api={api} />
    </div>
  </ShellLanguageContext.Provider>
</StrictMode>);
