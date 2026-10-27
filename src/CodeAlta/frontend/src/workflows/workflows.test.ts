import assert from "node:assert/strict";
import test from "node:test";
import type { WorkflowRunItem, WorkflowScriptItem, WorkflowScriptParameter, WorkflowStepItem } from "#neoastra";
import { argumentsOf, fieldKind, fieldsOf, filterScripts, initialValues, projectScriptPath, runDuration, runsRefreshMilliseconds, sortRuns, stepColumns } from "./workflows";

test("a script of the project opens in its code editor by its path there; yours and the samples do not", () => {
  assert.equal(projectScriptPath({ scope: "project", path: "C:\\code\\App\\.alta\\scripts\\triage.js" }, "C:\\code\\App"), ".alta/scripts/triage.js");
  assert.equal(projectScriptPath({ scope: "project", path: "c:\\Code\\app\\.alta\\scripts\\triage.js" }, "C:\\code\\App\\"), ".alta/scripts/triage.js", "Windows: the case alone differs");
  assert.equal(projectScriptPath({ scope: "project", path: "/home/me/App/.alta/scripts/triage.js" }, "/home/me/App"), ".alta/scripts/triage.js");
  assert.equal(projectScriptPath({ scope: "project", path: "/home/me/app/.alta/scripts/triage.js" }, "/home/me/App"), null, "elsewhere, the case counts");
  assert.equal(projectScriptPath({ scope: "global", path: "C:\\Users\\me\\.alta\\scripts\\triage.js" }, "C:\\code\\App"), null);
  assert.equal(projectScriptPath({ scope: "project", path: "C:\\code\\App2\\.alta\\scripts\\x.js" }, "C:\\code\\App"), null, "not a folder that only starts like it");
  assert.equal(projectScriptPath({ scope: "project", path: "C:\\code\\App\\.alta\\scripts\\x.js" }, null), null);
});

const step = (id: string, after: string[] | null = null, each = false): WorkflowStepItem => ({ id, title: id, after, each });
const parameter = (name: string, type: string | null, required = true, value: string | null = null): WorkflowScriptParameter =>
  ({ name, type, required, description: null, default: value });
const run = (id: string, state: string, startedAt: string, endedAt: string | null = null): WorkflowRunItem =>
  ({ id, sessionId: "s", title: null, state, startedAt, endedAt, callId: null, calls: 0 });

test("the steps of a script are drawn in stages: in order, side by side when they run after the same step", () => {
  // No `after`: after the step before; an empty list: at the start.
  assert.deepEqual(stepColumns([step("plan"), step("build"), step("ship")]).map(column => column.map(item => item.id)), [["plan"], ["build"], ["ship"]]);
  assert.deepEqual(stepColumns([step("fetch"), step("security", ["fetch"]), step("style", ["fetch"]), step("tests", ["fetch"], true), step("summary", ["security", "style", "tests"])])
    .map(column => column.map(item => item.id)), [["fetch"], ["security", "style", "tests"], ["summary"]]);
  assert.deepEqual(stepColumns([step("a", []), step("b", [])]).map(column => column.map(item => item.id)), [["a", "b"]]);
  assert.deepEqual(stepColumns([]), []);
});

test("a field of Run… edits a parameter by its type, and starts from its default", () => {
  assert.deepEqual(["string", "number", "integer", "boolean", "array", "object", null].map(fieldKind), ["text", "number", "integer", "boolean", "json", "json", "json"]);
  const parameters = [parameter("pr", "integer"), parameter("focus", "string", false, "\"security\""), parameter("draft", "boolean", false, "true"),
    parameter("files", "array", false, "[\"a.cs\"]"), parameter("note", "string", false)];
  assert.deepEqual(initialValues(parameters), { pr: "", focus: "security", draft: true, files: "[\n  \"a.cs\"\n]", note: "" });
});

test("the form gives the arguments of the script, and says which field is wrong", () => {
  const fields = fieldsOf([parameter("pr", "integer"), parameter("ratio", "number", false), parameter("focus", "string", false),
    parameter("draft", "boolean", false), parameter("files", "array", false)]);
  assert.deepEqual(argumentsOf(fields, { pr: "231", ratio: "", focus: "", draft: false, files: "[\"a.cs\"]" }), { json: "{\"pr\":231,\"draft\":false,\"files\":[\"a.cs\"]}" });
  assert.deepEqual(argumentsOf(fields, { pr: " ", draft: false }), { problem: { field: "pr", message: "Fill in {name}.", values: { name: "pr" } } });
  assert.deepEqual(argumentsOf(fields, { pr: "2.5", draft: false }), { problem: { field: "pr", message: "{name} is a whole number.", values: { name: "pr" } } });
  assert.deepEqual(argumentsOf(fields, { pr: "1", ratio: "x", draft: false }), { problem: { field: "ratio", message: "{name} is a number.", values: { name: "ratio" } } });
  assert.deepEqual(argumentsOf(fields, { pr: "1", files: "[", draft: false }), { problem: { field: "files", message: "{name} is not valid JSON.", values: { name: "files" } } });
  assert.deepEqual(argumentsOf([], {}), { json: "{}" });
});

test("the runs list the running ones first, then the newest, and read again often only while one runs", () => {
  const runs = [run("old", "completed", "2026-10-27T08:00:00Z", "2026-10-27T08:01:00Z"), run("going", "running", "2026-10-27T07:00:00Z"),
    run("new", "failed", "2026-10-27T09:00:00Z", "2026-10-27T09:00:30Z")];
  assert.deepEqual(sortRuns(runs).map(item => item.id), ["going", "new", "old"]);
  assert.equal(runDuration(runs[0], Date.parse("2026-10-27T10:00:00Z")), 60_000);
  assert.equal(runDuration(runs[1], Date.parse("2026-10-27T07:00:05Z")), 5000);
  assert.equal(runsRefreshMilliseconds(runs), 2000);
  assert.equal(runsRefreshMilliseconds([runs[0]]), 10_000);
});

test("the library is searched by name, title and description", () => {
  const script = (name: string, description: string | null): WorkflowScriptItem =>
    ({ name, scope: "project", title: null, description, problem: null, path: `${name}.js`, parameters: [], steps: [] });
  const scripts = [script("review-pr", "Review a pull request."), script("count-lines", null)];
  assert.deepEqual(filterScripts(scripts, "pull review").map(item => item.name), ["review-pr"]);
  assert.deepEqual(filterScripts(scripts, "COUNT").map(item => item.name), ["count-lines"]);
  assert.equal(filterScripts(scripts, " ").length, 2);
});
