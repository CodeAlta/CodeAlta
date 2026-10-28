import assert from "node:assert/strict";
import test from "node:test";
import { changeLogLevels, countLogFeatures, filterLogRows, logLevelTone, logTime } from "./applicationLogView";

test("log rows are toned by level and timed in local time to the millisecond", () => {
  assert.deepEqual(["Trace", "Debug", "Info", "Warn", "Warning", "Error", "Fatal", "Critical", "?"].map(logLevelTone),
    ["info", "info", "info", "warning", "warning", "error", "error", "error", "info"]);
  const local = new Date(2026, 0, 2, 3, 4, 5, 67);
  assert.deepEqual(logTime(local.toISOString()), { label: "03:04:05.067", title: "2026-01-02 03:04:05" });
  assert.deepEqual(logTime("time"), { label: "time", title: "time" });
});

const rows = [
  { level: "Info", logger: "CodeAlta.Desktop", text: "Startup: window at 120 ms", feature: "system" },
  { level: "Debug", logger: "CodeAlta.Issues.Git", text: "GET https://dev.azure.com/org/proj/_apis/wit/wiql answered 200", feature: "issues" },
  { level: "Warn", logger: "CodeAlta.Issues.Git", text: "Azure DevOps org/proj/app: listing the issues failed", feature: "issues" },
  { level: "Error", logger: "CodeAlta.Agent.OpenAI", text: "The request failed", feature: "providers" },
  { level: "Warn", logger: "Older.Host", text: "A row of a host that names no feature" },
];

test("the viewer keeps the rows of a feature, a level and words, together", () => {
  const texts = (filter: Parameters<typeof filterLogRows>[1]) => filterLogRows(rows, filter).map(row => row.text);
  assert.equal(texts({ level: "all", feature: "", query: "" }).length, 5);
  assert.deepEqual(texts({ level: "all", feature: "issues", query: "" }), [rows[1].text, rows[2].text]);
  assert.deepEqual(texts({ level: "warning", feature: "issues", query: "" }), [rows[2].text]);
  assert.deepEqual(texts({ level: "all", feature: "issues", query: "  WIQL " }), [rows[1].text], "words are found in any case");
  assert.deepEqual(texts({ level: "all", feature: "", query: "agent.openai" }), [rows[3].text], "words are found in the logger too");
  assert.deepEqual(texts({ level: "all", feature: "system", query: "" }), [rows[0].text, rows[4].text], "a row without a feature is of System");
  assert.deepEqual(texts({ level: "error", feature: "issues", query: "" }), []);
});

test("the rows of each feature are counted for the filter", () => {
  assert.deepEqual([...countLogFeatures(rows)], [["system", 2], ["issues", 2], ["providers", 1]]);
  assert.equal(countLogFeatures([]).size, 0);
});

test("a choice of a level gives the levels to save, a feature without its own level written as none", () => {
  const current = { level: "info", features: [{ id: "system", level: null }, { id: "issues", level: null }, { id: "git", level: "warn" }] };
  assert.deepEqual(changeLogLevels(current, "issues", "debug"), { level: "info", features: [{ id: "issues", level: "debug" }, { id: "git", level: "warn" }] });
  assert.deepEqual(changeLogLevels(current, "git", null), { level: "info", features: [] }, "the level of every feature again");
  assert.deepEqual(changeLogLevels(current, null, "error"), { level: "error", features: [{ id: "git", level: "warn" }] }, "the level of every feature changes alone");
});
