import assert from "node:assert/strict";
import test from "node:test";
import type { WorkItemRow } from "#neoastra";
import { closedTask, findTaskReference, taskReferenceIndex } from "./taskReferences";
import { filterWorkItems, workItems } from "./workItems";

const row = (id: string, more: Partial<WorkItemRow> = {}): WorkItemRow => ({ id, kind: "task", title: `Title of ${id}`, summary: null, category: "gap", status: "pending",
  statusText: null, created: "2026-11-08", file: `.alta/tasks/${id}.md`, proposedBy: null, runner: null, acknowledged: false, runsWith: null, tags: null, shortId: id.slice(-4), updated: null, ...more });

test("a text names a task of its project by its short id or its id, written alone", () => {
  const items = workItems([
    { projectId: "p1", tasks: [row("2026-11-08-fix-the-parser-p53b"), row("2026-10-26-leak", { shortId: null })], plans: [row("2026-11-01-plan-x2b4", { kind: "plan", shortId: null })], truncated: false, tags: null, declinedTags: null, tagProposals: null },
    { projectId: "p2", tasks: [row("2026-11-08-other-q9rh")], plans: [], truncated: false, tags: null, declinedTags: null, tagProposals: null },
  ], new Set());
  const index = taskReferenceIndex(items);

  assert.equal(findTaskReference(index, "p1", "p53b")?.id, "2026-11-08-fix-the-parser-p53b");
  assert.equal(findTaskReference(index, "p1", " p53b ")?.id, "2026-11-08-fix-the-parser-p53b", "a code span keeps the spaces written around it");
  assert.equal(findTaskReference(index, "p1", "2026-11-08-fix-the-parser-p53b")?.id, "2026-11-08-fix-the-parser-p53b");
  assert.equal(findTaskReference(index, "p1", "2026-10-26-leak")?.id, "2026-10-26-leak", "a task written before short ids is named by its id");
  assert.equal(findTaskReference(index, "p1", "q9rh"), null, "a task of another project is not one of this text");
  assert.equal(findTaskReference(index, "p2", "q9rh")?.projectId, "p2");
  assert.equal(findTaskReference(index, "p1", "x2b4"), null, "a plan is not a task");
  assert.equal(findTaskReference(index, "p1", "p53b is done"), null, "a code span that says more than the id names nothing");
  assert.equal(findTaskReference(index, "p1", "P53B"), null);
  assert.equal(findTaskReference(index, null, "p53b"), null, "a text outside a project names no task");
});

test("a short id two tasks share names neither, and their ids still do", () => {
  const index = taskReferenceIndex(workItems([{ projectId: "p1", tasks: [row("2026-11-08-one-mwf9"), row("2026-11-09-two-mwf9")], plans: [], truncated: false, tags: null, declinedTags: null, tagProposals: null }], new Set()));

  assert.equal(findTaskReference(index, "p1", "mwf9"), null);
  assert.equal(findTaskReference(index, "p1", "2026-11-09-two-mwf9")?.title, "Title of 2026-11-09-two-mwf9");
});

test("a task that is done or dismissed is closed, and the list finds a task by its short id", () => {
  assert.equal(closedTask({ status: "done" }), true);
  assert.equal(closedTask({ status: "dismissed" }), true);
  assert.equal(closedTask({ status: "later" }), false);
  const items = workItems([{ projectId: "p1", tasks: [row("2026-11-08-fix-the-parser-p53b"), row("2026-11-08-other-q9rh")], plans: [], truncated: false, tags: null, declinedTags: null, tagProposals: null }], new Set());
  assert.deepEqual(filterWorkItems(items, { projectId: null, kind: "all", stage: "todo", query: "p53b" }).map(item => item.id), ["2026-11-08-fix-the-parser-p53b"]);
});
