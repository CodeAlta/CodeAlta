import assert from "node:assert/strict";
import test from "node:test";
import type { FleetStatisticsGroupView, FleetStatisticsMeasuresView, FleetStatisticsView } from "#neoastra";
import { translate } from "../localization";
import { costOf, fleetDays, fleetProblem, fleetRows, projectLabel, tokensOf } from "./fleet";
import type { Translate } from "./labels";

// The "My fleet" scope: the days of its periods, the rows of its tables and the names of the projects of other machines.

const t: Translate = (key, parameters) => translate("en", key, parameters);

const measures = (input: number, output: number, activeMs = 0, usd = 0): FleetStatisticsMeasuresView => ({
  activeMs, runs: 1, runsFailed: 0, requests: 1, inputTokens: input, outputTokens: output, cacheReadTokens: 0, cacheWriteTokens: 0, reasoningTokens: 0, toolCalls: 2,
  toolFailures: 0, costs: usd ? [{ unit: "usd", amount: usd }] : [],
});

const group = (change: Partial<FleetStatisticsGroupView>): FleetStatisticsGroupView => ({
  machine: null, machineName: null, project: null, projectName: null, provider: null, model: null, period: null, measures: measures(0, 0), ...change,
});

const view = (groups: FleetStatisticsGroupView[]): FleetStatisticsView => ({
  linkId: "link", from: "2026-11-01", to: "2026-11-03", timeZone: "Europe/Paris", groupBy: [], totals: measures(0, 0), groups, machines: [], truncated: false,
});

test("the periods end today, in local days", () => {
  const today = new Date(2026, 10, 3, 23, 30);
  assert.deepEqual(fleetDays("7d", today), { from: "2026-10-28", to: "2026-11-03" });
  assert.deepEqual(fleetDays("30d", today), { from: "2026-10-05", to: "2026-11-03" });
  assert.deepEqual(fleetDays("90d", today), { from: "2026-08-06", to: "2026-11-03" });
  assert.deepEqual(fleetDays("month", today), { from: "2026-11-01", to: "2026-11-03" });
});

test("a project is named by this computer when it has it, by a short key otherwise, and a session without one is said so", () => {
  assert.equal(projectLabel(t, { project: "Aq3dF9xYzW_kLm0pQr7sTu", projectName: "CodeAlta" }), "CodeAlta");
  assert.equal(projectLabel(t, { project: "Aq3dF9xYzW_kLm0pQr7sTu", projectName: null }), "Project Aq3dF9");
  assert.equal(projectLabel(t, { project: "", projectName: null }), "No project");
});

test("the rows of a table are the largest first, with the tokens in and out and the cost in dollars", () => {
  const rows = fleetRows(t, view([
    group({ machine: "d-1", machineName: "Mac mini", measures: measures(100, 10, 5000) }),
    group({ machine: "d-2", machineName: "Desktop", measures: measures(1000, 300, 1000, 1.5) }),
  ]), "machine");
  assert.deepEqual(rows.map(row => [row.label, row.tokens, row.usd]), [["Desktop", 1300, 1.5], ["Mac mini", 110, 0]]);

  const models = fleetRows(t, view([group({ provider: "anthropic", model: "claude-opus-5-5", measures: measures(5, 5) }), group({ provider: "anthropic", model: "", measures: measures(0, 0) })]), "model");
  assert.deepEqual(models.map(row => [row.label, row.detail]), [["claude-opus-5-5", "anthropic"], ["No model", "anthropic"]], "tool calls have no model of their own");
  assert.equal(tokensOf(measures(2, 3)), 5);
  assert.equal(costOf(measures(0, 0, 0, 2)), 2);
  assert.equal(costOf(measures(0, 0), "credits"), 0);
});

test("a problem of the host says what to do", () => {
  assert.equal(fleetProblem(t, "ok"), null);
  assert.equal(fleetProblem(t, "no_server"), "This computer is not linked to a server.");
  assert.equal(fleetProblem(t, "unreachable"), "The server does not answer.");
  assert.equal(fleetProblem(t, "statistics.invalid"), "The statistics of the fleet could not be read (statistics.invalid).");
});
