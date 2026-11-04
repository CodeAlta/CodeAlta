// Disposable page: Settings > Fleet > People and access of the owner's computer at one server. `fleetAccessFixture.home()` shows a
// household (an owner, a member, a person invited); `company()` an organization with a team, a team machine, an automation whose
// owner left, a transfer to take, a proposal of an agent and changes to sign. Driven by fleetAccess.browser.test.ts: what the page
// asks is recorded in `calls`.
import { StrictMode, useState } from "react";
import { createRoot } from "react-dom/client";
import type { FleetAccessOverview } from "#neoastra";
import { ShellLanguageContext } from "../../shellLanguage";
import { FleetAccessSettings, type FleetAccessApi } from "./FleetAccessSettings";

document.documentElement.classList.add("bp6-dark");

const calls: string[] = [];
const person = (id: string, name: string, role: string, extra: Partial<FleetAccessOverview["people"][number]> = {}) =>
  ({ id, name, role, status: "active", devices: 1, teams: [], joined: true, isMe: false, ...extra });
const me = person("alex", "Alex", "owner", { isMe: true, devices: 2 });
const household: FleetAccessOverview = {
  status: "ok", message: null, me, capabilities: ["access.manage", "access.review", "access.sign", "access.view", "audit.read"], signer: true, advanced: false,
  document: { number: 0, signedBy: null, signedAt: null, pending: [] },
  people: [me, person("bea", "Bea", "member"), person("cy", "Cy", "member", { joined: false, devices: 0 })],
  teams: [], identities: [], grants: [], transfers: [], proposals: [], sessions: [{ id: "s1", title: "Fix the parser", machineName: "Mac mini" }],
  owners: [{ kind: "machine", id: "mini", name: "Mac mini", ownerKind: "person", ownerId: "alex", ownerName: "Alex", author: null, paused: false, ownerSuspended: false, canAct: true }],
  serverUrl: "https://home.example", providers: [],
};
const company: FleetAccessOverview = {
  ...household, advanced: true,
  me: { ...me, teams: [{ teamId: "infra", teamName: "Infra", role: "maintainer" }] },
  document: { number: 3, signedBy: "Alex", signedAt: "2026-11-03T09:00:00Z", pending: ["Max is a member of Infra", "Sam is suspended: their work stops on team machines"] },
  people: [{ ...me, teams: [{ teamId: "infra", teamName: "Infra", role: "maintainer" }] }, person("max", "Max", "member", { teams: [{ teamId: "infra", teamName: "Infra", role: "member" }] }),
    person("ada", "Ada", "admin"), person("sam", "Sam", "member", { status: "suspended" })],
  teams: [{ id: "infra", name: "Infra", runsMemberAutomations: false, canManage: true, machines: [{ id: "box", name: "Build box" }], onCall: null,
    members: [{ personId: "alex", name: "Alex", role: "maintainer" }, { personId: "max", name: "Max", role: "member" }] }],
  identities: [{ id: "svc", name: "infra-bot", ownerKind: "team", ownerName: "Infra", enabled: false }],
  providers: [{ id: "infra-anthropic", ownerKind: "team", ownerName: "Infra", name: "Infra Anthropic", type: "anthropic", endpoint: null, models: ["claude-sonnet-5-5"],
    machines: 1, canManage: true }],
  grants: [{ id: "g1", subjectKind: "person", subjectId: "ada", subjectName: "Ada", capability: "session.view-content", scopeKind: "resource", scopeId: "session:s1", scopeName: "Fix the parser",
    status: "pending_keys", expiresAt: null }],
  owners: [
    { kind: "machine", id: "box", name: "Build box", ownerKind: "team", ownerId: "infra", ownerName: "Infra", author: null, paused: false, ownerSuspended: false, canAct: true },
    { kind: "automation", id: "nightly-release-notes", name: "nightly-release-notes", ownerKind: "person", ownerId: "sam", ownerName: "Sam", author: "Sam", paused: true,
      ownerSuspended: true, canAct: true },
  ],
  transfers: [{ id: "t1", resourceKind: "machine", resourceId: "gpu", resourceName: "GPU tower", fromName: "Ada", toKind: "team", toId: "infra", toName: "Infra", state: "offered",
    reason: "transfer", offeredAt: "2026-11-04T08:00:00Z", incoming: true, outgoing: false }],
  proposals: [{ id: "p1", personName: "Max", sessionId: "s1", kind: "team-member", summary: "Ada reviews the release automations with the infra team",
    payload: "{\"teamId\":\"infra\",\"personId\":\"ada\",\"role\":\"member\"}", state: "open", createdAt: "2026-11-04T08:30:00Z" }],
};
let overview = household;
const ok = async () => ({ status: "ok", message: null });
const record = (name: string) => async (request: Record<string, unknown>) => {
  const { expectedEpoch: _epoch, linkId: _link, ...rest } = request;
  calls.push(JSON.stringify({ [name]: rest }));
  return ok();
};
const api: FleetAccessApi = {
  status: async () => ({ status: "ok", servers: [{ id: "home", url: "https://home.example", organization: "Home", link: "approved", connection: "online", problem: null,
    controller: true, spaces: [], recoveryKey: true, shareUrl: null }], deviceId: "desktop", words: "apple river stone", personName: null }) as never,
  access: async () => overview,
  invitePerson: async request => { calls.push(JSON.stringify({ invite: { name: request.name, role: request.role } }));
    return { status: "ok", message: null, token: "aet_example-code", expiresAt: "2026-11-07T09:00:00Z", serverUrl: "https://home.example" }; },
  setPersonRole: record("role") as never, deprovisionPerson: async request => { calls.push(JSON.stringify({ deprovision: { personId: request.personId,
    successorKind: request.successorKind, successorId: request.successorId } })); return { status: "ok", message: null, transferred: 1, notRevoked: [] }; },
  createTeam: record("createTeam") as never, updateTeam: record("updateTeam") as never, dissolveTeam: record("dissolveTeam") as never, setTeamMember: record("member") as never, setOnCall: record("oncall") as never,
  createGrant: record("grant") as never, revokeGrant: record("revokeGrant") as never, confirmGrantKeys: record("keys") as never, offerTransfer: record("offer") as never,
  decideTransfer: record("transfer") as never, resumeResource: record("resume") as never, createServiceIdentity: record("identity") as never,
  enableServiceIdentity: record("enable") as never, decideProposal: record("proposal") as never,
  prepareAccessDocument: async () => ({ status: "ok", message: null, changes: overview.document?.pending ?? [], number: (overview.document?.number ?? 0) + 1 }),
  signAccessDocument: record("sign") as never,
  setScopeProvider: record("provider") as never, deleteScopeProvider: record("deleteProvider") as never,
  accessReview: async () => ({ status: "ok", message: null, csv: "principal_kind,principal_id\r\n", lines: 1 }),
};

// The width of the page of the Settings window: about 1250 CSS pixels in a window of 1600, about 430 in one of 900.
function Fixture() {
  const [width, setWidth] = useState(1250);
  const [key, setKey] = useState(0);
  Object.assign(window, { fleetAccessFixture: { setWidth, calls, home: () => { overview = household; setKey(value => value + 1); },
    company: () => { overview = company; setKey(value => value + 1); } } });
  return <div className="ide-shell settings-dialog-content" style={{ padding: 12, width, boxSizing: "border-box" }}>
    <FleetAccessSettings key={key} epoch="12345678-1234-1234-1234-123456789abc" api={api} />
  </div>;
}

createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => {} }}>
    <Fixture />
  </ShellLanguageContext.Provider>
</StrictMode>);