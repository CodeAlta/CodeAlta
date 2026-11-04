// Disposable page: Create my server over SSH (the first try does not answer from here, the second goes through), Join
// an organization, and the Fleet button with a device that waits. Driven by createServer.browser.test.ts through
// `serverFixture`: what the page asks is recorded.
import { StrictMode, useState } from "react";
import { createRoot } from "react-dom/client";
import type { FleetDeviceView, MachineSetupEvent } from "#neoastra";
import { ShellLanguageContext } from "../shellLanguage";
import { CreateServerDialog, type CreateServerApi, type CreateServerMachinesApi } from "./CreateServerDialog";
import { JoinOrganizationDialog, type JoinOrganizationApi, type JoinOrganizationSpacesApi } from "./JoinOrganizationDialog";
import { FleetApprovals, type FleetApprovalsApi } from "./FleetApprovals";

document.documentElement.classList.add("bp6-dark");

const calls: string[] = [];
const epoch = "12345678-1234-1234-1234-123456789abc";
const wait = (ms: number) => new Promise(resolve => setTimeout(resolve, ms));
const event = (step: string, state: string, detail: string | null = null, code: string | null = null): MachineSetupEvent =>
  ({ step, state, detail, code, machineId: null, machineName: null, words: null, recoveryWords: null });
// The words of the owner's recovery key, made with the server and given once with the end of the setup.
const recoveryWords = "acid acorn actor agent alarm album alien alpha amber anchor angel ankle apple apron arena arrow atlas attic autumn award bacon badge bagel baker "
  + "bamboo banjo barn basil basket beach beacon bear";
let tries = 0;
const api: CreateServerApi = {
  status: async () => ({ status: "ok", servers: [], deviceId: "me", words: null, personName: "Alex Example" }),
  createServer: async request => {
    calls.push(JSON.stringify({ ssh: request.target, address: request.address, port: request.port, ownCertificate: request.ownCertificate }));
    const first = ++tries === 1;
    return (async function* () {
      for (const [step, detail] of [["connect", "me@box"], ["check", "Linux x86_64 · .NET SDK 10.0.114"], ["install", "CodeAlta Server 4.0.0 · ~/.dotnet/tools"],
        ["service", "systemd --user unit, port 2586, every network of the machine"]]) {
        yield event(step, "running");
        await wait(20);
        yield event(step, "done", detail);
      }
      yield event("reach", "running", "http://box:2586");
      if (first) { yield event("reach", "failed", "http://box:2586 · port 2586", "server_blocked_ufw"); return; }
      yield event("reach", "done", "http://box:2586");
      yield event("claim", "running");
      yield event("claim", "done");
      yield { ...event("finished", "done"), machineId: "link-1", recoveryWords };
    })();
  },
};
const machinesApi: CreateServerMachinesApi = {
  list: async () => ({ status: "ok", hostEpoch: epoch, machines: [{ id: "mac", name: "mac-mini", state: "online", os: "macos", version: "4.0.0", address: "mac:2585",
    fingerprint: "ABCD EFGH", words: "a b c d e f", sessions: 0, lastSeen: null, problem: null, permissions: ["view", "drive", "start", "manage"], pairedAt: "2026-11-01T00:00:00Z",
    paired: true, channel: "direct" }] }),
  joinServer: async request => { calls.push(JSON.stringify({ join: request.machineId, link: request.linkId })); return { status: "ok", name: "mac-mini", words: "maple orbit tiger cloud amber stone", message: null }; },
};
// Set by the test once another computer approved the one that joins.
let joinApproved = false;
let opened = false;
const joinApi: JoinOrganizationApi = {
  join: async request => {
    calls.push(JSON.stringify({ joinOrg: request.url, token: request.token }));
    // A server without an owner refuses an enrollment code: what was typed is its setup code.
    if (request.token.startsWith("SETUP")) return { status: "enrollment.server_not_set_up", message: "This server has no owner yet: set it up with its setup code.", recoveryWords: null, server: null };
    return { status: "ok", message: null, recoveryWords: null, server: { id: "corp", url: request.url, organization: "Contoso", link: "pending", connection: "pending", problem: null, controller: false, spaces: [],
      recoveryKey: null, shareUrl: null, stop: null } };
  },
  status: async () => ({ status: "ok", servers: joinApproved ? [{ id: "corp", url: "https://fleet.contoso.com", organization: "Contoso", link: "approved", connection: "online", problem: null,
    controller: false, spaces: [], recoveryKey: null, shareUrl: null, stop: null } as never] : [], deviceId: "me", words: "cabin onion leopard pigeon peach needle", personName: null }),
  // A server whose address names SSO offers its company sign-in and a passkey, one that names GitHub offers GitHub; the others offer none, and take an enrollment code.
  signInMethods: async request => request.url.includes("everything")
    ? { status: "ok", organization: "Contoso", ssoRequired: false, message: null, methods: [{ id: "company", name: "Contoso", breakGlass: false }, { id: "passkey", name: "Passkey", breakGlass: false },
      { id: "microsoft", name: "Microsoft", breakGlass: false }, { id: "google", name: "Google", breakGlass: false }, { id: "github", name: "GitHub", breakGlass: false }] }
    : request.url.includes("sso")
    ? { status: "ok", organization: "Contoso", ssoRequired: false, message: null, methods: [{ id: "passkey", name: "Passkey", breakGlass: false }, { id: "company", name: "Contoso", breakGlass: false }] }
    : request.url.includes("github")
      ? { status: "ok", organization: "Contoso", ssoRequired: false, message: null, methods: [{ id: "github", name: "GitHub", breakGlass: false }] }
      : { status: "ok", organization: null, ssoRequired: false, message: null, methods: [] },
  // GitHub gives a code first, entered at GitHub; the join waits until the page of the code opened (the person entered it), or Cancel.
  startSignInCode: async request => {
    calls.push(JSON.stringify({ startCode: request.url, method: request.method }));
    return { status: "ok", message: null, code: { handle: "0123456789abcdef0123456789abcdef", userCode: "WDJB-MJHT", url: "https://github.com/login/device",
      expiresAt: "2026-11-04T12:15:00Z" } };
  },
  joinWithSignIn: async (request, options) => {
    calls.push(JSON.stringify({ joinSignIn: request.url, method: request.method, handle: request.handle }));
    if (request.method === "github") {
      while (!opened && !options?.signal?.aborted) await new Promise(resolve => setTimeout(resolve, 20));
      if (options?.signal?.aborted) {
        calls.push(JSON.stringify({ joinCancelled: request.method }));
        return { status: "sign_in.cancelled", person: null, hasDevices: false, message: null, server: null };
      }
    }
    return { status: "ok", person: "Sam", hasDevices: request.method === "github", message: null, server: { id: "corp", url: request.url, organization: "Contoso",
      link: "pending", connection: "pending", problem: null, controller: false, spaces: [], recoveryKey: null, shareUrl: null, stop: null } };
  },
  openSignInPage: async request => { calls.push(JSON.stringify({ openPage: request.handle })); opened = true; return { status: "ok", message: null }; },
  linkSpace: async request => { calls.push(JSON.stringify({ link: request.spaceId, server: request.linkId })); return { status: "ok", message: null, space: null } as never; },
};
const spacesApi: JoinOrganizationSpacesApi = {
  list: async () => ({ status: "ok", spaces: [{ id: "default", name: "Default" }, { id: "home", name: "Home" }] as never }),
  create: async request => { calls.push(JSON.stringify({ space: request.name })); return { status: "ok", message: null, movedSessions: 0, space: { id: "work-1", name: request.name } } as never; },
};
let devices: FleetDeviceView[] = [{ id: "box", name: "build-box", kind: "node", status: "pending", online: true, words: "ruby scarf apple ember camera finch", createdAt: "2026-11-03T00:00:00Z" }];
const approvalsApi: FleetApprovalsApi = {
  status: async () => ({ status: "ok", deviceId: "me", words: null, personName: null, servers: [{ id: "home", url: "https://fleet.example", organization: "Home", link: "approved", connection: "online",
    problem: null, controller: true, spaces: [], recoveryKey: true, shareUrl: null, stop: null }] }),
  devices: async () => ({ status: "ok", devices, message: null, skippedEntries: 0 }),
  approve: async request => { calls.push(JSON.stringify({ approve: request.deviceId, words: request.words, kind: request.kind })); devices = devices.map(item => ({ ...item, status: "approved" })); return { status: "ok", device: null, message: null }; },
  revoke: async request => { calls.push(JSON.stringify({ refuse: request.deviceId })); return { status: "ok", device: null, message: null }; },
  watch: async () => (async function* () { })(),
  budgets: async () => ({ status: "ok", message: null, budgets: [], holds: [], notifications: [], machines: [], spaces: [], projects: [], timeZone: "UTC" }),
  continueRun: async () => ({ status: "ok", message: null }),
  stopRun: async () => ({ status: "ok", message: null }),
  readNotification: async () => ({ status: "ok", message: null }),
  directory: async () => ({ status: "ok", machines: [], sessions: [] }) as never,
};

function Fixture() {
  const [shown, setShown] = useState("create");
  Object.assign(window, { serverFixture: { calls, show: setShown, approveJoin: () => { joinApproved = true; } } });
  return <>
    <FleetApprovals epoch={epoch} api={approvalsApi} onOpen={() => calls.push(JSON.stringify({ open: "fleetServer" }))} />
    {shown === "create" && <CreateServerDialog epoch={epoch} api={api} machinesApi={machinesApi} spacesApi={{ link: { linkSpace: joinApi.linkSpace }, list: spacesApi }} onExisting={() => calls.push(JSON.stringify({ existing: true }))}
      onClose={(created, words) => calls.push(JSON.stringify({ close: created, recoveryWords: words ?? null }))} />}
    {shown === "join" && <JoinOrganizationDialog epoch={epoch} api={joinApi} spaces={spacesApi} onSetUp={address => calls.push(JSON.stringify({ setUp: address }))} onClose={joined => calls.push(JSON.stringify({ closeJoin: joined }))} />}
    {shown === "joinOwn" && <JoinOrganizationDialog epoch={epoch} kind="own" api={joinApi} spaces={spacesApi} onSetUp={address => calls.push(JSON.stringify({ setUp: address }))} onClose={joined => calls.push(JSON.stringify({ closeJoin: joined }))} />}
  </>;
}

createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => {} }}>
    <div className="ide-shell" style={{ padding: 12 }}><Fixture /></div>
  </ShellLanguageContext.Provider>
</StrictMode>);