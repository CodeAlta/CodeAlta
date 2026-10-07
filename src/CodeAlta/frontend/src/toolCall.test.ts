import assert from "node:assert/strict";
import test from "node:test";
import type { TimelineItem } from "./timeline";
import { altaEnvelope, argumentEntries, callDuration, changedFiles, countLines, diffFiles, formatDuration, formatSize, highlightDiffRows, highlightLanguage,
  highlightLines, matchRanges, outputShape, parseArguments, parseDirectoryListing, parseJsonRecords, parseNumberedLines, parseSearchMatches, parseShellResult,
  patchFiles, toolCallFromItem, toolCallFromRecord, toolFamily, toolState, type ToolCall, type ToolCallRecord } from "./toolCall";

const item = (overrides: Partial<TimelineItem>): TimelineItem => ({ key: "40", eventType: "activity", category: "tool", icon: "tool", title: "shell_command",
  subtitle: null, timestamp: "2026-10-07T08:00:00Z", markdown: null, summary: null, summaryIsCode: false, detailMarkdown: null, details: null,
  detailsLabel: "Tool details", metadata: [], truncated: false, bodyOmitted: false, copyMarkdown: null, toolPhase: "completed", ...overrides });
const call = (overrides: Partial<ToolCall> = {}): ToolCall => ({ name: "tool", kind: "ToolCall", state: "completed", command: null, workingDirectory: null,
  exitCode: null, error: null, arguments: null, output: null, diff: null, readFiles: [], modifiedFiles: [], ...overrides });
const whole = (text: string) => ({ text, partial: false });

test("the phase of a record reads as one of five states", () => {
  assert.deepEqual(["requested", "started", "progressed", "selected", "completed", "deselected", "failed", "canceled", undefined, "other"].map(toolState),
    ["pending", "running", "running", "running", "completed", "completed", "failed", "canceled", "pending", "pending"]);
});

test("a row gives the call at once: the whole command from its arguments, its texts as far as the row holds them", () => {
  const row = toolCallFromItem(item({ summary: "dotnet test", summaryIsCode: true, toolExitCode: 1, toolPhase: "failed", markdown: "shell_command exited with code 1.",
    toolCall: { providerId: "p", runId: "r", activityId: "a", kind: "ToolCall", name: "shell_command", offset: "40", outputOffset: "30", startedAt: null, endedAt: null },
    toolFields: [{ path: "arguments", text: '{"command":"dotnet test\\n  -c Release","workdir":"C:\\\\code","timeoutMs":1000}', truncated: false },
      { path: "content", text: "exit_code: 1", truncated: true }] }));
  assert.equal(row.command, "dotnet test\n  -c Release", "The row previews one line; the arguments have the command.");
  assert.deepEqual([row.state, row.exitCode, row.error, row.workingDirectory], ["failed", 1, "shell_command exited with code 1.", "C:\\code"]);
  assert.deepEqual(row.output, { text: "exit_code: 1", partial: true });
  // Arguments the row cut are not read as JSON: the command is the preview of the row.
  const cut = toolCallFromItem(item({ summary: "dotnet test", summaryIsCode: true, toolFields: [{ path: "arguments", text: '{"command":"dotnet te', truncated: true }] }));
  assert.equal(cut.command, "dotnet test");
  assert.equal(parseArguments(cut.arguments), null);
  // A call that names no command has none, and a completed call has no error.
  const read = toolCallFromItem(item({ title: "read_file", summary: "a.cs", markdown: "ignored" }));
  assert.deepEqual([read.command, read.error, read.kind], [null, null, "ToolCall"]);
});

test("the record of a call completes its row, which keeps the name and the state the live view gives", () => {
  const record: ToolCallRecord = { kind: "ToolCall", phase: "Started", name: "other", message: null, command: "git status", workingDirectory: "C:\\code",
    exitCode: null, error: "ignored while it runs", arguments: whole('{"command":"git status"}'), output: null, diff: null, readFiles: ["a"], modifiedFiles: [] };
  const row = call({ name: "shell_command", state: "completed", output: { text: "bounded", partial: true }, exitCode: 0 });
  const merged = toolCallFromRecord(record, row);
  assert.deepEqual([merged.name, merged.state, merged.command, merged.exitCode, merged.error], ["shell_command", "completed", "git status", 0, null]);
  assert.deepEqual(merged.output, { text: "bounded", partial: true }, "A record of the start has no output: the row's stays.");
  assert.deepEqual(merged.readFiles, ["a"]);
  assert.equal(toolCallFromRecord({ ...record, error: "boom" }, call({ state: "failed" })).error, "boom");
});

test("a call is shown by what it does: changes, a terminal, a file, or its arguments and result", () => {
  assert.equal(toolFamily(call({ name: "anything", diff: whole("@@ -1 +1 @@\n-a\n+b\n") })), "edit");
  assert.equal(toolFamily(call({ name: "apply_patch" })), "edit");
  assert.equal(toolFamily(call({ name: "shell_command", command: "ls" })), "shell");
  assert.equal(toolFamily(call({ name: "x", kind: "CommandExecution" })), "shell");
  assert.equal(toolFamily(call({ name: "alta", command: "alta plugin list" })), "generic", "An alta command writes records, not a terminal output.");
  assert.equal(toolFamily(call({ name: "read_file", output: whole("    1: a\n    2: b") })), "read");
  assert.equal(toolFamily(call({ name: "read_file", output: whole("File 'x' was not found.") })), "generic");
  assert.equal(toolFamily(call({ name: "grep", output: whole("a.cs:1: x") })), "generic");
});

test("the result of a shell command is read into its exit code, its folder and its two outputs", () => {
  assert.deepEqual(parseShellResult("exit_code: 0\r\nworking_directory: C:\\code\r\nstdout:\r\nline 1\r\nline 2\r\nstderr:\r\n(empty)"),
    { exitCode: 0, workingDirectory: "C:\\code", stdout: "line 1\nline 2", stderr: "" });
  assert.deepEqual(parseShellResult("exit_code: -3\nworking_directory: /tmp\nstdout:\n(empty)\nstderr:\nboom\nstderr:\nagain"),
    { exitCode: -3, workingDirectory: "/tmp", stdout: "(empty)\nstderr:\nboom", stderr: "again" }, "The last stderr line starts the standard error.");
  assert.deepEqual(parseShellResult("exit_code: 1\nworking_directory: /tmp\nstdout:\n(empty)\nstderr:\n(empty)"), { exitCode: 1, workingDirectory: "/tmp", stdout: "", stderr: "" });
  for (const other of ["", "shell_command was denied by the host.", "exit_code: x\nworking_directory: /\nstdout:\na\nstderr:\nb", "exit_code: 0\nstdout:\na\nstderr:\nb",
    "exit_code: 0\nworking_directory: /\nstdout:\nno error section"]) assert.equal(parseShellResult(other), null);
});

test("numbered lines are read only when every line has the next number", () => {
  assert.deepEqual(parseNumberedLines("   12: using System;\r\n   13: \r\n   14:     class A"), { start: 12, lines: ["using System;", "", "    class A"] });
  assert.deepEqual(parseNumberedLines("    1: a\n    2: cut in the mi", true), { start: 1, lines: ["a"] }, "A last line the page cut is dropped.");
  for (const other of ["", "File 'x' was not found.", "    1: a\n    3: c", "    1: a\nnot numbered", "    1: only", ""]) {
    const parsed = parseNumberedLines(other, other === "    1: only");
    assert.equal(parsed, null);
  }
});

test("a unified diff is split by file, and a file that is new or deleted is recognized", () => {
  const files = diffFiles("diff --git a/src/A.cs b/src/A.cs\n--- a/src/A.cs\n+++ b/src/A.cs\n@@ -1,2 +1,3 @@\n a\n-b\n+B\n+c\n"
    + "diff --git a/new.json b/new.json\nnew file mode 100644\n--- /dev/null\n+++ b/new.json\n@@ -0,0 +1,2 @@\n+{\n+}\n"
    + "diff --git a/old.txt b/old.txt\ndeleted file mode 100644\n--- a/old.txt\n+++ /dev/null\n@@ -1 +0,0 @@\n-gone\n");
  assert.deepEqual(files.map(file => [file.path, file.operation, file.added, file.removed]), [["src/A.cs", "update", 2, 1], ["new.json", "add", 2, 0], ["old.txt", "delete", 0, 1]]);
  assert.deepEqual(files[0].rows.map(row => row.kind), ["hunk", "context", "removed", "added", "added"]);
  // A diff that names no file is one file without a name.
  assert.deepEqual(diffFiles("@@ -1 +1 @@\n-a\n+b\n").map(file => [file.path, file.added, file.removed]), [["", 1, 1]]);
  assert.deepEqual(diffFiles(""), []);
});

test("the patch of apply_patch is read by file, with moves, before its record has a diff", () => {
  const files = patchFiles("*** Begin Patch\n*** Update File: src/A.cs\n*** Move to: src/B.cs\n@@ class A\n context\n-old\n+new\n*** End of File\n"
    + "*** Add File: notes.md\n+# Notes\n+\n*** Delete File: old.txt\n*** End Patch\ntrailing")!;
  assert.deepEqual(files.map(file => [file.path, file.operation, file.movedTo, file.added, file.removed]),
    [["src/A.cs", "update", "src/B.cs", 1, 1], ["notes.md", "add", null, 2, 0], ["old.txt", "delete", null, 0, 0]]);
  assert.deepEqual(files[0].rows.map(row => [row.kind, row.text, row.oldLine, row.newLine]),
    [["hunk", "class A", null, null], ["context", "context", null, null], ["removed", "old", null, null], ["added", "new", null, null]]);
  assert.equal(patchFiles("not a patch"), null);
});

test("the files a call changes come from its diff, else from what its arguments ask for", () => {
  const diff = whole("diff --git a/a.cs b/a.cs\n--- a/a.cs\n+++ b/a.cs\n@@ -1 +1 @@\n-a\n+b\n");
  assert.deepEqual(changedFiles(call({ diff }), { input: "*** Begin Patch\n*** Add File: other\n+x\n*** End Patch" }).map(file => file.path), ["a.cs"]);
  assert.deepEqual(changedFiles(call(), { input: "*** Begin Patch\n*** Add File: other\n+x\n*** End Patch" }).map(file => [file.path, file.operation]), [["other", "add"]]);
  const replaced = changedFiles(call(), { path: "a.cs", old_string: "one\ntwo", new_string: "three" })[0];
  assert.deepEqual([replaced.path, replaced.removed, replaced.added, replaced.rows.map(row => row.kind)], ["a.cs", 2, 1, ["removed", "removed", "added"]]);
  const written = changedFiles(call(), { path: "new.txt", content: "a\nb\n" })[0];
  assert.deepEqual([written.operation, written.added, written.rows.map(row => row.newLine)], ["add", 2, [1, 2]]);
  assert.deepEqual(changedFiles(call(), { path: "a.cs" }), []);
  assert.deepEqual(changedFiles(call(), null), []);
});

test("lines are highlighted one by one, and a construct over several lines keeps its color on each", () => {
  assert.equal(highlightLanguage("src/Program.cs"), "csharp");
  assert.equal(highlightLanguage("run.sh"), "bash");
  assert.equal(highlightLanguage("Directory.Build.props"), "xml");
  assert.equal(highlightLanguage("notes.unknown"), "plaintext");
  const lines = highlightLines(["/* a comment", "   that goes on */", "var x = 1;"], "csharp")!;
  assert.equal(lines.length, 3);
  for (const line of lines.slice(0, 2)) {
    assert.match(line, /^<span class="hljs-comment">.*<\/span>$/);
    assert.equal(line.split("<span").length, line.split("</span>").length, "Every span of a line is closed on that line.");
  }
  assert.match(lines[2], /hljs-keyword/);
  assert.equal(highlightLines(["<b>bold</b> & more"], "plaintext"), null);
  assert.doesNotMatch(highlightLines(["var s = \"<script>\";"], "csharp")![0], /<script>/, "The text of a line is escaped.");
});

test("the rows of a diff are highlighted by side, hunk by hunk", () => {
  const [file] = diffFiles("diff --git a/a.cs b/a.cs\n--- a/a.cs\n+++ b/a.cs\n@@ -1,2 +1,2 @@\n class A {\n-  int x;\n+  string y;\n@@ -9 +9 @@\n-}\n+} // end\n");
  const html = highlightDiffRows(file.rows, "csharp");
  assert.equal(html.length, file.rows.length);
  assert.equal(html[0], null, "A hunk header is not code.");
  assert.match(html[1]!, /hljs-keyword/);
  assert.match(html[2]!, /int/);
  assert.match(html[3]!, /string/);
  assert.match(html[6]!, /hljs-comment/);
  assert.deepEqual(highlightDiffRows(file.rows, "plaintext"), file.rows.map(() => null));
});

test("arguments are listed by name: short ones on a line, texts of several lines and structures in a block", () => {
  const entries = argumentEntries({ path: "C:\\a\\b.cs", limit: 100, verbose: false, nothing: null, empty: "", script: "() => {\n  return 1;\n}", function: "() =>\n1",
    globs: ["*.cs", "*.md"], none: [], nested: { a: 1 }, command: "skipped" }, ["command"]);
  assert.deepEqual(entries.map(entry => [entry.name, entry.block, entry.language]), [["path", false, null], ["limit", false, null], ["verbose", false, null],
    ["script", true, "javascript"], ["function", true, "javascript"], ["globs", false, null], ["nested", true, "json"]]);
  assert.equal(entries.find(entry => entry.name === "globs")!.value, "*.cs, *.md");
  assert.equal(entries.find(entry => entry.name === "nested")!.value, '{\n  "a": 1\n}');
  assert.equal(argumentEntries({ text: "x".repeat(241) })[0].block, true);
});

test("a folder listing and search matches are read from the results of list_dir and grep", () => {
  assert.deepEqual(parseDirectoryListing("[dir]  src\r\n[file] readme.md\r\n[file] a b.txt"),
    [{ name: "src", directory: true }, { name: "readme.md", directory: false }, { name: "a b.txt", directory: false }]);
  assert.deepEqual(parseDirectoryListing("(empty directory)"), []);
  assert.equal(parseDirectoryListing("Directory 'x' was not found."), null);
  assert.deepEqual(parseSearchMatches("C:\\code\\a.cs:12: class A\nC:\\code\\a.cs:40:   // class B\nnotes.md:3: - a class"),
    [{ path: "C:\\code\\a.cs", matches: [{ line: 12, text: "class A" }, { line: 40, text: "  // class B" }] }, { path: "notes.md", matches: [{ line: 3, text: "- a class" }] }]);
  assert.deepEqual(parseSearchMatches("(no matches)"), []);
  assert.equal(parseSearchMatches("The 'pattern' value is not a valid regular expression."), null);
  assert.deepEqual(matchRanges("public class Class", "class", false), [[7, 12], [13, 18]]);
  assert.deepEqual(matchRanges("public class Class", "class", true), [[7, 12]]);
  assert.deepEqual(matchRanges("abc", "(", false), [], "A pattern the page cannot read marks nothing.");
  assert.deepEqual(matchRanges("abc", "x*", false), []);
});

test("a result is shown as records, as formatted JSON, as Markdown or as it is", () => {
  const records = '{"type":"alta.result","version":1,"exitCode":0,"durationMs":8.5}\n{"type":"alta.plugin.refs","plugins":[]}\n';
  const shape = outputShape(whole(records));
  assert.equal(shape.kind, "records");
  assert.deepEqual(shape.kind === "records" && shape.records.map(record => record.type), ["alta.result", "alta.plugin.refs"]);
  assert.deepEqual(shape.kind === "records" && altaEnvelope(shape.records), { exitCode: 0, durationMs: 8.5 });
  assert.equal(altaEnvelope(parseJsonRecords('{"type":"other"}')!), null);
  assert.deepEqual(outputShape(whole(' {"a":[1,2]} ')), { kind: "json", text: '{\n  "a": [\n    1,\n    2\n  ]\n}' });
  assert.equal(outputShape(whole("Script ran:\n```json\n[1]\n```\n")).kind, "markdown");
  assert.equal(outputShape(whole("## Latest page snapshot\nuid=1_0 RootWebArea")).kind, "text", "A text with no code block keeps its lines as they are.");
  assert.equal(outputShape({ text: '{"a":1}', partial: true }).kind, "text", "A text the page cut is not parsed.");
  assert.equal(parseJsonRecords("Usage: alta [options]"), null);
  assert.equal(parseJsonRecords('{"a":1}\n[1]'), null);
});

test("durations, sizes and line counts are written for a label", () => {
  assert.deepEqual([0, 372, 1000, 4810, 13000, 59400, 60000, 185000, 3725000].map(formatDuration),
    ["0 ms", "372 ms", "1.0 s", "4.8 s", "13 s", "59 s", "1 min 00 s", "3 min 05 s", "1 h 02 min"]);
  assert.equal(formatDuration(-1), "");
  assert.deepEqual([0, 512, 1024, 1434, 3 * 1024 * 1024 + 200_000].map(formatSize), ["0 B", "512 B", "1.0 KB", "1.4 KB", "3.2 MB"]);
  assert.deepEqual(["", "a", "a\nb\n\n", "a\r\nb"].map(countLines), [0, 1, 2, 2]);
  assert.equal(callDuration("2026-10-07T08:00:00Z", "2026-10-07T08:00:13Z"), 13000);
  assert.equal(callDuration(null, "2026-10-07T08:00:13Z"), null);
  assert.equal(callDuration("2026-10-07T08:00:13Z", "2026-10-07T08:00:00Z"), null);
});
