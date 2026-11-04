import assert from "node:assert/strict";
import test from "node:test";
import type { FleetAuditItem } from "#neoastra";
import { actionLabels, auditCategories, dayName, formatDetails, groupByDay, isFiltered, noFilter, shortHash, toFilters } from "./audit";

const item = (sequence: number, recordedAt: string, action = "team.create", result = "success"): FleetAuditItem => ({
  sequence, recordedAt, action, result, personId: "p1", personName: "Alex", deviceId: "d1", deviceName: "Desktop", target: null, address: null, requestId: null, details: null,
  hash: `h${sequence}`, previousHash: `h${sequence - 1}`,
});

test("no filter asks for the whole log", () => {
  assert.equal(isFiltered(noFilter), false);
  assert.deepEqual(toFilters(noFilter, new Date()), { actions: null, personId: null, deviceId: null, result: null, target: null, address: null, from: null, to: null, text: null });
});

test("a kind of action asks for its prefixes, a result and words as they are", () => {
  const filters = toFilters({ ...noFilter, category: "access", outcome: "failure", text: "  infra  ", person: "p2" }, new Date());
  assert.deepEqual(filters?.actions, auditCategories.find(category => category.id === "access")!.actions);
  assert.equal(filters?.result, "failure");
  assert.equal(filters?.text, "infra");
  assert.equal(filters?.personId, "p2");
  assert.ok(auditCategories.every(category => category.id === "all" || category.actions.length > 0), "every kind but everything names its actions");
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
});

test("words of actions, short hashes and details", () => {
  assert.equal(actionLabels["device.approve"], "Approved a device");
  assert.equal(actionLabels["policy.stop"], "Stopped the work of the fleet");
  assert.equal(shortHash("abcdefghijklmnopqrstuvwxyz0123456789"), "abcdefgh…456789");
  assert.equal(shortHash("short"), "short");
  assert.equal(formatDetails("{\"a\":1}"), "{\n  \"a\": 1\n}");
  assert.equal(formatDetails("not json"), "not json");
  assert.equal(formatDetails(null), null);
});
