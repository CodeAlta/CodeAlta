import assert from "node:assert/strict";
import test from "node:test";
import type { ReviewOverviewDto } from "#neoastra";
import { checkoutName, plansUnderReview, reviewItems, reviewStage } from "./reviewItems";
import { filterWorkItems, stageCounts, workKindIcon, workStatusLabel } from "./workItems";

const entry = (values: Partial<ReviewOverviewDto>): ReviewOverviewDto => ({ checkout: null, missing: false, target: "changes", path: null, drafts: 0, open: 0, addressed: 0,
  resolved: 0, request: null, lastRound: null, updatedAt: "2026-10-20T10:00:00Z", ...values });
const t = (key: string, values?: Record<string, string | number>) => key.replace(/\{(\w+)\}/g, (_, name: string) => String(values?.[name] ?? ""));

test("a review waits for the user, for the agent, or for nobody", () => {
  assert.equal(reviewStage(entry({ drafts: 1 })), "todo", "drafts not submitted");
  assert.equal(reviewStage(entry({ addressed: 2, open: 1 })), "todo", "answers to resolve come first");
  assert.equal(reviewStage(entry({ request: { id: "a", sessionId: "s", target: "changes", path: null, title: null, createdAt: "" } })), "todo", "a request of an agent");
  assert.equal(reviewStage(entry({ missing: true, checkout: "/w/x" })), "todo", "a worktree that is gone waits for its deletion");
  assert.equal(reviewStage(entry({ open: 3 })), "running");
  assert.equal(reviewStage(entry({ resolved: 4 })), "closed");
});

test("the reviews are rows of Work items, beside the tasks and the plans", () => {
  const rows = reviewItems([{ projectId: "p", name: "P", entries: [
    entry({ target: "file", path: ".alta/plans/2026-10-20-x.md", drafts: 2, request: { id: "a", sessionId: "s1", target: "file", path: ".alta/plans/2026-10-20-x.md", title: null, createdAt: "" } }),
    entry({ checkout: "/home/me/.alta/worktrees/codealta/fiery-cirrus/", target: "branch", open: 1 }),
  ] }], t);
  assert.deepEqual(rows.map(row => [row.kind, row.title, row.stage, row.runner]), [
    ["review", "2026-10-20-x.md", "todo", "s1"], ["review", "The changes of the branch", "running", null]]);
  assert.equal(rows[0].summary, "Project folder · {count} drafts".replace("{count}", "2"));
  assert.equal(rows[1].summary, "Worktree fiery-cirrus · 1 open");
  assert.notEqual(rows[0].id, rows[1].id);
  assert.equal(workStatusLabel(rows[0]), "Waiting for you");
  assert.equal(workStatusLabel(rows[1]), "Waiting for the agent");
  assert.equal(workKindIcon("review"), "eye");
  // The filters and the counts of the stages take them like the other kinds.
  assert.equal(filterWorkItems(rows, { projectId: null, kind: "review", stage: "todo", query: "" }).length, 1);
  assert.equal(filterWorkItems(rows, { projectId: null, kind: "plan", stage: "todo", query: "" }).length, 0);
  assert.deepEqual(stageCounts(rows, null, "review"), { todo: 1, running: 1, later: 0, closed: 0 });
  // The plan of the project folder is marked as under review.
  assert.ok(plansUnderReview(rows).has("p\n.alta/plans/2026-10-20-x.md"));
});

test("a checkout is named by its folder", () => {
  assert.equal(checkoutName(null), null);
  assert.equal(checkoutName("C:\\\\work\\\\trees\\\\x\\\\"), "x");
  assert.equal(checkoutName("/a/b/c"), "c");
});
