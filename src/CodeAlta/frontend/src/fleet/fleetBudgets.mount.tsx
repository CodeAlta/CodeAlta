// Disposable page: Settings > Fleet > Budgets of the owner's computer at one server, with an enforced budget of the person that is
// reached, an advisory one of a machine at 85%, a run held by the first, a notification of a machine low on disk; and the Fleet
// button with its toasts. Driven by fleetBudgets.browser.test.ts through `fleetBudgetsFixture`: what the page asks is recorded.
import { StrictMode, useState } from "react";
import { createRoot } from "react-dom/client";
import type { FleetBudgetSetRequest, FleetBudgetsOverview, FleetSecretRefRequest } from "#neoastra";
import { ShellLanguageContext } from "../shellLanguage";
import { FleetBudgetsSettings, type FleetBudgetsApi } from "./FleetBudgetsSettings";
import { translate } from "../localization";
import { FleetApprovals, showHeldStartToast, type FleetApprovalsApi } from "./FleetApprovals";

document.documentElement.classList.add("bp6-dark");

const calls: string[] = [];
let overview: FleetBudgetsOverview = {
  status: "ok", message: null, timeZone: "Europe/Paris",
  budgets: [
    { id: "b1", name: "Home tokens", scopeKind: "person", scopeId: null, scopeLabel: "", measure: "tokens", limit: 2_000_000, used: 2_000_000, granted: 2_000_000,
      period: "month", mode: "enforced", reached: "hold", state: "reached", periodEnd: "2026-12-01T00:00:00Z", version: 3 },
    { id: "b2", name: "Build box spending of the week", scopeKind: "machine", scopeId: "box", scopeLabel: "Build box", measure: "cost", limit: 40, used: 34, granted: 0,
      period: "week", mode: "advisory", reached: "stop", state: "warning", periodEnd: "2026-11-09T00:00:00Z", version: 1 },
  ],
  holds: [{ id: "h1", budgetId: "b1", budgetName: "Home tokens", machine: "Mac mini", runId: "run-1", title: "Nightly report", status: "waiting",
    requestedAt: "2026-11-04T09:00:00Z" }],
  notifications: [{ id: "n1", kind: "health", severity: "error", budget: null, machine: "Build box", problem: "disk_low", subject: null, detail: "900",
    createdAt: "2026-11-04T08:00:00Z", resolved: false, read: false, machineId: "box" }],
  machines: [{ id: "mac", name: "Mac mini" }, { id: "box", name: "Build box" }], spaces: [{ id: "work", name: "Work" }], projects: [{ id: "api", name: "API" }],
};
const ok = async () => ({ status: "ok", message: null });
const status = async () => ({ status: "ok", servers: [{ id: "home", url: "https://fleet.example", organization: "Home", link: "approved", connection: "online", problem: null,
  controller: true, spaces: [], recoveryKey: true, shareUrl: null, stop: null }], deviceId: "desktop", words: "apple river stone", personName: null });
const continueRun = async (request: FleetSecretRefRequest) => {
  calls.push(JSON.stringify({ continue: request.id }));
  overview = { ...overview, holds: overview.holds.map(hold => hold.id === request.id ? { ...hold, status: "continued" } : hold) };
  return ok();
};
const api: FleetBudgetsApi = {
  status,
  budgets: async () => overview,
  setBudget: async (request: FleetBudgetSetRequest) => { calls.push(JSON.stringify({ set: { ...request, expectedEpoch: undefined, linkId: undefined } })); return ok(); },
  removeBudget: async (request: FleetSecretRefRequest) => { calls.push(JSON.stringify({ remove: request.id })); return ok(); },
  continueRun,
  stopRun: async (request: FleetSecretRefRequest) => { calls.push(JSON.stringify({ stop: request.id })); return ok(); },
  readNotification: async (request: FleetSecretRefRequest) => {
    calls.push(JSON.stringify({ read: request.id }));
    overview = { ...overview, notifications: overview.notifications.map(notification => notification.id === request.id ? { ...notification, read: true } : notification) };
    return ok();
  },
};
// The directory of the server: the sessions of the machines, one of which a test makes wait for a command to allow.
let waitingRun = false;
const directory = async () => ({ status: "ok", linkId: "home", url: "https://fleet.example", organization: "Home", connection: "online",
  machines: [{ id: "desktop", name: "This PC", isThisComputer: true }, { id: "mac", name: "Mac mini", isThisComputer: false }],
  sessions: [{ id: "s-local", machineId: "desktop", state: "requires_action" }, ...(waitingRun ? [{ id: "s-mac", machineId: "mac", state: "requires_action" }] : [])] }) as never;
const buttonApi: FleetApprovalsApi = {
  status, devices: async () => ({ status: "ok", devices: [], message: null, skippedEntries: 0 }), approve: ok as never, revoke: ok as never, watch: async () => (async function* () { })(),
  budgets: api.budgets, continueRun: api.continueRun, stopRun: api.stopRun, readNotification: api.readNotification, directory,
};
// The width of the page of the Settings window: about 1250 CSS pixels in a window of 1600, about 430 in one of 900.
function Fixture() {
  const [width, setWidth] = useState(1250);
  const [shown, setShown] = useState(false);
  // hideButton then showButton is a start of the window again: a new Fleet button that reads the server anew.
  Object.assign(window, { fleetBudgetsFixture: { setWidth, calls, showButton: () => setShown(true), hideButton: () => setShown(false), waitRun: () => { waitingRun = true; },
    // What the page says when a session started on the Mac mini has its first prompt held by Home tokens (the run of the hold is the session).
    heldStart: () => showHeldStartToast({ epoch: "12345678-1234-1234-1234-123456789abc", runId: "run-1", api: buttonApi, say: (key, values) => translate("en", key, values),
      message: translate("en", "The session was started on {name}. Its prompt waits for a reached budget of the machine: it is sent once you continue it anyway.", { name: "Mac mini" }) }) } });
  return <div className="ide-shell settings-dialog-content" style={{ padding: 12, width, boxSizing: "border-box" }}>
    {shown && <FleetApprovals epoch="12345678-1234-1234-1234-123456789abc" api={buttonApi} onOpen={() => calls.push(JSON.stringify({ open: "fleetBudgets" }))} />}
    <FleetBudgetsSettings epoch="12345678-1234-1234-1234-123456789abc" api={api} />
  </div>;
}

createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => {} }}>
    <Fixture />
  </ShellLanguageContext.Provider>
</StrictMode>);