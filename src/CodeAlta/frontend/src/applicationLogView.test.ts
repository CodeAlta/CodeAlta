import assert from "node:assert/strict";
import test from "node:test";
import { logLevelTone, logTime } from "./applicationLogView";

test("log rows are toned by level and timed in local time to the millisecond", () => {
  assert.deepEqual(["Trace", "Debug", "Info", "Warn", "Warning", "Error", "Fatal", "Critical", "?"].map(logLevelTone),
    ["info", "info", "info", "warning", "warning", "error", "error", "error", "info"]);
  const local = new Date(2026, 0, 2, 3, 4, 5, 67);
  assert.deepEqual(logTime(local.toISOString()), { label: "03:04:05.067", title: "2026-01-02 03:04:05" });
  assert.deepEqual(logTime("time"), { label: "time", title: "time" });
});
