// Disposable page: the page of a paired machine this computer manages, then of one it does not. Driven by
// machinePage.browser.test.ts through `machineFixture`: what the page asks is recorded.
import { StrictMode, useState } from "react";
import { createRoot } from "react-dom/client";
import type { MachineDeviceView, MachineManagement, MachineProviderStatusView, MachineServerLinkView, MachineView, SecretView, VariableView } from "#neoastra";
import { ShellLanguageContext } from "../shellLanguage";
import { MachinePage, type MachinePageApi } from "./MachinePage";
import { localSecret, noCredentials } from "../secrets/credentials";

document.documentElement.classList.add("bp6-dark");

const calls: string[] = [];
const machine = (id: string, name: string, permissions: string[]): MachineView => ({ id, name, state: "online", os: "linux", version: "4.0.0", address: `${name}:2585`,
  fingerprint: "ABCD EFGH", words: "apple river stone cloud amber tiger", sessions: 1, lastSeen: null, problem: null, permissions, pairedAt: "2026-11-01T00:00:00Z",
  paired: true, channel: "direct", tags: [], working: null, queued: null, sessionLimit: null, serversToJoin: [] });
let devices: MachineDeviceView[] = [
  { id: "me", name: "desktop", fingerprint: "HS62 JUOJ W5CV", permissions: ["view", "drive", "start", "manage"], pairedAt: "2026-11-01T00:00:00Z", revokedAt: null,
    channels: 1, isThisComputer: true },
  { id: "phone", name: "phone", fingerprint: "PH0N EEEE AAAA", permissions: ["view"], pairedAt: "2026-11-02T00:00:00Z", revokedAt: null, channels: 0, isThisComputer: false },
];
// What a session of the machine without a mode of its own does: it bypasses permissions, as a machine starts.
let sessionMode = "bypassPermissions";
// The policy of the machine for agents of other machines: off in every space, as a machine starts.
const agentPolicy = { spaces: [] as string[], sandbox: null as string | null, allowBypass: false };
// The people of the organization the machine names, and one who asked to use it.
const machinePeople = {
  people: [{ personId: "bea", name: "Bea", permissions: ["view", "drive"] }],
  asks: [{ personId: "cy", name: "Cy", askedAt: "2026-11-04T09:00:00Z" }],
  others: [{ personId: "cy", name: "Cy", permissions: [] as string[] }, { personId: "dan", name: "Dan", permissions: [] as string[] }],
};
let projects = [{ id: "p1", name: "app", path: "/home/me/code/app", repository: "github.com/me/app" }];
// The servers the managed machine joined, as it says its connection stands: none, but in the variant that joins another one.
let managedServers: MachineServerLinkView[] = [];
const view = (id: string): MachineManagement => id === "managed"
  ? { status: "ok", message: null, version: "4.0.0", sessionsAtWork: 0, updating: false, update: { running: "4.0.0", installed: "4.0.0", pendingVersion: null, pendingPhase: null,
    lastVersion: null, lastOutcome: null }, devices, projects, problems: [], providers: [{ key: "codex", name: "Codex", isDefault: true, defaultModel: "GPT-6.1", models: 3 }],
    thisDevice: "me", worktrees: { count: 3, kilobytes: 2.5 * 1024 * 1024, idleDays: 7, measuredAt: "2026-11-06T10:00:00Z" },
    sandbox: { profile: null, available: false, backend: "linux-bubblewrap", reason: "bubblewrap (`bwrap` 0.5 or later) is not installed or cannot create user namespaces.",
      setup: "sudo apt install bubblewrap slirp4netns" }, servers: managedServers,
    service: { installed: true, startsAtBoot: false, lines: ["Service: a systemd user unit (~/.config/systemd/user/altanode.service).",
      "After a restart: it waits until me logs in (lingering is off: sudo loginctl enable-linger me starts it at boot)."], fix: "sudo loginctl enable-linger me", fixWhere: "terminal" } }
  : { status: "not_manager", message: null, version: null, sessionsAtWork: 0, updating: false, update: null, devices: [], projects: [], problems: [], providers: [], thisDevice: "me", worktrees: null, sandbox: null, service: null, servers: [] };
const api: MachinePageApi = {
  management: async request => view(request.machineId),
  limits: async () => ({ status: "ok", message: null, sessions: 3 }),
  setLimits: async request => { calls.push(JSON.stringify({ limits: request.sessions })); return { status: "ok", message: null }; },
  seats: async () => ({ status: "ok", message: null, limit: 3, working: 3, queue: [
    { id: "run-1", sessionId: "s9", title: "Nightly report", kind: "automation", position: 1, queuedAt: "2026-11-09T10:00:00Z" }] }),
  startSeat: async request => { calls.push(JSON.stringify({ startSeat: request.id })); return { status: "ok", message: null }; },
  cancelSeat: async request => { calls.push(JSON.stringify({ cancelSeat: request.id })); return { status: "ok", message: null }; },
  setDevicePermission: async request => {
    calls.push(JSON.stringify({ permission: [request.deviceId, request.permission, request.allowed] }));
    devices = devices.map(device => device.id !== request.deviceId ? device
      : { ...device, permissions: request.allowed ? [...device.permissions, request.permission] : device.permissions.filter(value => value !== request.permission) });
    return { status: "ok", message: null };
  },
  revokeDevice: async request => {
    calls.push(JSON.stringify({ revoke: request.deviceId }));
    devices = devices.map(device => device.id === request.deviceId ? { ...device, revokedAt: "2026-11-03T00:00:00Z" } : device);
    return { status: "ok", message: null };
  },
  cloneProject: async request => {
    calls.push(JSON.stringify({ clone: request.value }));
    projects = [...projects, { id: "p2", name: "repo", path: "/home/me/code/github.com/owner/repo", repository: "github.com/owner/repo" }];
    return { status: "ok", message: "Cloned into /home/me/code/github.com/owner/repo." };
  },
  addProject: async request => { calls.push(JSON.stringify({ add: request.value })); return { status: "ok", message: null }; },
  removeProject: async request => {
    calls.push(JSON.stringify({ remove: request.value }));
    projects = projects.filter(project => project.id !== request.value);
    return { status: "ok", message: null };
  },
  readLog: async () => ({ status: "ok", text: "2026-11-03 INF CodeAlta Node started.\n", path: "/home/me/.alta/logs/altanode.log", truncated: false, message: null }),
  updateMachine: async request => {
    calls.push(JSON.stringify({ update: request.check ? "check" : "install" }));
    return request.check ? { status: "available", running: "4.0.0", latest: "4.0.1", target: "4.0.1", message: null }
      : { status: "staged", running: "4.0.0", latest: "4.0.1", target: "4.0.1", message: null };
  },
  // The secrets of the machine: by name, never their values; a rotation of one that is gone is refused as the machine does.
  secrets: async () => ({ status: "ok", protection: "libsecret", secrets: [...secrets.values()].map(secret => localSecret(secret)), variables: [...variables.values()], ...noCredentials }),
  setSecret: async request => { calls.push(JSON.stringify({ setSecret: request.name, machine: request.machineId })); setSecret(request.name, request.value); return { status: "ok", message: null }; },
  rotateSecret: async request => {
    calls.push(JSON.stringify({ rotateSecret: request.name }));
    if (!secrets.has(request.name)) return { status: "secret.not_found", message: `No secret is named ${request.name} on this machine.` };
    setSecret(request.name, request.value);
    return { status: "ok", message: null };
  },
  deleteSecret: async request => { calls.push(JSON.stringify({ deleteSecret: request.name })); secrets.delete(request.name); return { status: "ok", message: null }; },
  setVariable: async request => {
    calls.push(JSON.stringify({ setVariable: request.name, value: request.value }));
    variables.set(request.name, { name: request.name, value: request.value, version: (variables.get(request.name)?.version ?? 0) + 1, setBy: "desktop (manage)", setAt: "2026-11-03T09:00:00Z" });
    return { status: "ok", message: null };
  },
  deleteVariable: async request => { calls.push(JSON.stringify({ deleteVariable: request.name })); variables.delete(request.name); return { status: "ok", message: null }; },
  sessionDefaults: async () => ({ status: "ok", message: null, permissionMode: sessionMode }),
  setSessionPermissionMode: async request => { calls.push(JSON.stringify({ sessionMode: request.permissionMode })); sessionMode = request.permissionMode; return { status: "ok", message: null }; },
  agents: async () => ({ status: "ok", message: null, sandbox: agentPolicy.sandbox, allowBypass: agentPolicy.allowBypass, sandboxes: ["workspace", "strict"],
    spaces: [{ id: "default", name: "Default" }, { id: "work", name: "Work" }].map(space => ({ ...space, allowed: agentPolicy.spaces.includes(space.id) })) }),
  setAgentSpace: async request => {
    calls.push(JSON.stringify({ agentSpace: [request.space, request.allowed] }));
    agentPolicy.spaces = request.allowed ? [...agentPolicy.spaces, request.space] : agentPolicy.spaces.filter(space => space !== request.space);
    return { status: "ok", message: null };
  },
  setAgentSandbox: async request => { calls.push(JSON.stringify({ agentSandbox: request.sandbox })); agentPolicy.sandbox = request.sandbox ?? null; return { status: "ok", message: null }; },
  people: async () => ({ status: "ok", message: null, people: machinePeople.people, asks: machinePeople.asks, others: machinePeople.others, owner: null }),
  setMachinePerson: async request => {
    calls.push(JSON.stringify({ person: { personId: request.personId, permissions: request.permissions, refuse: request.refuse } }));
    return { status: "ok", message: null };
  },
  setAgentBypass: async request => { calls.push(JSON.stringify({ agentBypass: request.allowed })); agentPolicy.allowBypass = request.allowed; return { status: "ok", message: null }; },
  // The providers of the machine: Codex ready, OpenAI skipped for its key, Copilot not signed in. A key typed on the page makes OpenAI ready;
  // the sign-in of Copilot gives a code, and ends at the second wait.
  providers: async () => ({ status: "ok", message: null, providers: [...providerList], secretProtection: "libsecret",
    types: [{ type: "openai-responses", name: "OpenAI", credential: "key", signIn: false }, { type: "anthropic", name: "Anthropic", credential: "key", signIn: false },
      { type: "copilot", name: "GitHub Copilot", credential: "sign_in", signIn: true }] }),
  setProvider: async request => {
    calls.push(JSON.stringify({ setProvider: request.key, type: request.type, keyGiven: !!request.apiKey, makeDefault: request.makeDefault }));
    const known = providerList.find(provider => provider.key === request.key);
    const next: MachineProviderStatusView = { ...(known ?? { key: request.key, name: request.displayName || "Anthropic", type: request.type ?? "anthropic", credential: "key", signIn: false,
      isDefault: false, model: null, apiUrl: null }), enabled: true, state: "ready", reason: null, detail: null, keySecret: `PROVIDER_${request.key.toUpperCase()}_API_KEY` };
    providerList = known ? providerList.map(provider => provider.key === request.key ? next : provider) : [...providerList, next];
    return { status: "ok", provider: next, message: null };
  },
  providerSignIn: async request => {
    calls.push(JSON.stringify({ signIn: request.key }));
    return { status: "ok", message: null, url: "https://github.com/login/device", userCode: "WDJB-MJHT", expiresAt: null };
  },
  providerSignInWait: async request => {
    if (++signInWaits < 2) { await new Promise(resolve => setTimeout(resolve, 800)); return { status: "waiting", account: null, code: null }; }
    providerList = providerList.map(provider => provider.key === request.key ? { ...provider, state: "ready", reason: null } : provider);
    return { status: "completed", account: "octocat", code: null };
  },
  // What the machine can add: Claude Code it installs, Codex CLI and an ACP agent of its plugins, a subscription, APIs, a model server.
  providerOffers: async () => oldNode ? { status: "method_not_found", message: "Unknown method control.providers.offers.", error: null, offers: [] } : ({ status: "ok", message: null, error: null, offers: [
    { id: "claude-code", type: "claude-code", name: "Claude Code", group: "agent", credential: "cli", signIn: false, description: "Anthropic's agent.", icon: null,
      program: { state: "missing", version: "2.1.322", update: null, detail: null }, unavailable: null, catalog: null, configuredKey: null },
    { id: "codex-cli:codex-cli", type: "codex-cli", name: "Codex CLI", group: "agent", credential: "cli", signIn: false, description: "OpenAI's Codex agent.", icon: null,
      program: { state: "missing", version: "0.130.0", update: null, detail: null }, unavailable: null, catalog: "Codex CLI", configuredKey: null },
    { id: "acp:gemini", type: "acp", name: "Gemini CLI", group: "agent", credential: "cli", signIn: false, description: "Google's agent.", icon: null,
      program: { state: "runner", version: "0.9.0", update: null, detail: "npx" }, unavailable: "Node.js is not installed on the machine.", catalog: "ACP registry", configuredKey: null },
    { id: "copilot", type: "copilot", name: "GitHub Copilot", group: "subscription", credential: "sign_in", signIn: true, description: null, icon: null, program: null, unavailable: null,
      catalog: null, configuredKey: "copilot" },
    { id: "anthropic", type: "anthropic", name: "Anthropic", group: "key", credential: "key", signIn: false, description: null, icon: null, program: null, unavailable: null, catalog: null,
      configuredKey: null },
    { id: "llama-cpp", type: "llama-cpp", name: "llama.cpp", group: "local", credential: "none", signIn: false, description: null, icon: null, program: null, unavailable: null, catalog: null,
      configuredKey: null },
  ] }),
  // Claude Code is downloaded by the machine (one wait), then written: not signed in yet, with the sign-in of the machine to make.
  addProvider: async request => {
    calls.push(JSON.stringify({ addProvider: request.offerId }));
    operationWaits = 0;
    return { status: "ok", message: null, id: "op1", state: "running", fraction: 0, key: null };
  },
  providerOperationWait: async () => {
    if (++operationWaits < 2) { await new Promise(resolve => setTimeout(resolve, 600)); return { status: "ok", message: "Claude Code 2.1.322 is being downloaded…", id: "op1", state: "running", fraction: 0.5, key: null }; }
    if (!providerList.some(provider => provider.key === "claude-code")) providerList = [...providerList, { key: "claude-code", name: "Claude Code", type: "claude-code", enabled: true,
      state: "failed", reason: "start_failed", detail: "Claude Code is not signed in on this machine.", credential: "cli", keySecret: null, signIn: false, isDefault: false, model: null,
      apiUrl: null, program: { state: "installed", version: "2.1.322", update: null, detail: "/home/me/.alta/cache/claude-code/2.1.322/linux-x64/claude" }, icon: null }];
    claudeAdded = true;
    await new Promise(resolve => setTimeout(resolve, 600));
    return { status: "ok", message: null, id: "op1", state: "completed", fraction: null, key: "claude-code" };
  },
  updateProvider: async request => { calls.push(JSON.stringify({ updateProvider: request.key })); return { status: "ok", message: null, id: "op2", state: "completed", fraction: null, key: request.key }; },
  // Codex signs in with the browser of this computer and its callback, Mistral Vibe on a page the machine waits on; each ends at its second wait.
  signIns: async () => ({ status: "ok", message: null, items: [
    ...(claudeAdded ? [{ kind: "provider", key: "claude-code", space: null, title: "Claude Code", state: signedIn.has("claude") ? "signed_in" : "unknown", account: null,
      expiresAt: null, ways: ["code"], detail: null }] : []),
    { kind: "provider", key: "codex", space: null, title: "Codex", state: signedIn.has("codex") ? "signed_in" : "signed_out", account: signedIn.has("codex") ? "me@example.com" : null,
      expiresAt: null, ways: ["browser"], detail: null },
    { kind: "provider", key: "vibe", space: null, title: "Mistral Vibe", state: signedIn.has("vibe") ? "signed_in" : "signed_out", account: null, expiresAt: null,
      ways: ["page"], detail: null },
    { kind: "provider", key: "agent", space: null, title: "Agent", state: signedIn.has("agent") ? "signed_in" : "unknown", account: null, expiresAt: null,
      ways: ["secret"], detail: null },
    { kind: "provider", key: "opencode", space: null, title: "OpenCode", state: "unknown", account: null, expiresAt: null, ways: [],
      detail: "It signs in in a terminal of the machine: run opencode auth login there." },
    { kind: "mcp", key: "linear", space: null, title: "Linear", state: "signed_in", account: null, expiresAt: null, ways: ["browser"], detail: null },
  ] }),
  signInStart: async request => {
    calls.push(JSON.stringify({ signInStart: [request.kind, request.key] }));
    signInWaits = 0;
    if (request.key === "claude-code") return { status: "ok", message: null, attemptId: "claude", way: "code", page: "https://claude.com/cai/oauth/authorize?code=true&state=s1",
      userCode: null, ask: null, expiresAt: "2026-11-07T15:00:00Z" };
    if (request.key === "agent") return { status: "ok", message: null, attemptId: "agent", way: "secret", page: "https://platform.example.com/api-keys", userCode: null,
      ask: "API key (OPENAI_API_KEY)", expiresAt: "2026-11-07T15:00:00Z" };
    return request.key === "vibe"
      ? { status: "ok", message: null, attemptId: "vibe", way: "page", page: "https://console.mistral.ai/vibe/sign-in/p1", userCode: null, ask: null, expiresAt: "2026-11-07T15:00:00Z" }
      : { status: "ok", message: null, attemptId: "codex", way: "browser", page: "https://auth.example.com/authorize?state=s1", userCode: null, ask: null,
        expiresAt: "2026-11-07T15:00:00Z" };
  },
  signInWait: async request => {
    // A key ends its sign-in once it is typed, not before.
    if (request.attemptId === "agent" || request.attemptId === "claude") {
      await new Promise(resolve => setTimeout(resolve, 300));
      return signedIn.has(request.attemptId) ? { status: "completed", account: null, code: null, message: null } : { status: "waiting", account: null, code: null, message: null };
    }
    if (++signInWaits < 2) { await new Promise(resolve => setTimeout(resolve, 800)); return { status: "waiting", account: null, code: null, message: null }; }
    signedIn.add(request.attemptId);
    return { status: "completed", account: request.attemptId === "codex" ? "me@example.com" : null, code: null, message: null };
  },
  // What is typed goes to the machine; the page records only that it was sent.
  signInCode: async request => { calls.push(JSON.stringify({ signInCode: request.attemptId })); signedIn.add(request.attemptId); return { status: "completed", account: null, code: null, message: null }; },
  signInCancel: async () => ({ status: "failed", account: null, code: "canceled", message: null }),
  // A link of a sign-in dialog opens through the host: the page records the address it asked to open.
  openSignInPage: async request => { calls.push(JSON.stringify({ openPage: request.url })); return { status: "ok" }; },
  signOut: async request => { calls.push(JSON.stringify({ signOut: [request.kind, request.key] })); return { status: "ok", item: null, message: null }; },
  labels: async () => ({ status: "ok", message: null, labels: [...labels] }),
  setLabels: async request => { calls.push(JSON.stringify({ labels: request.labels })); labels = [...request.labels]; return { status: "ok", message: null }; },
  joinServer: async request => { calls.push(JSON.stringify({ joinServer: [request.machineId, request.linkId] })); return { status: "ok", name: "build-box", words: "apple river stone cloud amber tiger", message: null }; },
};
let labels: string[] = ["gpu"];
let signInWaits = 0;
let operationWaits = 0;
// A node of an older version, which does not know the offers.
let oldNode = false;
let claudeAdded = false;
const signedIn = new Set<string>();
let providerList: MachineProviderStatusView[] = [
  { key: "codex", name: "Codex", type: "codex", enabled: true, state: "ready", reason: null, detail: null, credential: "sign_in", keySecret: null, signIn: false, isDefault: true,
    model: "gpt-6.1", apiUrl: null },
  { key: "openai", name: "OpenAI", type: "openai-responses", enabled: true, state: "not_ready", reason: "key_missing", detail: "OPENAI_API_KEY", credential: "key", keySecret: null,
    signIn: false, isDefault: false, model: null, apiUrl: null },
  { key: "copilot", name: "GitHub Copilot", type: "copilot", enabled: true, state: "not_ready", reason: "sign_in_needed", detail: null, credential: "sign_in", keySecret: null,
    signIn: true, isDefault: false, model: null, apiUrl: null },
];
const secrets = new Map<string, SecretView & { value: string }>();
const variables = new Map<string, VariableView>([["GRAFANA_URL", { name: "GRAFANA_URL", value: "https://grafana.example", version: 1, setBy: "build-box owner", setAt: "2026-11-02T09:00:00Z" }]]);
function setSecret(name: string, value: string) {
  secrets.set(name, { ...localSecret({ name, version: (secrets.get(name)?.version ?? 0) + 1, setBy: "desktop (manage)", setAt: "2026-11-03T09:00:00Z" }), value });
}

function Fixture() {
  const [shown, setShown] = useState("managed");
  Object.assign(window, { machineFixture: { calls, show: setShown, oldNode: () => { oldNode = true; } } });
  // A node whose connection to a server it joined fails after its .NET runtime was replaced under it: only the node knows why.
  managedServers = shown === "joinable" ? [{ name: "Work", state: "disconnected",
    message: "transport_error Could not load file or assembly 'System.Net.WebSockets.Client, Version=10.0.0.0'." }] : [];
  return <MachinePage key={shown} epoch="12345678-1234-1234-1234-123456789abc" api={api}
    machine={shown === "managed" ? machine("managed", "build-box", ["view", "drive", "start", "manage"])
      // Paired before this computer had a server: not on it yet, so the person's other computers do not see it.
      : shown === "joinable" ? { ...machine("managed", "build-box", ["view", "drive", "start", "manage"]), serversToJoin: [{ linkId: "home", name: "Home" }] }
      : shown === "other-joinable" ? { ...machine("other", "mac-mini", ["view", "drive", "start"]), serversToJoin: [{ linkId: "home", name: "Home" }] }
      // A machine this computer manages through the relay of the server: it sees its providers and sign-ins, and gives none.
      : shown === "managed-relayed" ? { ...machine("managed", "build-box", ["view", "drive", "start", "manage"]), paired: false, channel: "relayed" }
      // A node of the owner's organization this computer reaches through the relay of the server: it has no pairing of its own.
      : shown === "relayed" ? { ...machine("relayed", "pc-ai", ["view", "drive", "start"]), paired: false, channel: "relayed" }
      : machine("other", "mac-mini", ["view", "drive", "start"])}
    onBack={() => calls.push(JSON.stringify({ back: shown }))} />;
}

createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => {} }}>
    <div className="ide-shell settings-dialog-content" style={{ padding: 12, height: "100%", boxSizing: "border-box", overflow: "auto" }}>
      <Fixture />
    </div>
  </ShellLanguageContext.Provider>
</StrictMode>);
