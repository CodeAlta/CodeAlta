// Disposable page: Settings > Fleet > Secrets and environments of the owner's computer at one server, with two machines, a
// secret a revoked machine held, a variable, an environment that requires the owner's approval and a run that waits for it.
// Driven by fleetSecretsSettings.browser.test.ts through `fleetSecretsFixture`: what the page asks is recorded.
import { StrictMode, useState } from "react";
import { createRoot } from "react-dom/client";
import type { FleetApprovalActionRequest, FleetSecretMachineRequest, FleetSecretRotateRequest, FleetSecretSetRequest, FleetSecretsOverview } from "#neoastra";
import { ShellLanguageContext } from "../shellLanguage";
import { FleetSecretsSettings, type FleetSecretsApi } from "./FleetSecretsSettings";

document.documentElement.classList.add("bp6-dark");

const calls: string[] = [];
const machine = (id: string, name: string) => ({ id, name, machine: true });
let overview: FleetSecretsOverview = {
  status: "ok", message: null,
  secrets: [
    { id: "s1", kind: "secret", name: "GRAFANA_SERVICE_ACCOUNT_TOKEN_PRODUCTION", scopeKind: "person", scopeId: null, scopeLabel: "Alex", version: 2, hostOnly: false,
      setBy: "Desktop", setAt: "2026-11-03T09:00:00Z", holders: [{ id: "desktop", name: "Desktop", machine: false }, machine("mac", "Mac mini")], flagged: [machine("old", "Old box")],
      value: null },
    { id: "s2", kind: "secret", name: "DEPLOY_TOKEN", scopeKind: "environment", scopeId: "env-prod", scopeLabel: "production", version: 1, hostOnly: true,
      setBy: "Desktop", setAt: "2026-11-03T10:00:00Z", holders: [machine("mac", "Mac mini"), machine("linux", "Linux box")], flagged: [], value: null },
    { id: "s3", kind: "variable", name: "GRAFANA_URL", scopeKind: "space", scopeId: "space-1", scopeLabel: "Work", version: 1, hostOnly: false,
      setBy: "Desktop", setAt: "2026-11-03T11:00:00Z", holders: [machine("mac", "Mac mini")], flagged: [], value: "https://grafana.example.com" },
  ],
  environments: [{ id: "env-prod", name: "production", version: 1, approvers: ["p1"], approverNames: ["Alex"], labels: ["prod"], automations: [], verified: true }],
  approvals: [{ id: "a1", environment: "production", machine: "Mac mini", runId: "run-1", title: "Deploy the API", status: "pending", requestedAt: "2026-11-03T12:00:00Z",
    mayApprove: true, review: "review-a1" }],
  machines: [{ id: "mac", name: "Mac mini" }, { id: "linux", name: "Linux box" }],
  people: [{ id: "p1", name: "Alex" }, { id: "p2", name: "Sam" }],
  spaces: [{ id: "work", name: "Work" }],
  projects: [{ id: "api", name: "API" }],
};
const ok = async () => ({ status: "ok", message: null });
const api: FleetSecretsApi = {
  status: async () => ({ status: "ok", servers: [{ id: "home", url: "https://fleet.example", organization: "Home", link: "approved", connection: "online", problem: null,
    controller: true, spaces: [], recoveryKey: true, shareUrl: null, stop: null }], deviceId: "desktop", words: "apple river stone", personName: null }),
  secrets: async () => overview,
  setSecret: async (request: FleetSecretSetRequest) => { calls.push(JSON.stringify({ set: { ...request, expectedEpoch: undefined, linkId: undefined } })); return ok(); },
  rotateSecret: async (request: FleetSecretRotateRequest) => { calls.push(JSON.stringify({ rotate: request.id, value: request.value })); return ok(); },
  deleteSecret: ok,
  grantSecret: async (request: FleetSecretMachineRequest) => { calls.push(JSON.stringify({ give: request.id, machine: request.machineId })); return ok(); },
  withdrawSecret: async (request: FleetSecretMachineRequest) => { calls.push(JSON.stringify({ withdraw: request.id, machine: request.machineId })); return ok(); },
  setEnvironment: ok,
  approveRun: async (request: FleetApprovalActionRequest) => {
    calls.push(JSON.stringify({ approve: request.approvalId }));
    overview = { ...overview, approvals: overview.approvals.map(approval => approval.id === request.approvalId ? { ...approval, status: "approved", mayApprove: false } : approval) };
    return ok();
  },
};
// The width of the page of the Settings window: about 1250 CSS pixels in a window of 1600, about 430 in one of 900.
function Fixture() {
  const [width, setWidth] = useState(1250);
  Object.assign(window, { fleetSecretsFixture: { setWidth, calls } });
  return <div className="ide-shell settings-dialog-content" style={{ padding: 12, width, boxSizing: "border-box" }}>
    <FleetSecretsSettings epoch="12345678-1234-1234-1234-123456789abc" api={api} />
  </div>;
}

createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => {} }}>
    <Fixture />
  </ShellLanguageContext.Provider>
</StrictMode>);