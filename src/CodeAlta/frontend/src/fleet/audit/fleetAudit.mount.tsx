// Disposable page: Settings > Fleet > Audit of the owner's computer at one server, with a log of a week of a small organization.
// Driven by fleetAudit.browser.test.ts: what the page asks is recorded in `calls`; `fleetAuditFixture.member()` shows the page
// of a person who may not read the log, `broken()` a chain that breaks.
import { StrictMode, useState } from "react";
import { createRoot } from "react-dom/client";
import type { FleetAccessOverview, FleetAuditItem, FleetAuditPage } from "#neoastra";
import { ShellLanguageContext } from "../../shellLanguage";
import { FleetAuditSettings, type FleetAuditApi } from "./FleetAuditSettings";

const dark = new URLSearchParams(location.search).get("theme") !== "light";
document.documentElement.classList.toggle("bp6-dark", dark);
document.body.classList.toggle("bp6-dark", dark);

const now = new Date(2026, 10, 4, 15, 30, 0);
const calls: string[] = [];
const at = (days: number, hours: number, minutes: number) => new Date(now.getFullYear(), now.getMonth(), now.getDate() - days, hours, minutes, 12).toISOString();
const rows: [string, string, string | null, string | null, string | null, string | null][] = [
  ["policy.stop", "success", "Alex", "Alex desktop", null, "{\"scope\":\"machines\",\"machines\":[\"Build box\"],\"reason\":\"A runaway automation\"}"],
  ["audit.export", "success", "Ines", "Ines laptop", null, "{\"format\":\"csv\",\"records\":214}"],
  ["person.invite", "forbidden", "Bea", "Bea laptop", null, null],
  ["device.approve", "success", "Alex", "Alex desktop", "Build box", null],
  ["team.member", "success", "Alex", "Alex desktop", "Infra", null],
  ["automation.run", "success", "Max", "Max MacBook", "nightly-release-notes", null],
  ["sign_in.complete", "success", "Bea", "Bea laptop", null, "{\"method\":\"passkey\"}"],
  ["grant.revoke", "success", "Ada", "Ada desktop", "session:Fix the parser", null],
  ["relay.open", "success", "Max", "Max MacBook", "Mac mini", null],
  ["server.setup", "success", "Alex", "Alex desktop", null, null],
];
const log: FleetAuditItem[] = rows.map(([action, result, personName, deviceName, target, details], index) => ({
  sequence: 240 - index, recordedAt: at(index < 3 ? 0 : index < 6 ? 1 : index, 15 - index, 5 * index), action, result, personId: personName?.toLowerCase() ?? null, personName,
  deviceId: deviceName ? deviceName.toLowerCase().replace(/ /g, "-") : null, deviceName, target, address: "100.64.0." + (index + 2), requestId: "0HN" + (7000 + index), details,
  hash: "Qm9hcmQtaGFzaC1vZi1yZWNvcmQt" + (240 - index), previousHash: "Qm9hcmQtaGFzaC1vZi1yZWNvcmQt" + (239 - index),
}));
const person = (id: string, name: string, role: string) => ({ id, name, role, status: "active", devices: 1, teams: [], joined: true, isMe: id === "alex" });
const owner: FleetAccessOverview = {
  status: "ok", message: null, me: person("alex", "Alex", "owner"), capabilities: ["access.view", "audit.read", "audit.export"], signer: true, advanced: true,
  document: { number: 2, signedBy: "Alex", signedAt: now.toISOString(), pending: [] },
  people: [person("alex", "Alex", "owner"), person("ada", "Ada", "admin"), person("bea", "Bea", "member"), person("ines", "Ines", "auditor"), person("max", "Max", "member")],
  teams: [], identities: [], grants: [], transfers: [], proposals: [], sessions: [], owners: [], serverUrl: "https://fleet.example", providers: [],
};
let access = owner;
let brokenAt: number | null = null;
// A page of older records held until the test lets it answer, as a slow server would.
let olderHeld: Promise<void> | null = null;
let releaseOlder: () => void = () => {};

const api: FleetAuditApi = {
  status: async () => ({ status: "ok", servers: [{ id: "home", url: "https://fleet.example", organization: "Example", link: "approved", connection: "online", problem: null,
    controller: true, spaces: [], recoveryKey: true, stop: null }], deviceId: "desktop", words: "apple river stone", personName: null }) as never,
  access: async () => access,
  devices: async () => ({ status: "ok", devices: [{ id: "build-box", name: "Build box", kind: "node", status: "approved", online: true, words: "", createdAt: "2026-11-01T08:00:00Z" }],
    skippedEntries: 0, message: null }),
  audit: async request => {
    calls.push(JSON.stringify({ audit: { filters: request.filters, before: request.before } }));
    if (request.before !== null && olderHeld) await olderHeld;
    const filters = request.filters;
    const matching = log.filter(item => (!filters?.actions || filters.actions.some(prefix => item.action === prefix || prefix.endsWith(".") && item.action.startsWith(prefix)))
      && (!filters?.personId || item.personId === filters.personId) && (!filters?.result || (filters.result === "failure" ? item.result !== "success" : item.result === filters.result))
      && (!filters?.text || JSON.stringify(item).includes(filters.text)) && (request.before === null || item.sequence < request.before));
    const limit = request.limit ?? 100;
    const page: FleetAuditPage = { status: "ok", message: null, items: matching.slice(0, Math.min(limit, 6)), before: matching.length > 6 ? matching[5].sequence : null };
    return page;
  },
  verifyAudit: async () => ({ status: "ok", message: null, valid: brokenAt === null, count: 240, brokenAt, headHash: brokenAt === null ? log[0].hash : log[4].hash,
    checkedAt: now.toISOString() }),
  exportAudit: async request => { calls.push(JSON.stringify({ export: { format: request.format, filters: request.filters } }));
    return { status: "ok", message: null, path: `C:\\Users\\alex\\Downloads\\codealta-audit-2026-11-04-1530.${request.format}`, kilobytes: 42 }; },
  revealAuditExport: async () => { calls.push(JSON.stringify({ reveal: true })); return { status: "ok", message: null }; },
};

// The width of the page of the Settings window: about 1250 CSS pixels in a window of 1600, about 430 in one of 900.
function Fixture() {
  const [width, setWidth] = useState(1250);
  const [key, setKey] = useState(0);
  Object.assign(window, { fleetAuditFixture: { setWidth, calls,
    holdOlder: () => { olderHeld = new Promise<void>(resolve => { releaseOlder = () => { olderHeld = null; resolve(); }; }); },
    releaseOlder: () => releaseOlder(),
    member: () => { access = { ...owner, capabilities: ["access.view"] }; setKey(value => value + 1); },
    broken: () => { brokenAt = 236; setKey(value => value + 1); } } });
  return <div className="ide-shell settings-dialog-content" style={{ padding: 12, width, boxSizing: "border-box" }}>
    <FleetAuditSettings key={key} epoch="12345678-1234-1234-1234-123456789abc" api={api} now={fixedNow} />
  </div>;
}

function fixedNow() { return now; }

createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => {} }}>
    <Fixture />
  </ShellLanguageContext.Provider>
</StrictMode>);
