import assert from "node:assert/strict";
import test from "node:test";
import { formatReminderDelay, reminderDelaySeconds } from "./reminderDuration";

test("whole seconds and invariant duration forms preserve the exact backend bounds", () => {
  for (const [value, expected] of [["1", 1], ["300", 300], ["86400", 86400],
    ["00:00:01", 1], ["00:05:00", 300], ["23:59:59", 86399], ["1.00:00:00", 86400]] as const)
    assert.equal(reminderDelaySeconds(value), expected, value);
});

test("never round fractions, overflow, implicit/locale signs or malformed times", () => {
  for (const value of ["", "0", "86401", "99999999999999999999", "1.5", "0.5", "1e2", "+1", "-1", " 1 ",
    "00:00:00", "00:00:00.5", "00:00:01.000", "24:00:00", "1.00:00:01", "2.00:00:00",
    "1:00:00", "00:60:00", "00:00:60", "01:2:03", "1,00", "1,00:00:00", "1.24:00:00"])
    assert.equal(reminderDelaySeconds(value), null, value);
});

test("compact unit forms are exact and round-trip through the formatter", () => {
  for (const [value, expected] of [["90s", 90], ["5m", 300], ["1h", 3600], ["1h30m", 5400], ["1h 30m", 5400], ["1d", 86400], ["2M", 120]] as const)
    assert.equal(reminderDelaySeconds(value), expected, value);
  for (const value of ["0s", "0m", "2d", "1.5h", "5 minutes", "m", "1h-5m", "30m1h"])
    assert.equal(reminderDelaySeconds(value), null, value);
  for (const [seconds, text] of [[45, "45s"], [300, "5m"], [5400, "1h 30m"], [86400, "1d"], [3661, "1h 1m 1s"]] as const) {
    assert.equal(formatReminderDelay(seconds), text);
    assert.equal(reminderDelaySeconds(text), seconds);
  }
});