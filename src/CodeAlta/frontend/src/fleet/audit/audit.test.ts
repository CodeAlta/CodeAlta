import assert from "node:assert/strict";
import test from "node:test";
import type { FleetAuditItem } from "#neoastra";
import { actionLabels, auditCategories, dayName, dayOf, targetNames, targetOf, formatDetails, groupByDay, isFiltered, noFilter, shortHash, toFilters } from "./audit";

const item = (sequence: number, recordedAt: string, action = "team.create", result = "success"): FleetAuditItem => ({
  sequence, recordedAt, action, result, personId: "p1", personName: "Alex", deviceId: "d1", deviceName: "Desktop", target: null, address: null, requestId: null, details: null,
  hash: `h${sequence}`, previousHash: `h${sequence - 1}`,
});

test("no filter asks for the whole log", () => {
  assert.equal(isFiltered(noFilter), false);
  assert.deepEqual(toFilters(noFilter, new Date()), { actions: null, personId: null, deviceId: null, result: null, target: null, address: null, from: null, to: null, text: null });
});

test("kinds of actions ask for their prefixes together, a result and words as they are", () => {
  const actions = (id: string) => auditCategories.find(category => category.id === id)!.actions;
  const filters = toFilters({ ...noFilter, categories: ["access"], outcome: "failure", text: "  infra  ", person: "p2" }, new Date());
  assert.deepEqual(filters?.actions, actions("access"));
  assert.equal(filters?.result, "failure");
  assert.equal(filters?.text, "infra");
  assert.equal(filters?.personId, "p2");
  assert.equal(isFiltered({ ...noFilter, categories: ["access"] }), true);
  assert.deepEqual(toFilters({ ...noFilter, categories: ["budgets", "secrets"] }, new Date())?.actions, [...actions("secrets"), ...actions("budgets")], "in the order of the kinds");
  assert.ok(auditCategories.every(category => category.actions.length > 0), "every kind names its actions");
  // The server takes 32 actions or prefixes at most (FleetAuditQuery.MaxActions): every kind chosen at once stays within them.
  assert.ok(toFilters({ ...noFilter, categories: auditCategories.map(category => category.id) }, new Date())!.actions!.length <= 32);
});

test("a period counts back from now; two days run from the first midnight to the end of the last day, here", () => {
  const now = new Date(2026, 10, 4, 15, 0, 0);
  assert.equal(toFilters({ ...noFilter, period: "week" }, now)?.from, new Date(now.getTime() - 7 * 86_400_000).toISOString());
  const custom = toFilters({ ...noFilter, period: "custom", from: "2026-11-01", to: "2026-11-03" }, now);
  assert.equal(custom?.from, new Date(2026, 10, 1).toISOString());
  assert.equal(custom?.to, new Date(2026, 10, 4).toISOString());
  assert.equal(toFilters({ ...noFilter, period: "custom", from: "2026-02-30" }, now), null, "a day that does not exist does not read");
  assert.equal(toFilters({ ...noFilter, period: "custom", from: "2026-11-03", to: "2026-11-01" }, now), null, "the last day comes after the first");
  assert.equal(toFilters({ ...noFilter, period: "custom" }, now)?.from, null, "a custom period without days is the whole log");
});

test("the timeline groups records by the day they were recorded here, newest first", () => {
  const today = new Date(2026, 10, 4, 12);
  const days = groupByDay([item(5, new Date(2026, 10, 4, 9).toISOString()), item(4, new Date(2026, 10, 4, 1).toISOString()),
    item(3, new Date(2026, 10, 3, 23).toISOString()), item(1, new Date(2026, 9, 1, 8).toISOString())]);
  assert.deepEqual(days.map(group => group.items.map(record => record.sequence)), [[5, 4], [3], [1]]);
  assert.deepEqual(days.map(group => dayName(group.date, today)), ["today", "yesterday", "date"]);
  assert.equal(dayOf(new Date(2026, 10, 4, 9).toISOString()).key, dayOf(new Date(2026, 10, 4, 1).toISOString()).key, "a row of the table says its day");
  assert.notEqual(dayOf(new Date(2026, 10, 4, 1).toISOString()).key, dayOf(new Date(2026, 10, 3, 23).toISOString()).key);
});

test("a target reads as the name of a person, a team or a device, or as a version of the policy", () => {
  const names = targetNames([{ id: "p1", name: "Bea" }], [{ id: "t1", name: "Infra" }], [{ id: "d1", name: "build-box" }, { id: "p1", name: "not a person" }]);
  assert.deepEqual(targetOf({ action: "person.invite", target: "p1" }, names), { name: "Bea" }, "the first list that names an id wins");
  assert.deepEqual(targetOf({ action: "device.approve", target: "d1" }, names), { name: "build-box" });
  assert.deepEqual(targetOf({ action: "policy.stop", target: "4" }, names), { version: "4" });
  assert.equal(targetOf({ action: "automation.run", target: "nightly" }, names), null, "a target without a name shows as written");
  assert.equal(targetOf({ action: "team.create", target: null }, names), null);
});

test("words of actions, short hashes and details", () => {
  assert.equal(actionLabels["device.approve"], "Approved a device");
  assert.equal(actionLabels["policy.stop"], "Stopped the work of the fleet");
  // The server records the end of a relay as "relay", with what went through it: it has words, and the kind of devices holds it.
  assert.equal(actionLabels["relay"], "Closed a channel to a machine");
  assert.ok(auditCategories.find(category => category.id === "devices")?.actions.includes("relay"));
  assert.equal(shortHash("abcdefghijklmnopqrstuvwxyz0123456789"), "abcdefgh…456789");
  assert.equal(shortHash("short"), "short");
  assert.equal(formatDetails("{\"a\":1}"), "{\n  \"a\": 1\n}");
  assert.equal(formatDetails("not json"), "not json");
  assert.equal(formatDetails(null), null);
});
