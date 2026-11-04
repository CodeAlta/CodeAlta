// Disposable page: Settings > Fleet > Policies of the owner's computer at one server, and the stop of the title bar. The policy
// starts with two rules and no stop; the fixture records what the page signs in `calls` and answers it as a server would:
// a stop signed shows as a stop, a lift as none. `fleetPoliciesFixture.member()` shows the page of a person who does not sign.
// Driven by fleetPolicies.browser.test.ts.
import { StrictMode, useState } from "react";
import { createRoot } from "react-dom/client";
import type { FleetPolicyOverview, FleetPolicyRulesView, FleetPolicyStopView, FleetServerView } from "#neoastra";
import { ShellLanguageContext } from "../../shellLanguage";
import { FleetPoliciesSettings, type FleetPoliciesApi } from "./FleetPoliciesSettings";
import { FleetStopIndicator } from "./FleetStopIndicator";

const dark = new URLSearchParams(location.search).get("theme") !== "light";
document.documentElement.classList.toggle("bp6-dark", dark);
document.body.classList.toggle("bp6-dark", dark);

const calls: string[] = [];
const machines = [{ id: "box", name: "Build box", team: "Infra" }, { id: "mini", name: "Mac mini", team: null }, { id: "gpu", name: "GPU tower", team: "Infra" }];
let rules: FleetPolicyRulesView = { providers: ["anthropic", "openai"], models: null, sandbox: "workspace", toolsOff: null, network: null, remoteStarts: null, agentStarts: false,
  maxSessionMinutes: null, permissionMode: null, publishFacts: null, accountCredentials: null };
let stop: FleetPolicyStopView | null = null;
let number = 2;
let canSign = true;
// Draws the page again once the fixture signed, as a status read would.
let bump = () => {};
const names = (view: FleetPolicyStopView) => [...view.machines.map(id => machines.find(machine => machine.id === id)?.name ?? id), ...view.teams.map(id => id === "infra" ? "Infra" : id)];
const describe = (next: FleetPolicyRulesView | null, nextStop: FleetPolicyStopView | null) => {
  const lines: string[] = [];
  if (JSON.stringify(next) !== JSON.stringify(rules)) lines.push("The rules change");
  if (nextStop && !stop) lines.push(nextStop.scope === "fleet" ? "STOP: the work of the fleet stops on every machine" : `STOP: the work of the fleet stops on ${names(nextStop).join(", ")}`);
  if (!nextStop && stop) lines.push("The work of the fleet goes on: the stop is lifted");
  return lines;
};
const overview = (): FleetPolicyOverview => ({
  status: "ok", message: null, canSign, number, signedBy: "Alex", signedAt: "2026-11-04T13:02:00Z", rules, stop: stop ? { ...stop, names: names(stop) } : null, machines,
  teams: [{ id: "infra", name: "Infra" }],
  versions: [
    { number: 2, signedBy: "Alex", signedAt: "2026-11-03T09:00:00Z", changes: ["Agents start no work on other machines"], stops: false },
    { number: 1, signedBy: "Alex", signedAt: "2026-11-02T09:00:00Z", changes: ["Only these kinds of providers: anthropic, openai", "The work of the fleet runs in the sandbox 'workspace' at least"], stops: false },
  ],
});
const server = (): FleetServerView => ({ id: "home", url: "https://fleet.example", organization: "Example", link: "approved", connection: "online", problem: null, controller: true,
  spaces: [], recoveryKey: true, stop: stop ? { scope: stop.scope, reason: stop.reason, machines: stop.machines.length, teams: stop.teams.length, since: "2026-11-04T14:02:00Z", number } : null });

const api: FleetPoliciesApi = {
  status: async () => ({ status: "ok", servers: [server()], deviceId: "desktop", words: "apple river stone", personName: null }) as never,
  policy: async () => overview(),
  preparePolicy: async request => ({ status: "ok", message: null, changes: describe(request.rules, request.stop) }),
  signPolicy: async request => {
    calls.push(JSON.stringify({ sign: { rules: request.rules, stop: request.stop, changes: request.changes } }));
    if (JSON.stringify(request.changes) !== JSON.stringify(describe(request.rules, request.stop))) return { status: "changed", message: null };
    rules = request.rules ?? rules;
    stop = request.stop;
    number++;
    bump();
    return { status: "ok", message: null };
  },
};

// The width of the page of the Settings window: about 1250 CSS pixels in a window of 1600, about 430 in one of 900.
function Fixture() {
  const [width, setWidth] = useState(1250);
  const [key, setKey] = useState(0);
  const [, setTick] = useState(0);
  bump = () => setTick(value => value + 1);
  Object.assign(window, { fleetPoliciesFixture: { setWidth, calls, member: () => { canSign = false; setKey(value => value + 1); } } });
  return <div className="ide-shell settings-dialog-content" style={{ padding: 12, width, boxSizing: "border-box" }}>
    <div className="window-actions fleet-fixture-titlebar" style={{ display: "flex", justifyContent: "flex-end", minHeight: 30 }}>
      <FleetStopIndicator key={`indicator-${key}-${number}`} epoch="12345678-1234-1234-1234-123456789abc" api={api} onOpen={() => calls.push(JSON.stringify({ open: "fleetPolicies" }))} />
    </div>
    <FleetPoliciesSettings key={key} epoch="12345678-1234-1234-1234-123456789abc" api={api} />
  </div>;
}

createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => {} }}>
    <Fixture />
  </ShellLanguageContext.Provider>
</StrictMode>);
