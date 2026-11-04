import assert from "node:assert/strict";
import test from "node:test";
import type { FleetSecretsOverview, FleetSecretView } from "#neoastra";
import { holderNames, isEnvironmentName, needsRotation, parseLabels, scopeLabel, scopeOptions, waitingApprovals } from "./fleetSecrets";

const secret = (change: Partial<FleetSecretView>): FleetSecretView => ({ id: "s1", kind: "secret", name: "TOKEN", scopeKind: "person", scopeId: null, scopeLabel: "Alex",
  version: 1, hostOnly: false, setBy: "Desktop", setAt: "2026-11-03T09:00:00Z", holders: [], flagged: [], value: null, ...change });
const overview: FleetSecretsOverview = {
  status: "ok", message: null, secrets: [], machines: [], people: [], spaces: [{ id: "work", name: "Work" }], projects: [{ id: "api", name: "API" }],
  environments: [{ id: "e1", name: "production", version: 1, approvers: [], approverNames: [], labels: [], automations: [], verified: true },
    { id: "e2", name: "forged", version: 1, approvers: [], approverNames: [], labels: [], automations: [], verified: false }],
  approvals: [
    { id: "late", environment: "production", machine: "Mac", runId: "r2", title: null, status: "pending", requestedAt: "2026-11-03T12:00:00Z", mayApprove: true, review: "review" },
    { id: "early", environment: "production", machine: "Mac", runId: "r1", title: null, status: "pending", requestedAt: "2026-11-03T11:00:00Z", mayApprove: true, review: "review" },
    { id: "done", environment: "production", machine: "Mac", runId: "r0", title: null, status: "approved", requestedAt: "2026-11-03T10:00:00Z", mayApprove: false, review: "review" },
    { id: "other", environment: "production", machine: "Mac", runId: "r3", title: null, status: "pending", requestedAt: "2026-11-03T10:00:00Z", mayApprove: false, review: "review" },
  ],
};

test("a value is set for all the work of the person, a space, a project, or an environment whose rules verify", () => {
  assert.deepEqual(scopeOptions(overview).map(option => [option.value, option.kind, option.id, option.name]), [
    ["person", "person", null, null], ["space:work", "space", "work", "Work"], ["project:api", "project", "api", "API"], ["environment:e1", "environment", "e1", "production"],
  ]);
  assert.deepEqual(scopeLabel(secret({ scopeKind: "project", scopeLabel: "API" })), { key: "Project: {name}", name: "API" });
  assert.deepEqual(scopeLabel(secret({})), { key: "All my work", name: null });
});

test("a value names the machines that hold it, and needs a rotation once a revoked machine held it", () => {
  const held = secret({ holders: [{ id: "desktop", name: "Desktop", machine: false }, { id: "mac", name: "Mac mini", machine: true }] });
  assert.deepEqual(holderNames(held), ["Mac mini"], "the person's computers are not machines");
  assert.equal(needsRotation(held), false);
  assert.equal(needsRotation(secret({ flagged: [{ id: "old", name: "Old", machine: true }] })), true);
});

test("an environment is named by letters, digits, - and _, its labels typed with commas or spaces, and its runs approved oldest first", () => {
  assert.equal(isEnvironmentName("prod-eu_1"), true);
  assert.equal(isEnvironmentName("prod eu"), false);
  assert.equal(isEnvironmentName(""), false);
  assert.deepEqual(parseLabels(" gpu, prod  gpu,,eu "), ["gpu", "prod", "eu"]);
  assert.deepEqual(waitingApprovals(overview).map(approval => approval.id), ["early", "late"], "those the person may give, waiting, the oldest first");
});