import assert from "node:assert/strict";
import test from "node:test";
import type { WorkItemRow, WorkItemsProject } from "#neoastra";
import { carriedBy, filterWorkItems, readingOrder, sessionCards, stageCounts, startChoices, workCounts, workItemKey, workItems, workStage, workStatusLabel } from "./workItems";

const row = (id: string, kind: "task" | "plan", status: string, more: Partial<WorkItemRow> = {}): WorkItemRow => ({ id, kind, title: `Title of ${id}`, summary: null,
  category: kind === "task" ? "gap" : null, status, statusText: null, created: "2026-10-07", file: `.alta/${kind}s/${id}.md`, proposedBy: null, runner: null, acknowledged: false, ...more });
const project = (projectId: string, tasks: WorkItemRow[], plans: WorkItemRow[] = []): WorkItemsProject => ({ projectId, tasks, plans, truncated: false });
const sessions = new Set(["s1", "s2"]);

test("an item is where its file and its session put it", () => {
  assert.equal(workStage(row("a", "task", "pending"), sessions), "todo");
  assert.equal(workStage(row("a", "task", "pending", { runner: "s1" }), sessions), "running");
  assert.equal(workStage(row("a", "task", "pending", { runner: "gone" }), sessions), "todo", "a session that was deleted carries nothing out");
  assert.equal(workStage(row("a", "task", "later", { runner: "s1" }), sessions), "later");
  assert.equal(workStage(row("a", "task", "done"), sessions), "closed");
  assert.equal(workStage(row("a", "task", "dismissed"), sessions), "closed");
  assert.equal(workStage(row("p", "plan", "draft"), sessions), "todo");
  assert.equal(workStage(row("p", "plan", "approved", { runner: "s2" }), sessions), "running");
  assert.equal(workStage(row("p", "plan", "in-progress"), sessions), "running", "a plan says itself that it is in progress");
  assert.equal(workStage(row("p", "plan", "blocked"), sessions), "todo");
  assert.equal(workStage(row("p", "plan", "done", { runner: "s2" }), sessions), "closed");
  assert.equal(workStatusLabel({ kind: "plan", status: "approved", stage: "todo" }), "Ready");
  assert.equal(workStatusLabel({ kind: "task", status: "pending", stage: "running" }), "In progress");
  assert.equal(workStatusLabel({ kind: "task", status: "pending", stage: "todo" }), "To do");
});

test("a project counts what waits and what a session works on now", () => {
  const items = workItems([
    project("one", [row("a", "task", "pending"), row("b", "task", "pending", { runner: "s1" }), row("c", "task", "later"), row("d", "task", "done")],
      [row("p", "plan", "approved"), row("q", "plan", "in-progress"), row("r", "plan", "done")]),
    project("two", [row("e", "task", "pending", { runner: "s2" })]),
  ], sessions);
  assert.deepEqual(items.map(item => item.id), ["p", "q", "r", "a", "b", "c", "d", "e"], "the plans come first in a project");
  const counts = workCounts(items, new Set(["s1"]));
  assert.deepEqual(counts.get("one"), { open: 2, running: 1 }, "a plan whose file says in progress, with no session at work, is neither");
  assert.equal(counts.get("two"), undefined, "a session that is idle works on nothing");
  assert.deepEqual(workCounts(items, new Set(["s2"])).get("two"), { open: 0, running: 1 });
  assert.deepEqual(stageCounts(items, "one", "all"), { todo: 2, running: 2, later: 1, closed: 2 });
  assert.deepEqual(stageCounts(items, null, "task"), { todo: 1, running: 2, later: 1, closed: 1 });
});

test("a session shows what it proposed until the user decides", () => {
  const items = workItems([project("one", [
    row("2026-10-07-b", "task", "pending", { proposedBy: "s1" }),
    row("2026-10-07-a", "task", "pending", { proposedBy: "s1" }),
    row("later", "task", "later", { proposedBy: "s1" }),
    row("started", "task", "pending", { proposedBy: "s1", runner: "s2" }),
    row("other", "task", "pending", { proposedBy: "s2" }),
    row("nobody", "task", "pending"),
  ], [
    row("ready", "plan", "approved", { proposedBy: "s1" }),
    row("put-away", "plan", "approved", { proposedBy: "s1", acknowledged: true }),
    row("draft", "plan", "draft", { proposedBy: "s1" }),
  ])], sessions);
  assert.deepEqual(sessionCards(items, "s1").map(item => item.id), ["ready", "2026-10-07-a", "2026-10-07-b"], "the plan first, then the tasks in a steady order");
  assert.deepEqual(sessionCards(items, "s2").map(item => item.id), ["other"]);
  assert.deepEqual(carriedBy(items, "s2").map(item => item.id), ["started"]);
  assert.deepEqual(carriedBy(items, "s1"), []);
  assert.equal(workItemKey(items[0]), "one\nplan\nready");
});

test("the projects used last are read first", () => {
  const projects = [{ id: "a" }, { id: "b" }, { id: "c", archived: true }, { id: "d" }, { id: "e" }];
  const sessionRows = [{ projectId: "d", updatedAt: "2026-10-07T10:00:00Z" }, { projectId: "b", updatedAt: "2026-10-07T12:00:00Z" },
    { projectId: "b", updatedAt: "2026-10-01T00:00:00Z" }, { projectId: null, updatedAt: "2026-10-07T13:00:00Z" }];
  assert.deepEqual(readingOrder(projects, sessionRows, null), ["b", "d", "a", "e"]);
  assert.deepEqual(readingOrder(projects, sessionRows, "e"), ["e", "b", "d", "a"], "the selected project before all");
  assert.deepEqual(readingOrder([], [], null), []);
});

test("the preferred way of starting comes first, and only a session can do the work itself", () => {
  assert.deepEqual(startChoices("worktree", true), ["worktree", "session", "here"]);
  assert.deepEqual(startChoices("here", true), ["here", "worktree", "session"]);
  assert.deepEqual(startChoices("here", false), ["worktree", "session"], "the Work items tab has no session to do it in");
  assert.deepEqual(startChoices("unknown", true), ["worktree", "session", "here"]);
});

test("the list is one stage of one project, of one kind, matching what is typed", () => {
  const items = workItems([project("one", [row("fix-locator", "task", "pending", { summary: "Windows drive letter" }), row("other", "task", "later")], [row("plan", "plan", "approved")]),
    project("two", [row("elsewhere", "task", "pending")])], sessions);
  const ids = (filter: Parameters<typeof filterWorkItems>[1]) => filterWorkItems(items, filter).map(item => item.id);
  assert.deepEqual(ids({ projectId: null, kind: "all", stage: "todo", query: "" }), ["plan", "fix-locator", "elsewhere"]);
  assert.deepEqual(ids({ projectId: "one", kind: "task", stage: "todo", query: "" }), ["fix-locator"]);
  assert.deepEqual(ids({ projectId: null, kind: "all", stage: "todo", query: "  DRIVE windows " }), ["fix-locator"]);
  assert.deepEqual(ids({ projectId: "one", kind: "all", stage: "later", query: "" }), ["other"]);
  assert.deepEqual(ids({ projectId: "one", kind: "plan", stage: "later", query: "" }), []);
});
