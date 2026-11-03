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
  ({ step, state, detail, code, machineId: null, machineName: null, words: null });
let tries = 0;
const api: CreateServerApi = {
  createServer: async request => {
    calls.push(JSON.stringify({ ssh: request.target, address: request.address, port: request.port }));
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
      yield { ...event("finished", "done"), machineId: "link-1" };
    })();
  },
};
const machinesApi: CreateServerMachinesApi = {
  list: async () => ({ status: "ok", hostEpoch: epoch, machines: [{ id: "mac", name: "mac-mini", state: "online", os: "macos", version: "4.0.0", address: "mac:2585",
    fingerprint: "ABCD EFGH", words: "a b c d e f", sessions: 0, lastSeen: null, problem: null, permissions: ["view", "drive", "start", "manage"], pairedAt: "2026-11-01T00:00:00Z",
    paired: true, channel: "direct" }] }),
  joinServer: async request => { calls.push(JSON.stringify({ join: request.machineId, link: request.linkId })); return { status: "ok", name: "mac-mini", words: "maple orbit tiger cloud amber stone", message: null }; },
};
const joinApi: JoinOrganizationApi = {
  join: async request => {
    calls.push(JSON.stringify({ joinOrg: request.url, token: request.token }));
    return { status: "ok", message: null, recoveryWords: null, server: { id: "corp", url: request.url, organization: "Contoso", link: "pending", connection: "pending", problem: null, controller: false, spaces: [],
      recoveryKey: null } };
  },
  status: async () => ({ status: "ok", servers: [], deviceId: "me", words: "cabin onion leopard pigeon peach needle" }),
  linkSpace: async request => { calls.push(JSON.stringify({ link: request.spaceId, server: request.linkId })); return { status: "ok", message: null, space: null } as never; },
};
const spacesApi: JoinOrganizationSpacesApi = {
  list: async () => ({ status: "ok", spaces: [] }),
  create: async request => { calls.push(JSON.stringify({ space: request.name })); return { status: "ok", message: null, movedSessions: 0, space: { id: "work-1", name: request.name } } as never; },
};
let devices: FleetDeviceView[] = [{ id: "box", name: "build-box", kind: "node", status: "pending", online: true, words: "ruby scarf apple ember camera finch", createdAt: "2026-11-03T00:00:00Z" }];
const approvalsApi: FleetApprovalsApi = {
  status: async () => ({ status: "ok", deviceId: "me", words: null, servers: [{ id: "home", url: "https://fleet.example", organization: "Home", link: "approved", connection: "online",
    problem: null, controller: true, spaces: [], recoveryKey: true }] }),
  devices: async () => ({ status: "ok", devices, message: null }),
  approve: async request => { calls.push(JSON.stringify({ approve: request.deviceId, words: request.words })); devices = devices.map(item => ({ ...item, status: "approved" })); return { status: "ok", device: null, message: null }; },
  revoke: async request => { calls.push(JSON.stringify({ refuse: request.deviceId })); return { status: "ok", device: null, message: null }; },
  watch: async () => (async function* () { })(),
};

function Fixture() {
  const [shown, setShown] = useState("create");
  Object.assign(window, { serverFixture: { calls, show: setShown } });
  return <>
    <FleetApprovals epoch={epoch} api={approvalsApi} onOpen={() => calls.push(JSON.stringify({ open: "fleetServer" }))} />
    {shown === "create" && <CreateServerDialog epoch={epoch} api={api} machinesApi={machinesApi} onExisting={() => calls.push(JSON.stringify({ existing: true }))}
      onClose={created => calls.push(JSON.stringify({ close: created }))} />}
    {shown === "join" && <JoinOrganizationDialog epoch={epoch} api={joinApi} spaces={spacesApi} onClose={joined => calls.push(JSON.stringify({ closeJoin: joined }))} />}
  </>;
}

createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => {} }}>
    <div className="ide-shell" style={{ padding: 12 }}><Fixture /></div>
  </ShellLanguageContext.Provider>
</StrictMode>);
