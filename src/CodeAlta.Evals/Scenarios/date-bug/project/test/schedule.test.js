import { test } from "node:test";
import assert from "node:assert/strict";
import { weekDays } from "../src/schedule.js";

// The shop's time zone, where the clocks change on the last Sunday of March and of October.
process.env.TZ = "Europe/Paris";

test("the days of a week", () => {
  assert.deepEqual(weekDays("2026-03-23"), ["2026-03-23", "2026-03-24", "2026-03-25", "2026-03-26", "2026-03-27", "2026-03-28", "2026-03-29"]);
});

test("the days of the week the clocks change", () => {
  assert.deepEqual(weekDays("2026-10-19"), ["2026-10-19", "2026-10-20", "2026-10-21", "2026-10-22", "2026-10-23", "2026-10-24", "2026-10-25"]);
  assert.deepEqual(weekDays("2026-10-26"), ["2026-10-26", "2026-10-27", "2026-10-28", "2026-10-29", "2026-10-30", "2026-10-31", "2026-11-01"]);
});
