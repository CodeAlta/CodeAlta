import assert from "node:assert/strict";
import test from "node:test";
import type { FleetBudgetsOverview, FleetBudgetView, FleetNotificationView } from "#neoastra";
import { approvalRun, budgetIntent, budgetScopeLabel, budgetScopeOptions, formatAmount, newNotifications, notificationPhrase, parseLimit, usedShare, waitingHolds } from "./budgets";

const budget = (change: Partial<FleetBudgetView>): FleetBudgetView => ({ id: "b1", name: "Home", scopeKind: "person", scopeId: null, scopeLabel: "", measure: "tokens",
  limit: 1_000_000, used: 0, granted: 0, period: "month", mode: "enforced", reached: "hold", state: "ok", periodEnd: "2026-12-01T00:00:00Z", version: 1, ...change });
const note = (change: Partial<FleetNotificationView>): FleetNotificationView => ({ id: "n1", kind: "health", severity: "warning", budget: null, machine: "Mac mini", problem: null,
  subject: null, detail: null, createdAt: "2026-11-04T10:00:00Z", resolved: false, read: false, machineId: null, ...change });
const overview: FleetBudgetsOverview = {
  status: "ok", message: null, budgets: [], timeZone: "Europe/Paris",
  holds: [
    { id: "late", budgetId: "b1", budgetName: "Home", machine: "Mac mini", runId: "r2", title: null, status: "waiting", requestedAt: "2026-11-04T12:00:00Z" },
    { id: "early", budgetId: "b1", budgetName: "Home", machine: "Mac mini", runId: "r1", title: "Report", status: "waiting", requestedAt: "2026-11-04T11:00:00Z" },
    { id: "done", budgetId: "b1", budgetName: "Home", machine: "Mac mini", runId: "r0", title: null, status: "continued", requestedAt: "2026-11-04T10:00:00Z" },
  ],
  notifications: [note({ id: "old", read: true }), note({ id: "gone", resolved: true }), note({ id: "late", createdAt: "2026-11-04T12:00:00Z" }), note({ id: "early" })],
  machines: [{ id: "mac", name: "Mac mini" }], spaces: [{ id: "work", name: "Work" }], projects: [{ id: "api", name: "API" }],
};

test("a budget limits all the work of the person, a machine, a space or a project", () => {
  assert.deepEqual(budgetScopeOptions(overview).map(option => [option.value, option.kind, option.id, option.name]), [
    ["person", "person", null, null], ["machine:mac", "machine", "mac", "Mac mini"], ["space:work", "space", "work", "Work"], ["project:api", "project", "api", "API"],
  ]);
  assert.deepEqual(budgetScopeLabel(budget({})), { key: "All my work", name: null });
  assert.deepEqual(budgetScopeLabel(budget({ scopeKind: "machine", scopeLabel: "Mac mini" })), { key: "Machine: {name}", name: "Mac mini" });
});

test("a budget shows what it used of its limit, calm, then warning, then reached", () => {
  assert.equal(usedShare(budget({ used: 250_000 })), 0.25);
  assert.equal(usedShare(budget({ used: 3_000_000 })), 1, "past its limit, the bar is full");
  assert.equal(usedShare(budget({ limit: 0 })), 0);
  assert.deepEqual(["ok", "warning", "reached"].map(budgetIntent), ["primary", "warning", "danger"]);
  assert.equal(formatAmount("tokens", 1_250_000, "en"), "1.3M");
  assert.equal(formatAmount("cost", 42.5, "en"), "$43");
  assert.equal(formatAmount("cost", 4.25, "en"), "$4.25");
});

test("a limit is a positive number of tokens or dollars, as typed", () => {
  assert.equal(parseLimit("2 000 000", "tokens"), 2_000_000);
  assert.equal(parseLimit("1,500", "tokens"), 1500);
  assert.equal(parseLimit("$49.999", "cost"), 50);
  assert.equal(parseLimit("12.34", "cost"), 12.34);
  for (const wrong of ["", "0", "-5", "abc", "1e9", "0.001"]) assert.equal(parseLimit(wrong, wrong === "0.001" ? "cost" : "tokens"), null, wrong);
});

test("the runs that wait and the new notifications come the oldest first; what was decided, dismissed or ended does not", () => {
  assert.deepEqual(waitingHolds(overview).map(hold => hold.id), ["early", "late"]);
  assert.deepEqual(newNotifications(overview).map(notification => notification.id), ["early", "late"]);
});

test("a notification says what happened, with the names this computer gives", () => {
  assert.deepEqual(notificationPhrase(note({ kind: "budget.warning", budget: "Home", machine: null })), { key: "{budget} used 80% of its limit.", values: { budget: "Home" } });
  assert.deepEqual(notificationPhrase(note({ kind: "budget.reached", budget: null, subject: "Home" })), { key: "{budget} reached its limit.", values: { budget: "Home" } });
  assert.deepEqual(notificationPhrase(note({ problem: "disk_low", detail: "900" })), { key: "{machine} is low on disk space: {detail} MB left.", values: { machine: "Mac mini", detail: "900" } });
  assert.deepEqual(notificationPhrase(note({ problem: "sign_in_failed", subject: "anthropic" })).key, "The sign-in of {subject} failed on {machine}.");
  assert.deepEqual(notificationPhrase(note({ problem: "offline", detail: "7" })).values, { machine: "Mac mini", detail: "7" });
  assert.equal(notificationPhrase(note({ problem: "something new" })).key, "{machine} has a problem.");
  // A run that waits for the person says so, and names the run they take over from it.
  const approval = note({ kind: "approval", subject: "a1", detail: "run-9", machineId: "node-1" });
  assert.deepEqual(notificationPhrase(approval), { key: "A run on {machine} waits for your approval.", values: { machine: "Mac mini" } });
  assert.deepEqual(approvalRun(approval), { automationId: "a1", machineId: "node-1", runId: "run-9" });
  assert.equal(approvalRun(note({ kind: "approval", subject: "a1", detail: "run-9" })), null, "an older server names no machine");
  assert.equal(approvalRun(note({ problem: "disk_low", subject: "a1", detail: "900", machineId: "node-1" })), null);
});
