// Disposable page: the Fleet tab of Settings in its window, with the Overview of a fleet that runs, of one that starts, and the
// table of its machines. Driven by fleetConsole.browser.test.ts through `fleetConsoleFixture`; its pictures are those of the
// guide (a fleet of the size of an organization, which no computer of a developer has).
import { StrictMode, useState } from "react";
import { createRoot } from "react-dom/client";
import { InputGroup } from "@blueprintjs/core";
import type { FleetServerView, MachineView } from "#neoastra";
import { AppIcon, type IconName } from "../../AppIcon";
import { AppWindow } from "../../AppWindow";
import { ShellLanguageContext } from "../../shellLanguage";
import type { Space } from "../../spaces/spaces";
import { MachinesSettings } from "../../machines/MachinesSettings";
import { FleetOverview, type FleetOverviewApi } from "./FleetOverview";

document.documentElement.classList.add("bp6-dark");

const epoch = "12345678-1234-1234-1234-123456789abc";
const day = 24 * 3600_000, now = Date.parse("2026-11-07T15:00:00Z");
const ago = (hours: number) => new Date(now - hours * 3600_000).toISOString();
let serial = 0;
const machine = (name: string, state: string, os: string | null, version: string | null, sessions: number, more: Partial<MachineView> = {}): MachineView => ({
  id: `m${++serial}`, name, state, os, version, address: `${name}.corp.example:2585`, fingerprint: "K3QF 8WZD", words: "harbor maple quartz ember tiger lotus", sessions,
  lastSeen: state === "online" ? ago(0.02) : ago(30 + serial * 3), problem: state === "offline" ? "No address of the machine answered." : state === "unauthorized" ? "The machine refused this computer: its owner revoked it." : null,
  permissions: ["view", "drive", "start", ...(serial % 3 === 0 ? [] : ["manage"])], pairedAt: new Date(now - (40 - serial) * day).toISOString(), paired: serial % 4 !== 0,
  channel: state === "online" ? serial % 5 === 0 ? "relayed" : "direct" : null, ...more,
});
const fleet: MachineView[] = [
  machine("build-01", "online", "Ubuntu 24.04", "4.1.0", 6), machine("build-02", "online", "Ubuntu 24.04", "4.1.0", 4), machine("build-03", "online", "Ubuntu 24.04", "4.1.0", 5),
  machine("build-04", "connecting", "Ubuntu 24.04", "4.1.0", 0), machine("gpu-a100-1", "online", "Ubuntu 22.04", "4.1.0", 3), machine("gpu-a100-2", "online", "Ubuntu 22.04", "4.0.2", 2),
  machine("mac-mini-ci", "online", "macOS 26.1", "4.1.0", 4), machine("mac-studio", "online", "macOS 26.1", "4.1.0", 2), machine("mac-mini-qa", "offline", "macOS 26.0", "4.0.2", 0),
  machine("win-build-1", "online", "Windows 11", "4.1.0", 3), machine("win-build-2", "online", "Windows 11", "4.1.0", 1), machine("win-legacy", "unauthorized", "Windows 10", "4.0.0", 0),
  machine("docs-renderer", "online", "Debian 13", "4.1.0", 1), machine("release-box", "online", "Ubuntu 24.04", "4.1.0", 2), machine("perf-lab", "offline", "Ubuntu 24.04", "4.0.2", 0),
  machine("arm-runner-1", "online", "Ubuntu 24.04", "4.1.0", 2), machine("arm-runner-2", "online", "Ubuntu 24.04", "4.1.0", 1), machine("sandbox-01", "online", "Fedora 43", "4.1.0", 0),
  machine("eval-cluster-1", "online", "Ubuntu 24.04", "4.1.0", 7), machine("eval-cluster-2", "online", "Ubuntu 24.04", "4.1.0", 6), machine("design-mac", "online", "macOS 26.1", "4.1.0", 1),
  machine("home-nas", "offline", "Debian 12", "4.0.1", 0),
];
const server: FleetServerView = { id: "s1", url: "https://fleet.northwind.example", organization: "Northwind Labs", link: "approved", connection: "online", problem: null,
  controller: true, spaces: ["work", "open-source"], recoveryKey: true, shareUrl: null, stop: null };
const space = (id: string, name: string, icon: string | null, color: string | null): Space => ({ id, name, description: null, icon, color, isDefault: id === "default", projectIds: [],
  file: null, agentPrompt: null, disabledTools: [], ownTiers: false, identityEmail: null, identityParts: [] });
const spaces = [space("default", "Default", null, null), space("work", "Work", "briefcase", "#4c90f0"), space("open-source", "Open source", "github", "#9881f3"),
  space("personal", "Personal", "home", "#32a467")];

const state = { machines: fleet, servers: [server] as FleetServerView[], failing: false };
const failing = () => { if (state.failing) throw new Error("The bridge did not answer."); };
const never = async (_request: unknown, options?: { signal?: AbortSignal }) => (async function* () {
  await new Promise(resolve => options?.signal?.addEventListener("abort", resolve));
})();
const machinesApi = {
  list: async () => { failing(); return { status: "ok", hostEpoch: epoch, machines: state.machines }; },
  watch: never,
  previewPairing: async () => ({ status: "invalid" }),
  pair: async () => ({ status: "invalid" }),
  forget: async () => ({ status: "ok" }),
  moveSettings: async () => ({ status: "ok", receiveMoves: false }),
  setReceiveMoves: async () => ({ status: "ok", receiveMoves: false }),
  // What this computer lets the others do is not the subject of these pictures.
  thisComputer: async () => ({ status: "unavailable" }),
} as unknown as FleetOverviewApi["machines"] & Parameters<typeof MachinesSettings>[0]["api"];
const api: FleetOverviewApi = { machines: machinesApi, servers: { status: async () => { failing(); return { status: "ok", servers: state.servers, deviceId: "me", words: "a b c d e f" }; }, watch: never } as unknown as FleetOverviewApi["servers"] };

const pages: readonly Readonly<{ id: string; label: string; icon: IconName }>[] = [
  { id: "fleet", label: "Overview", icon: "fleet" }, { id: "machines", label: "Machines", icon: "remote" }, { id: "fleetServer", label: "Server", icon: "server" },
  { id: "fleetSecrets", label: "Secrets and environments", icon: "secret" }, { id: "fleetBudgets", label: "Budgets", icon: "usage" },
  { id: "fleetAccess", label: "People and access", icon: "user" }, { id: "fleetAudit", label: "Audit", icon: "logs" }, { id: "fleetPolicies", label: "Policies", icon: "shield" },
];

/** The window of Settings on its Fleet tab, as the shell draws it (`SettingsOverlay` in main.tsx). */
function Frame() {
  const [page, setPage] = useState("fleet");
  const [generation, setGeneration] = useState(0);
  Object.assign(window, { fleetConsoleFixture: {
    show: (next: string) => setPage(next),
    start: () => { state.machines = []; state.servers = []; setGeneration(value => value + 1); },
    run: () => { state.machines = fleet; state.servers = [server]; state.failing = false; setGeneration(value => value + 1); },
    fail: () => { state.failing = true; setGeneration(value => value + 1); },
    // A fleet of the size of a large organization, for the pages of its tables.
    many: (count: number) => {
      state.machines = Array.from({ length: count }, (_, index) => machine(`node-${String(index + 1).padStart(3, "0")}`, index % 9 === 0 ? "offline" : "online", "Ubuntu 24.04", "4.1.0", index % 5));
      setGeneration(value => value + 1);
    },
  } });
  return <AppWindow storageKey="fixture.settings" className="settings-dialog" titleId="settings-title" title="Settings" closeLabel="Close settings" onClose={() => { }}
    preferredSize={viewport => ({ width: viewport.width * 0.8, height: viewport.height * 0.8 })} minimumSize={{ width: 560, height: 360 }}>
    <div className="settings-dialog-body">
      <nav className="settings-dialog-navigation" aria-label="Settings pages" data-fleet>
        <div className="settings-mode" role="group" aria-label="Settings">
          <button type="button" aria-pressed={false} data-settings-mode="general"><AppIcon name="settings" size={15} /><span>General</span></button>
          <button type="button" aria-pressed data-settings-mode="fleet"><AppIcon name="fleet" size={15} /><span>Fleet</span></button>
        </div>
        <div className="settings-search-bar"><InputGroup className="settings-search-field" type="search" size="small" leftIcon={<AppIcon name="search" size={14} className="bp6-icon" />}
          placeholder="Search settings" aria-label="Search settings" /></div>
        <div className="settings-dialog-group"><h3>Fleet</h3>
          {pages.map(entry => <button key={entry.id} type="button" aria-current={entry.id === page ? "page" : undefined} data-settings-section={entry.id}
            onClick={() => setPage(entry.id)}><AppIcon name={entry.icon} size={16} /><span>{entry.label}</span></button>)}
        </div>
      </nav>
      <div className="settings-dialog-content" key={`${page}:${generation}`}>
        {page === "machines" ? <MachinesSettings epoch={epoch} api={machinesApi} />
          : <FleetOverview epoch={epoch} spaces={spaces} api={api} />}
      </div>
    </div>
  </AppWindow>;
}

createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => {} }}>
    <div className="ide-shell" style={{ height: "100%" }}><Frame /></div>
  </ShellLanguageContext.Provider>
</StrictMode>);
