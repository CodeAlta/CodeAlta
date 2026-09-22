import assert from "node:assert/strict";
import test from "node:test";
import { sessionTime } from "./sessionTime";

const now = Date.parse("2026-09-22T12:00:00Z");

test("session times are relative, with an absolute local tooltip and machine-readable timestamp", () => {
  const recent = sessionTime("2026-09-22T11:55:00Z", now);
  assert.equal(recent.label, "5min ago");
  assert.equal(recent.title, new Date("2026-09-22T11:55:00Z").toString());
  assert.equal(recent.dateTime, "2026-09-22T11:55:00.000Z");
  assert.equal(sessionTime("2026-09-22T10:00:00Z", now).label, "2h ago");
  assert.equal(sessionTime("2026-09-21T12:00:00Z", now).label, "yesterday");
  assert.equal(sessionTime("2026-09-22T12:02:00Z", now).label, "in 2min");
  assert.equal(sessionTime("2026-09-22T11:59:30Z", now).label, "30s ago");
  assert.equal(sessionTime("2026-09-22T11:59:57Z", now).label, "just now");
});

test("invalid timestamps remain visible rather than displaying misleading relative time", () => {
  assert.deepEqual(sessionTime("unknown", now), { label: "unknown", title: "unknown", dateTime: undefined });
});
