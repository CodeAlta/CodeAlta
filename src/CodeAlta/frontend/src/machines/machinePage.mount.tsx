// Disposable page: the page of a paired machine this computer manages, then of one it does not. Driven by
// machinePage.browser.test.ts through `machineFixture`: what the page asks is recorded.
import { StrictMode, useState } from "react";
import { createRoot } from "react-dom/client";
import type { MachineDeviceView, MachineManagement, MachineProviderStatusView, MachineView, SecretView, VariableView } from "#neoastra";
import { ShellLanguageContext } from "../shellLanguage";
import { MachinePage, type MachinePageApi } from "./MachinePage";

document.documentElement.classList.add("bp6-dark");

const calls: string[] = [];
const machine = (id: string, name: string, permissions: string[]): MachineView => ({ id, name, state: "online", os: "linux", version: "4.0.0", address: `${name}:2585`,
  fingerprint: "ABCD EFGH", words: "apple river stone cloud amber tiger", sessions: 1, lastSeen: null, problem: null, permissions, pairedAt: "2026-11-01T00:00:00Z",
  paired: true, channel: "direct" });
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
const view = (id: string): MachineManagement => id === "managed"
  ? { status: "ok", message: null, version: "4.0.0", sessionsAtWork: 0, updating: false, update: { running: "4.0.0", installed: "4.0.0", pendingVersion: null, pendingPhase: null,
    lastVersion: null, lastOutcome: null }, devices, projects, problems: [], providers: [{ key: "codex", name: "Codex", isDefault: true, defaultModel: "GPT-6.1", models: 3 }],
    thisDevice: "me", worktrees: { count: 3, kilobytes: 2.5 * 1024 * 1024, idleDays: 7, measuredAt: "2026-11-06T10:00:00Z" },
    sandbox: { profile: null, available: false, backend: "linux-bubblewrap", reason: "bubblewrap (`bwrap` 0.5 or later) is not installed or cannot create user namespaces.",
      setup: "sudo apt install bubblewrap slirp4netns" } }
  : { status: "not_manager", message: null, version: null, sessionsAtWork: 0, updating: false, update: null, devices: [], projects: [], problems: [], providers: [], thisDevice: "me", worktrees: null, sandbox: null };
const api: MachinePageApi = {
  management: async request => view(request.machineId),
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
  secrets: async () => ({ status: "ok", protection: "libsecret", secrets: [...secrets.values()].map(secret => ({ name: secret.name, version: secret.version, setBy: secret.setBy, setAt: secret.setAt })), variables: [...variables.values()] }),
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
  labels: async () => ({ status: "ok", message: null, labels: [...labels] }),
  setLabels: async request => { calls.push(JSON.stringify({ labels: request.labels })); labels = [...request.labels]; return { status: "ok", message: null }; },
};
let labels: string[] = ["gpu"];
let signInWaits = 0;
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
  secrets.set(name, { name, value, version: (secrets.get(name)?.version ?? 0) + 1, setBy: "desktop (manage)", setAt: "2026-11-03T09:00:00Z" });
}

function Fixture() {
  const [shown, setShown] = useState("managed");
  Object.assign(window, { machineFixture: { calls, show: setShown } });
  return <MachinePage key={shown} epoch="12345678-1234-1234-1234-123456789abc" api={api}
    machine={shown === "managed" ? machine("managed", "build-box", ["view", "drive", "start", "manage"])
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
