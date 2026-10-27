import assert from "node:assert/strict";
import test from "node:test";
import { parseScriptResult, parseWorkflowResult } from "./scriptResult";

test("the prompt of a script that ended gives its result as Markdown, what it wrote beside", () => {
  const read = parseWorkflowResult("Script run-72aa19 (Fix the issues) ended:\nScript completed · 36m50s · 4 calls, 4 at once\nOutput:\n```\nResult:\nchecked\n```\n"
    + "Profile: step fix 36m · script 1 ms\nResult:\n### Fix\n- `alta/branch` done\n");

  assert.ok(read);
  assert.equal(read.runId, "run-72aa19");
  assert.equal(read.title, "Fix the issues");
  assert.equal(read.status, "Script completed · 36m50s · 4 calls, 4 at once");
  assert.equal(read.phase, "completed");
  assert.equal(read.result, "### Fix\n- `alta/branch` done");
  assert.equal(read.notes, "Output:\n```\nResult:\nchecked\n```\nProfile: step fix 36m · script 1 ms", "a line of the output is not the result");
  assert.equal(read.fullResult, null);
});

test("a long result names the file that keeps the whole of it", () => {
  const read = parseScriptResult("Script completed · 2.0 s · no calls\nFull result: C:\\Users\\me (x)\\tool-output\\script-run-1-result.md (52,341 characters; a preview of its start and its end is below, read the file for the rest)\n"
    + "Result:\nstart\n\n… 40,000 characters left out: they are in the full result …\n\nend\n");

  assert.deepEqual(read?.fullResult, { path: "C:\\Users\\me (x)\\tool-output\\script-run-1-result.md", characters: "52,341" });
  assert.equal(read?.notes, "");
  assert.match(read!.result!, /^start\n\n… 40,000 characters left out/);
});

test("an object is indented, a failure has no result, another text is not a result", () => {
  assert.equal(parseScriptResult("Script completed · 1 ms · no calls\nResult:\n```json\n{\"files\":3}\n```\n")?.result, "```json\n{\n  \"files\": 3\n}\n```");
  const failed = parseScriptResult("Script failed at line 3:1 · 4 ms · no calls\nTypeError: x is undefined\n   3 | return x.a;\n");
  assert.equal(failed?.phase, "failed");
  assert.equal(failed?.result, null);
  assert.equal(failed?.notes, "TypeError: x is undefined\n   3 | return x.a;");
  assert.equal(parseScriptResult("Script stopped · 1.0 s · 1 call\n")?.phase, "canceled");
  assert.equal(parseWorkflowResult("Please fix the script run-1 ended:\nScript completed"), null);
  assert.equal(parseScriptResult("Scripts completed today"), null);
});

test("a prompt written before the result came last still shows its text", () => {
  const value = JSON.stringify("## Codex\n- `exec` and `wait` ≈ done");
  const read = parseWorkflowResult(`Script run-61039c (Research) ended:\nScript completed · 2m33s · 3 calls, 3 at once\nResult:\n${value}\nProfile: step research 2m33s\n`);
  assert.equal(read?.result, "## Codex\n- `exec` and `wait` ≈ done");
  assert.equal(read?.notes, "Profile: step research 2m33s");

  // Its start and its end, cut in the middle of the JSON of a text, the way the screenshot of a long result shows it.
  const escaped = "\"### Fix: \\u0060alta/wandering\\u0060 (from main\\u0060)\\n- first";
  const cut = parseScriptResult(`Script completed · 36m50s · 4 calls\nResult:\n${escaped}\n… 8,500 characters left out …\nu0060 the end of it\\n\"\nProfile: step a 1s\n`);
  assert.equal(cut?.result, "### Fix: `alta/wandering` (from main`)\n- first\n\n… 8,500 characters left out …\n\n the end of it\n");
  assert.equal(cut?.notes, "Profile: step a 1s");
});
