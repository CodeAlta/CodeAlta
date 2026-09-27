import test from "node:test";
import assert from "node:assert/strict";
import { activeProjectReference, insertProjectReference, validReferenceSpans } from "./projectReferences";
import { captureSubmission } from "./sessionOperations";

test("presentation validates host UTF-16 ranges without interpreting reference syntax", () => {
  assert.equal(validReferenceSpans("😀 @file", [{ start: 3, length: 5, status: "resolved" }]), true);
  for (const spans of [[{ start: -1, length: 1, status: "resolved" }], [{ start: 0, length: 9, status: "resolved" }],
    [{ start: 0, length: 1, status: "invented" }], [{ start: 0, length: 1, status: "resolved" }, { start: 0, length: 1, status: "escaped" }],
    Array.from({ length: 257 }, (_, start) => ({ start, length: 1, status: "unresolved" }))])
    assert.equal(validReferenceSpans("😀 @file", spans), false);
});

test("Send captures reference scope by value, without caller-owned mutable identity", () => {
  const scope = { projectId: "project", projectPath: "/owned/project" };
  const send = captureSubmission("epoch", "session", "@file:2-4", "original", null, scope)!;
  scope.projectPath = "/different";
  assert.deepEqual(send.references, { projectId: "project", projectPath: "/owned/project" });
  assert.ok(Object.isFrozen(send.references));
  assert.equal(captureSubmission("epoch", "session", "@file", "original", null, { projectId: "", projectPath: "/owned" }), null);
});

test("reference editing handles escapes, quoted paths and bounded insertion", () => {
  assert.equal(activeProjectReference("@@literal", 9), null);
  assert.equal(activeProjectReference("mail@host", 9), null);
  assert.equal(activeProjectReference("@src/app.cs:2-4", 15), null);
  assert.deepEqual(activeProjectReference("@src/old.cs:2-4", 4), { start: 0, end: 11, query: "src" });
  assert.deepEqual(insertProjectReference("@src/old.cs:2-4", 0, 11, "src/app.cs", false), { text: '@"src/app.cs":2-4', caret: 13 });
  assert.deepEqual(activeProjectReference('see @"src/a b', 13), { start: 4, end: 13, query: "src/a b" });
  assert.deepEqual(insertProjectReference("see @sr end", 4, 7, "src/a b.cs", false), { text: 'see @"src/a b.cs"  end', caret: 18 });
  for (const path of ["../secret", "/absolute", "C:/secret", "a\\b", 'a"b', "a\nfile", "a//b"])
    assert.equal(insertProjectReference("@", 0, 1, path, false), null);
  assert.equal(insertProjectReference("x".repeat(32768), 0, 0, "a", false), null);
});
