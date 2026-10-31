import { test } from "node:test";
import assert from "node:assert/strict";

const root = process.env.EVAL_PROJECT_URL;
const dates = await import(new URL("src/dates.js", root));
const { deliveryDay, weekDays } = await import(new URL("src/schedule.js", root));

for (const zone of ["Europe/Paris", "America/New_York", "Asia/Tokyo", "UTC", "Pacific/Auckland"]) {
  test("days are right in " + zone, () => {
    process.env.TZ = zone;
    assert.equal(dates.formatDay(dates.parseDay("2026-03-29")), "2026-03-29");
    assert.equal(dates.formatDay(dates.addDays(dates.parseDay("2026-03-28"), 1)), "2026-03-29");
    assert.equal(dates.formatDay(dates.addDays(dates.parseDay("2026-03-28"), 2)), "2026-03-30");
    assert.equal(dates.formatDay(dates.addDays(dates.parseDay("2026-11-02"), -2)), "2026-10-31");
    assert.equal(dates.formatDay(dates.addDays(dates.parseDay("2026-12-31"), 1)), "2027-01-01");
    assert.equal(deliveryDay("2026-10-24", 3), "2026-10-27");
    assert.equal(deliveryDay("2026-03-27", 4), "2026-03-31");
    assert.deepEqual(weekDays("2026-03-23").at(-1), "2026-03-29");
    assert.deepEqual(weekDays("2026-04-06")[0], "2026-04-06");
  });
}
