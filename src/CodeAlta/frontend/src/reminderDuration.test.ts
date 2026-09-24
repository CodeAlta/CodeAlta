import assert from "node:assert/strict";
import test from "node:test";
import { reminderDelaySeconds } from "./reminderDuration";

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
