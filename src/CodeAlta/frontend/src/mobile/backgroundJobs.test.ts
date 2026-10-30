import assert from "node:assert/strict";
import test from "node:test";
import { jobFacts, listedJobs } from "./backgroundJobs";
import type { BackgroundJob } from "./contract";

const now = Date.parse("2026-10-30T17:00:00Z");
const job: BackgroundJob = { id: "job-a", state: "running", command: "dotnet test", folder: "/project", startedAt: new Date(now - 26_000).toISOString() };

test("running jobs survive a finished agent turn; recent outcomes expire after ten minutes", () => {
  const recent = { ...job, id: "recent", state: "succeeded" as const, endedAt: new Date(now - 599_999).toISOString() };
  const old = { ...recent, id: "old", endedAt: new Date(now - 600_000).toISOString() };
  assert.deepEqual(listedJobs([job, recent, old], now), [job, recent]);
  assert.deepEqual(listedJobs([job, recent], now + 600_000), [job]);
});

test("job facts distinguish success, failure, timeout and stop, with exit code and fixed ended duration", () => {
  assert.equal(jobFacts(job, now), "Running · 26s");
  for (const [state, label] of [["succeeded", "Succeeded"], ["failed", "Failed"], ["timed_out", "Timed out"], ["cancelled", "Stopped"]] as const) {
    assert.equal(jobFacts({ ...job, state, endedAt: new Date(now).toISOString(), exitCode: 1 }, now + 60_000), `${label} · Exit code 1 · 26s`);
  }
});
