import assert from "node:assert/strict";
import test from "node:test";
import type { FleetAccessOverview } from "#neoastra";
import { assignableRoles, attention, attentionCount, canManagePerson, isAdvanced, isContentCapability, ownerTargets, parseTarget, proposalDetail } from "./access";

const person = (id: string, name: string, role: string, extra: Partial<FleetAccessOverview["people"][number]> = {}) =>
  ({ id, name, role, status: "active", devices: 1, teams: [], joined: true, isMe: false, ...extra });

const home: FleetAccessOverview = {
  status: "ok", message: null, me: person("alex", "Alex", "owner", { isMe: true }), capabilities: ["access.manage", "access.sign", "access.view"], signer: true, advanced: false,
  document: { number: 0, signedBy: null, signedAt: null, pending: [] },
  people: [person("alex", "Alex", "owner", { isMe: true }), person("bea", "Bea", "member"), person("cy", "Cy", "member", { joined: false, devices: 0 })],
  teams: [], identities: [], grants: [], owners: [], transfers: [], proposals: [], serverUrl: "https://home.example",
};

test("a household stays simple; an owner gives every role and an admin all but owner and admin", () => {
  assert.equal(isAdvanced(home), false);
  assert.equal(isAdvanced({ ...home, teams: [{ id: "t", name: "Infra", members: [], machines: [], runsMemberAutomations: false, canManage: true }] }), true);
  assert.deepEqual(assignableRoles(home), ["owner", "admin", "member", "auditor", "guest"]);
  const admin = { ...home, me: person("ada", "Ada", "admin", { isMe: true }) };
  assert.deepEqual(assignableRoles(admin), ["member", "auditor", "guest"]);
  assert.equal(canManagePerson(admin, person("o", "O", "owner")), false, "an admin does not change an owner");
  assert.equal(canManagePerson(home, home.people[0]), false, "nobody changes their own role here");
  assert.equal(canManagePerson(home, home.people[1]), true);
  assert.deepEqual(assignableRoles({ ...home, capabilities: ["access.view"] }), [], "a member gives no role");
});

test("what waits for the person: changes to sign, transfers to take, proposals, resources whose owner left", () => {
  const busy: FleetAccessOverview = {
    ...home,
    document: { number: 2, signedBy: "Alex", signedAt: null, pending: ["Bea is suspended: their work stops on team machines"] },
    transfers: [{ id: "x", resourceKind: "machine", resourceId: "m", resourceName: "Box", fromName: "Bea", toKind: "person", toId: "alex", toName: "Alex", state: "offered",
      reason: "transfer", offeredAt: "2026-11-04T09:00:00Z", incoming: true, outgoing: false }],
    proposals: [{ id: "p", personName: "Bea", sessionId: "s", kind: "role", summary: "Make Bea admin", payload: "{\"personId\":\"bea\",\"role\":\"admin\"}", state: "open",
      createdAt: "2026-11-04T09:00:00Z" }],
    owners: [{ kind: "automation", id: "nightly", name: "nightly", ownerKind: "person", ownerId: "bea", ownerName: "Bea", author: "Bea", paused: true, ownerSuspended: true, canAct: true },
      { kind: "automation", id: "weekly", name: "weekly", ownerKind: "person", ownerId: "alex", ownerName: "Alex", author: "Bea", paused: true, ownerSuspended: false, canAct: true }],
  };
  const waiting = attention(busy);
  assert.equal(attentionCount(waiting), 5);
  assert.deepEqual(waiting.orphans.map(owner => owner.id), ["nightly"]);
  assert.deepEqual(waiting.paused.map(owner => owner.id), ["weekly"]);
  assert.equal(attention({ ...busy, signer: false }).sign.length, 0, "only a signer signs");
  assert.equal(proposalDetail(busy.proposals[0].payload, id => id === "bea" ? "Bea" : id), "personId: Bea · role: admin");
});

test("a resource goes to the organization, a team or another active person who joined", () => {
  const withTeam = { ...home, teams: [{ id: "t", name: "Infra", members: [], machines: [], runsMemberAutomations: false, canManage: true }] };
  assert.deepEqual(ownerTargets(withTeam, { ownerKind: "person", ownerId: "alex" }).map(target => target.value), ["organization:", "team:t", "person:bea"]);
  assert.deepEqual(parseTarget("team:t"), { kind: "team", id: "t" });
  assert.deepEqual(parseTarget("organization:"), { kind: "organization", id: null });
  assert.equal(isContentCapability("session.view-content"), true);
  assert.equal(isContentCapability("automation.run"), false);
});
