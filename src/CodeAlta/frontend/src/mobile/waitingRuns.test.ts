import assert from "node:assert/strict";
import test from "node:test";
import { waitingLine } from "./waitingRuns";

const run = { runId: "run-9", automationId: "a1", name: "Incident diagnosis", machineId: "node-1", machine: "Build box", sessionId: null, since: null };

test("a run that waits says what it is and where it runs", () => {
  assert.equal(waitingLine(run), "Incident diagnosis on Build box waits for your approval.");
  assert.equal(waitingLine({ ...run, name: null, machine: null }), "A run of an automation on node-1 waits for your approval.", "a run the server told of, without its name");
  assert.equal(waitingLine({ ...run, machineId: null, machine: null }), "Incident diagnosis waits for your approval.", "a run of this computer");
});
