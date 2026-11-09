import assert from "node:assert/strict";
import test from "node:test";
import { insertReferences, issueReference, linkText, matchesReference, referenceKindOfKey, referenceRows, sessionReference, toggledKinds, workReference,
  defaultReferenceKinds, type ReferenceItem } from "./referenceItems";
import type { WorkItem } from "./workItems/workItems";

const work = (id: string, kind: string, more: Partial<WorkItem> = {}): WorkItem => ({ id, kind, title: `Title of ${id}`, summary: null, category: "gap",
  status: "pending", statusText: null, created: "2026-11-01", file: `.alta/${kind}s/${id}.md`, proposedBy: null, runner: null, acknowledged: false,
  runsWith: null, tags: null, shortId: kind === "task" ? id.slice(-4) : null, updated: null, projectId: "p1", stage: "todo", ...more });
const issue = (number: number, updatedAt: string, open = true) => issueReference({ kind: "issue", number, title: `Issue ${number}`, state: open ? "Open" : "Closed",
  open, url: `https://github.com/o/r/issues/${number}`, updatedAt } as never)!;

test("each kind is inserted as the texts name it", () => {
  assert.equal(issue(12, "2026-11-01T00:00:00Z").insert, "[#12](https://github.com/o/r/issues/12)");
  assert.equal(workReference(work("2026-11-08-fix-p53b", "task"))!.insert, "`p53b`", "a task by its short id, in code: a chip in a message");
  assert.equal(workReference(work("2026-10-26-leak", "task", { shortId: null }))!.insert, "`2026-10-26-leak`", "an older task by its id");
  assert.equal(workReference(work("2026-11-01-plan", "plan", { title: "Ship [it]\nnow" }))!.insert, "[Ship \\[it\\] now](.alta/plans/2026-11-01-plan.md)");
  assert.equal(sessionReference({ id: "01a1c209-e03f", title: "Review the parser", updatedAt: null, running: false })!.insert,
    "[Review the parser](session:01a1c209-e03f)");
  assert.equal(sessionReference({ id: "bad id", title: "x", updatedAt: null, running: false }), null);
  assert.equal(linkText("  "), "…");
});

test("the rows mix the kinds shown, the most recent first, and leave the closed ones when asked", () => {
  const items: ReferenceItem[] = [
    issue(1, "2026-10-01T00:00:00Z"), issue(2, "2026-11-05T00:00:00Z", false),
    workReference(work("2026-11-03-task-ab12", "task", { updated: "2026-11-09T08:00:00Z" }))!,
    workReference(work("2026-11-02-done-cd34", "task", { status: "done" }))!,
    workReference(work("2026-10-20-plan", "plan", { created: "2026-10-20" }))!,
    sessionReference({ id: "s1", title: "Parser review", updatedAt: "2026-11-10T00:00:00Z", running: true })!,
  ];
  const keys = (rows: readonly ReferenceItem[]) => rows.map(row => row.key);
  assert.deepEqual(keys(referenceRows(items, defaultReferenceKinds, true, "")),
    ["task:2026-11-03-task-ab12", "issue:issue:2", "task:2026-11-02-done-cd34", "plan:2026-10-20-plan", "issue:issue:1"],
    "the sessions are not shown at first; a task is as recent as its last change");
  assert.deepEqual(keys(referenceRows(items, new Set(["session", "plan"]), false, "")), ["session:s1", "plan:2026-10-20-plan"]);
  assert.deepEqual(keys(referenceRows(items, defaultReferenceKinds, false, "")), ["task:2026-11-03-task-ab12", "plan:2026-10-20-plan", "issue:issue:1"]);
  // The issues are searched by the host: a query filters the others only, by their name, title or id.
  assert.deepEqual(keys(referenceRows(items, defaultReferenceKinds, true, "#ab12")), ["task:2026-11-03-task-ab12", "issue:issue:2", "issue:issue:1"]);
  assert.equal(matchesReference(items[5], "parser REV"), true);
  assert.equal(matchesReference(items[5], "parser fix"), false);
});

test("the keys and the toggles of the kinds", () => {
  const key = (code: string, more: Partial<KeyboardEvent> = {}) => referenceKindOfKey({ code, ctrlKey: true, metaKey: false, altKey: false, ...more });
  assert.deepEqual([key("Digit1"), key("Digit2"), key("Numpad3"), key("Digit4")], ["issue", "task", "plan", "session"]);
  assert.equal(key("Digit5"), null);
  assert.equal(key("Digit1", { ctrlKey: false }), null);
  assert.equal(key("Digit1", { altKey: true }), null);
  assert.equal(key("Digit1", { ctrlKey: false, metaKey: true }), "issue", "Cmd on macOS");
  assert.deepEqual([...toggledKinds(defaultReferenceKinds, "session", false)], ["issue", "task", "plan", "session"]);
  assert.deepEqual([...toggledKinds(defaultReferenceKinds, "task", false)], ["issue", "plan"]);
  assert.deepEqual([...toggledKinds(defaultReferenceKinds, "plan", true)], ["plan"]);
  const one = new Set(["plan"] as const);
  assert.equal(toggledKinds(one, "plan", false), one, "the last kind shown stays");
});

test("references are inserted in place of the # token, after a space when they follow a word", () => {
  assert.deepEqual(insertReferences("fix #p5 now", 4, 7, ["`p53b`"]), { text: "fix `p53b` now", caret: 10 });
  assert.deepEqual(insertReferences("fix", 3, 3, ["`a`", "`b`"]), { text: "fix `a` `b`", caret: 11 });
  assert.equal(insertReferences("x", 0, 0, []), null);
});
