import assert from "node:assert/strict";
import test from "node:test";
import { activityTicks, validActivity, orderObservedActivity, readRecentSessionCount, readSubAgentCount, validRecentSessionCount } from "./recentSessions";

test("recent count storage is canonical, bounded, nonwriting and reports failures", () => {
  for (let i = 1; i <= 50; i++) assert.deepEqual(readRecentSessionCount(() => String(i)), { value: i });
  assert.deepEqual(readRecentSessionCount(() => null), { value: 6 });
  for (const raw of ["", "0", "51", "1.0", "01", " 2", "NaN", "-1", "999999999999999999999"]) {
    assert.equal(readRecentSessionCount(() => raw).value, 6);
    assert.match(readRecentSessionCount(() => raw).notice!, /invalid.*Not overwritten/);
  }
  assert.match(readRecentSessionCount(() => { throw Error(); }).notice!, /unavailable/);
  for (const n of [0, 51, NaN, Infinity, 1.5]) assert.equal(validRecentSessionCount(n), false);
});
test("the count of sub-agents is stored the same way, with a default of its own", () => {
  assert.deepEqual(readSubAgentCount(() => null), { value: 4 });
  assert.deepEqual(readSubAgentCount(() => "50"), { value: 50 });
  assert.deepEqual(readSubAgentCount(() => "0"), { value: 4, issue: "invalid", notice: "Sub-agent count: invalid saved preference; using 4. Not overwritten." });
  assert.equal(readSubAgentCount(() => { throw Error(); }).issue, "unavailable");
});
test("observed activity keeps exact submillisecond order, stable unknowns and rejects malformed facts", () => {
  const timestamp = "2026-01-01T12:00:00.0000001+02:00";
  const row = { timestamp, source: "admitted_agent_event", admittedEvents: "9223372036854775807", omittedEvents: "3" };
  assert.equal(validActivity(row), true);
  assert.equal(activityTicks(timestamp), activityTicks("2026-01-01T10:00:00.0000001+00:00"));
  for (const value of ["2026-02-30T12:00:00.0000000+00:00", "0001-01-01T00:00:00.0000000+00:00", "2026-01-01T24:00:00.0000000+00:00", "2026-01-01T12:00:00.0000000+14:01", "invalid"])
    assert.equal(activityTicks(value), null);
  for (const patch of [{ source: "saved_update" }, { admittedEvents: "9223372036854775808" }, { admittedEvents: "0" }, { omittedEvents: "01" }, { timestamp: null }])
    assert.equal(validActivity({ ...row, ...patch }), false);
  assert.equal(validActivity({ ...row, timestamp: null, admittedEvents: "0" }), true);
  const rows = [{ id: "unknown1", t: null }, { id: "older", t: timestamp }, { id: "unknown2", t: null },
    { id: "newer", t: "2026-01-01T12:00:00.0000002+02:00" }, { id: "tie", t: timestamp }];
  assert.deepEqual(orderObservedActivity(rows, row => row.t).map(row => row.id), ["newer", "older", "tie", "unknown1", "unknown2"]);
  assert.equal(rows[0].id, "unknown1");
});
