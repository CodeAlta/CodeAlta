// Disposable page: Add a machine… with a scripted host. Over SSH, the first try meets a host key ssh does not know and
// the second goes through; through the server, a node the server planted comes before the machine, and only the one
// whose words the person confirms is approved. Driven by
// addMachine.browser.test.ts through `addMachineFixture`: what the dialog asks is recorded.
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import type { FleetDeviceView, MachineSetupEvent } from "#neoastra";
import { ShellLanguageContext } from "../shellLanguage";
import { AddMachineDialog, type AddMachineApi, type AddMachineServerApi } from "./AddMachineDialog";

document.documentElement.classList.add("bp6-dark");

const calls: string[] = [];
const event = (step: string, state: string, detail: string | null = null, code: string | null = null): MachineSetupEvent =>
  ({ step, state, detail, code, machineId: null, machineName: null, words: null, recoveryWords: null });
const wait = (ms: number) => new Promise(resolve => setTimeout(resolve, ms));
// ssh records the key the person trusted: the next connections know it.
let trusted = false;
const api: AddMachineApi = {
  addOverSsh: async request => {
    calls.push(JSON.stringify({ ssh: request.target, trust: request.trustedHostKey, port: request.port, alwaysOn: request.alwaysOn, sudo: request.sudoPassword }));
    return (async function* () {
      yield event("connect", "running", request.target);
      await wait(50);
      if (request.trustedHostKey === "SHA256:Qm9vLWtleS1maW5nZXJwcmludA (ED25519)") trusted = true;
      if (!trusted) { yield event("connect", "failed", "SHA256:Qm9vLWtleS1maW5nZXJwcmludA (ED25519)", "host_key_unknown"); return; }
      yield event("connect", "done", "me@pc-ai");
      // Always on: sudo asks a password there, which the check asks the person once.
      if (request.alwaysOn && !request.sudoPassword) { yield event("check", "failed", "sudo asks the password of me there.", "sudo_password_needed"); return; }
      for (const [step, detail] of [["check", "Linux x86_64 · .NET SDK 10.0.114"], ["install", "CodeAlta Node 4.0.0 · ~/.dotnet/tools"],
        ["service", "systemd --user unit, port 2585"], ["pair", "apple river stone cloud amber tiger"]]) {
        yield event(step, "running");
        await wait(30);
        yield event(step, "done", detail);
      }
      yield { ...event("finished", "done"), machineId: "pc-ai-id", machineName: "pc-ai", words: "apple river stone cloud amber tiger" };
    })();
  },
};
let devices: FleetDeviceView[] = [{ id: "me", name: "desktop", kind: "controller", status: "approved", online: true, words: "a b c d e f", createdAt: "2026-11-01T00:00:00Z" }];
let polls = 0;
const serverApi: AddMachineServerApi = {
  status: async () => ({ status: "ok", deviceId: "me", words: null, personName: null, servers: [{ id: "home", url: "https://fleet.example", organization: "Home", link: "approved",
    connection: "online", problem: null, controller: true, spaces: [], recoveryKey: true, shareUrl: null, stop: null }] }),
  createToken: async () => { calls.push(JSON.stringify({ token: "home" })); return { status: "ok", token: "JOIN-1234", expiresAt: null, message: null }; },
  devices: async () => {
    // A node of the server's own making comes first, at the second read after the code; the machine the person added comes after it.
    if (++polls === 3) devices = [...devices, { id: "planted", name: "build-box", kind: "node", status: "pending", online: true, words: "acid acorn actor agent alarm album",
      createdAt: "2026-11-03T00:00:00Z" }];
    if (polls === 5) devices = [...devices, { id: "box", name: "build-box", kind: "node", status: "pending", online: true, words: "maple orbit tiger cloud amber stone",
      createdAt: "2026-11-03T00:00:01Z" }];
    return { status: "ok", devices, message: null, skippedEntries: 0 };
  },
  approve: async request => {
    calls.push(JSON.stringify({ approve: request.deviceId, kind: request.kind, words: request.words }));
    devices = devices.map(device => device.id === request.deviceId ? { ...device, status: "approved" } : device);
    return { status: "ok", device: devices.find(device => device.id === request.deviceId) ?? null, message: null };
  },
};
Object.assign(window, { addMachineFixture: { calls } });

createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => {} }}>
    <AddMachineDialog epoch="12345678-1234-1234-1234-123456789abc" api={api} serverApi={serverApi} onPairLink={() => calls.push(JSON.stringify({ link: true }))}
      onClose={added => calls.push(JSON.stringify({ close: added }))} onOpen={machineId => calls.push(JSON.stringify({ open: machineId }))} />
  </ShellLanguageContext.Provider>
</StrictMode>);