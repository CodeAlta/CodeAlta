import assert from "node:assert/strict";
import test from "node:test";
import { hostPromptOf, jobOutcome, previewLine } from "./hostPrompt";

const job = "[CodeAlta background job]\nJob: job-1\nTitle: Run the tests\nCommand: dotnet test -c Debug\nResult: succeeded (exit code 0) after 26 s\n\nOutput:\n```\nPassed!\n```";

test("a prompt the user typed is no prompt of the host", () => {
  assert.equal(hostPromptOf({ kind: "user.message", text: "Fix the parser", provenance: null, sourceSessionId: null }), null);
  assert.equal(hostPromptOf({ kind: "assistant.message", text: job, provenance: "background-job" }), null, "only a user message");
});

test("the end of a background job is a job, by its envelope or its provenance", () => {
  for (const provenance of ["background-job", null]) {
    const read = hostPromptOf({ kind: "user.message", text: job, provenance });
    assert.equal(read?.kind, "job");
    if (read?.kind !== "job") return;
    assert.equal(read.job.title, "Run the tests");
    assert.equal(read.job.command, "dotnet test -c Debug");
    assert.deepEqual(read.outcome, { state: "succeeded", label: "succeeded", exitCode: 0, duration: "26 s" });
    assert.match(read.job.body, /Passed!/);
  }
});

test("how a job ended: failed, timed out, stopped", () => {
  assert.deepEqual(jobOutcome({ result: "failed (exit code 2) after 1 min 3 s", phase: "failed" }), { state: "failed", label: "failed", exitCode: 2, duration: "1 min 3 s" });
  assert.deepEqual(jobOutcome({ result: "timed out after 10 min", phase: "failed" }), { state: "failed", label: "timed out", exitCode: null, duration: "10 min" });
  assert.deepEqual(jobOutcome({ result: "stopped after 4 s", phase: "canceled" }), { state: "stopped", label: "stopped", exitCode: null, duration: "4 s" });
});

test("a message of another agent names the session it comes from, by its envelope or by the record", () => {
  const recorded = hostPromptOf({ kind: "user.message", text: "Here is my report.", provenance: "agent-session", sourceSessionId: "child-1" });
  assert.deepEqual(recorded && { kind: recorded.kind, source: recorded.kind === "agent" ? recorded.sourceSessionId : null, body: recorded.kind === "agent" ? recorded.body : null },
    { kind: "agent", source: "child-1", body: "Here is my report." });
  const envelope = "[CodeAlta delegated-agent message]\nSource session: parent-9\nKind: child-session-update\n\nAll done.";
  const read = hostPromptOf({ kind: "user.message", text: envelope });
  assert.equal(read?.kind, "agent");
  if (read?.kind === "agent") {
    assert.equal(read.sourceSessionId, "parent-9");
    assert.equal(read.label, "Child session update");
    assert.equal(read.body, "All done.");
  }
});

test("the result of a script of the background is a workflow; another provenance is titled by it", () => {
  const workflow = hostPromptOf({ kind: "user.message", provenance: "workflow", text: "Script run-7f3c2a (Nightly) ended:\nScript completed · 2.0 s · 3 calls\n\nResult:\nAll **good**." });
  assert.equal(workflow?.kind, "workflow");
  // What it returned is data for the agent: the card shows what the script presented, and its value apart.
  if (workflow?.kind === "workflow") { assert.equal(workflow.result.title, "Nightly"); assert.equal(workflow.body, ""); assert.equal(workflow.result.result, "All **good**."); }
  const presented = hostPromptOf({ kind: "user.message", provenance: "workflow",
    text: "Script run-1 (Review) ended:\nScript completed · 4.0 s · 2 calls\nPresented to the user (they saw it on the card of the run; do not repeat it):\n````markdown\n## Findings\n- one\n````\nResult:\n```json\n{ \"issues\": 1 }\n```" });
  if (presented?.kind === "workflow") { assert.equal(presented.body, "## Findings\n- one"); assert.match(presented.result.result ?? "", /"issues"/); }
  else assert.fail("a workflow");
  const titles = ["reminder", "user-review", "model-fallback", "automation", "user-answer", "plugin", "code-review"].map(provenance => {
    const read = hostPromptOf({ kind: "user.message", provenance, text: "Check the build." });
    return read?.kind === "host" ? [read.title, read.icon] : null;
  });
  assert.deepEqual(titles, [["Reminder", "timer"], ["User review", "eye"], ["Model fallback", "model"], ["Automation", "zap"], ["User answer", "ask"], ["Plugin", "plugin"], ["Code review", "info"]]);
  assert.equal(hostPromptOf({ kind: "user.message", provenance: "Not One!", text: "x" }), null, "what is no provenance is a prompt of the user");
});

test("the preview of a card is a line of plain text", () => {
  assert.equal(previewLine("## Report\n\n```ts\nconst x = 1;\n```\n- **Done**: see [the file](a.md)"), "Report Done: see the file");
  assert.equal(previewLine("x".repeat(300), 20).length, 20);
});
