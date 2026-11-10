import assert from "node:assert/strict";
import test from "node:test";
import type { WorkItemRow, WorkItemsProject } from "#neoastra";
import { carriedBy, createdDay, filterWorkItems, readingOrder, sessionCards, shownTags, sortWorkItems, stageCounts, startChoices, tagColor, tagNameOf, workCounts, workDateGroup,
  workItemKey, workItems, workStage, workStatusLabel } from "./workItems";

const row = (id: string, kind: "task" | "plan", status: string, more: Partial<WorkItemRow> = {}): WorkItemRow => ({ id, kind, title: `Title of ${id}`, summary: null,
  category: kind === "task" ? "gap" : null, status, statusText: null, created: "2026-10-07", file: `.alta/${kind}s/${id}.md`, proposedBy: null, runner: null, acknowledged: false, runsWith: null, tags: null, shortId: null, updated: null, ...more });
const project = (projectId: string, tasks: WorkItemRow[], plans: WorkItemRow[] = []): WorkItemsProject => ({ projectId, tasks, plans, truncated: false, tags: null, declinedTags: null, tagProposals: null });
const sessions = new Set(["s1", "s2"]);

test("the list keeps the tasks that have one of the chosen tags, and the tags typed after #", () => {
  const items = workItems([project("p", [row("mobile", "task", "pending", { tags: ["mobile"] }), row("both", "task", "pending", { tags: ["mobile", "ci"] }),
    row("none", "task", "pending")])], sessions);
  const ids = (tags: readonly string[], query = "") => filterWorkItems(items, { projectId: null, kind: "all", stage: "todo", query, tags: new Set(tags) }).map(item => item.id);
  assert.deepEqual(ids([]), ["mobile", "both", "none"]);
  assert.deepEqual(ids(["ci", "web"]), ["both"], "any of the chosen tags");
  assert.deepEqual(ids([], "#mobile"), ["mobile", "both"]);
  assert.deepEqual(ids([], "#mobile #ci"), ["both"], "every typed tag");
  assert.deepEqual(ids([], "#mobile both"), ["both"], "with words of the title");
});

test("the tags shown are those of one project, or of all of them by name", () => {
  const tag = (name: string, openTasks: number, description: string | null = null, listed = true) => ({ name, description, openTasks, listed });
  const projects = [{ ...project("a", []), tags: [tag("mobile", 2, "The phone app."), tag("ci", 1)] }, { ...project("b", []), tags: [tag("mobile", 3, null, false)] }];
  assert.deepEqual(shownTags(projects, "b"), [tag("mobile", 3, null, false)]);
  assert.deepEqual(shownTags(projects, null), [tag("mobile", 5, "The phone app."), tag("ci", 1)]);
  assert.deepEqual(shownTags([project("c", [])], null), [], "a project without tags has none");
});

test("a tag name is typed in any case, with or without #, and its color comes from it", () => {
  assert.equal(tagNameOf(" #Flaky-Tests "), "flaky-tests");
  for (const wrong of ["", "two words", "-ci", "ci-", "a--b", "café", "x".repeat(33)]) assert.equal(tagNameOf(wrong), null, wrong);
  assert.equal(tagColor("mobile"), tagColor("mobile"));
  assert.match(tagColor("mobile"), /^var\(--chart-\d+\)$/);
});

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
  assert.equal(workStatusLabel({ kind: "task", status: "pending", stage: "todo" }), "Inbox");
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
    row("mine", "task", "pending", { proposedBy: "s1", runner: "s1" }),
    row("mine-seen", "task", "pending", { proposedBy: "s1", runner: "s1", acknowledged: true }),
    row("other", "task", "pending", { proposedBy: "s2" }),
    row("nobody", "task", "pending"),
  ], [
    row("ready", "plan", "approved", { proposedBy: "s1" }),
    row("put-away", "plan", "approved", { proposedBy: "s1", acknowledged: true }),
    row("draft", "plan", "draft", { proposedBy: "s1" }),
  ])], sessions);
  // A task the session started itself stays, saying so, until the user puts its card away.
  assert.deepEqual(sessionCards(items, "s1").map(item => item.id), ["ready", "2026-10-07-a", "2026-10-07-b", "mine"], "the plan first, then the tasks in a steady order");
  assert.deepEqual(sessionCards(items, "s2").map(item => item.id), ["other"]);
  assert.deepEqual(carriedBy(items, "s2").map(item => item.id), ["started"]);
  assert.deepEqual(carriedBy(items, "s1").map(item => item.id), ["mine", "mine-seen"]);
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
test("the day a task or a plan was created is that day here, whatever the time zone", () => {
  assert.equal(createdDay("2026-11-08")?.getTime(), new Date(2026, 10, 8).getTime());
  assert.equal(createdDay("2026-11-08T23:30:00")?.getTime(), new Date(2026, 10, 8).getTime(), "a moment is the day it falls on here");
  assert.equal(createdDay(null), null);
  assert.equal(createdDay("someday"), null);
});

test("the list orders the items by when they were created, newest or oldest first, those without a date last", () => {
  const items = workItems([project("one", [row("old", "task", "pending", { created: "2026-10-01", title: "B" }), row("new", "task", "pending", { created: "2026-11-08", title: "Z" }),
    row("none", "task", "pending", { created: null, title: "A" }), row("same", "task", "pending", { created: "2026-11-08", title: "M" })],
    [row("plan", "plan", "draft", { created: "2026-11-05", title: "P" })])], sessions);
  assert.deepEqual(sortWorkItems(items, "newest").map(item => item.id), ["same", "new", "plan", "old", "none"], "the same day by title");
  assert.deepEqual(sortWorkItems(items, "oldest").map(item => item.id), ["old", "plan", "same", "new", "none"]);
});

test("the groups of the list are today, yesterday, this week from Monday, last week, then each month", () => {
  // Sunday 8 November 2026: its week started on Monday the 2nd, the week before on the 26th of October.
  const now = new Date(2026, 10, 8, 15, 0, 0);
  const group = (created: string | null) => workDateGroup(created, now).key;
  assert.equal(group("2026-11-08"), "today");
  assert.equal(group("2026-11-09"), "today", "a day ahead of this clock is today");
  assert.equal(group("2026-11-07"), "yesterday");
  assert.equal(group("2026-11-02"), "this-week");
  assert.equal(group("2026-11-01"), "last-week");
  assert.equal(group("2026-10-26"), "last-week");
  assert.equal(group("2026-10-25"), "2026-10");
  assert.equal(group("2026-09-30"), "2026-9");
  assert.equal(group(null), "unknown");
  assert.deepEqual(workDateGroup("2026-11-07", now).label, { key: "Yesterday" });
  assert.deepEqual(workDateGroup("2026-10-03", now).label, { month: new Date(2026, 9, 1) });
  // On a Monday, the day before is yesterday, and last week starts a week before.
  assert.equal(workDateGroup("2026-11-01", new Date(2026, 10, 2)).key, "yesterday");
  assert.equal(workDateGroup("2026-10-26", new Date(2026, 10, 2)).key, "last-week");
});
