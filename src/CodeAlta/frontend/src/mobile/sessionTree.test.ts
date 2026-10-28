import assert from "node:assert/strict";
import test from "node:test";
import type { Live } from "./contract";
import type { SessionView } from "./sessionStore";
import { createFolds, foldKey, keepOrder, sessionTree, subAgentWords, topLevelCount, type TreeRow } from "./sessionTree";

const base = Date.parse("2026-10-27T10:00:00Z");
const live = (id: string, minutesAgo: number, state: Live["live"] = "idle", asking: string | null = null): Live => ({
  id, projectId: "p", live: state, kind: "assistant", line: null, at: new Date(base - minutesAgo * 60_000).toISOString(), activity: null, canSteer: false,
  asking: asking ? { id: asking, revision: "1", state: "pending", immediate: false } : null,
});
const view = (id: string, minutesAgo: number, parentId: string | null = null, state: Live["live"] = "idle", asking: string | null = null): SessionView => ({
  id, title: id, projectId: "p", project: "parser", space: null, model: null, agent: null, worktree: null, parentId, parentTitle: null, summary: null,
  lastActivity: null, live: live(id, minutesAgo, state, asking),
});
const ids = (rows: readonly TreeRow[]) => rows.map(row => `${"-".repeat(row.depth)}${row.view.id}`);
const allOpen = { open: () => true };
const allFolded = { open: () => false };

test("a sub-agent is never above its parent: the parent is as recent as the latest of its branch", () => {
  const views = [view("child", 1, "root"), view("other", 5), view("root", 30), view("grand", 0, "child"), view("second", 10, "root")];
  assert.deepEqual(ids(sessionTree(views, allOpen)), ["root", "-child", "--grand", "-second", "other"]);
  assert.deepEqual(ids(sessionTree(views, allFolded)), ["root", "other"], "folded, the branch is one card");
});

test("the lines of the tree: each sub-agent says whether a sibling follows, and the levels above whether theirs go on", () => {
  const rows = sessionTree([view("root", 30), view("a", 1, "root"), view("a1", 2, "a"), view("a2", 3, "a"), view("b", 4, "root")], allOpen);
  assert.deepEqual(rows.map(row => [row.view.id, row.depth, row.more, row.rails]), [
    ["root", 0, false, []], ["a", 1, true, []], ["a1", 2, true, [true]], ["a2", 2, false, [true]], ["b", 1, false, []],
  ]);
});

test("the summary of folded sub-agents counts those it started and what its whole branch does", () => {
  const rows = sessionTree([view("root", 30), view("a", 1, "root", "running"), view("b", 2, "root", "idle", "ask1"), view("c", 3, "root"),
    view("c1", 4, "c", "tools")], allFolded);
  const subAgents = rows[0].subAgents!;
  assert.deepEqual([subAgents.count, subAgents.running, subAgents.waiting, subAgents.children.map(child => child.id)], [3, 2, 1, ["a", "b", "c"]]);
  assert.deepEqual(subAgentWords(subAgents), { count: "3 sub-agents", states: ["1 waits for you", "2 running"] });
  assert.deepEqual(subAgentWords({ ...subAgents, count: 1, running: 0, waiting: 2 }), { count: "1 sub-agent", states: ["2 wait for you"] });
  assert.equal(rows[0].open, false);
  assert.equal(sessionTree([view("lone", 1)], allOpen)[0].subAgents, null, "no sub-agent, no summary");
});

test("a sub-agent whose parent is not listed is a card of its own that names it; a loop is cut", () => {
  const orphan = { ...view("orphan", 1, "gone"), parentTitle: "Prepare the release" };
  const rows = sessionTree([orphan, view("x", 2, "y"), view("y", 3, "x"), view("self", 4, "self")], allOpen);
  assert.deepEqual(rows.find(row => row.view.id === "orphan")?.parentElsewhere, { title: "Prepare the release" });
  assert.equal(rows.length, 4, "every session once");
  assert.equal(rows.find(row => row.view.id === "self")?.parentElsewhere, undefined);
  assert.equal(topLevelCount([orphan, view("root", 9), view("kid", 1, "root")]), 2, "a listed parent takes the card of its sub-agents");
});

test("an order already shown is kept, and a new session goes after the one it follows", () => {
  assert.deepEqual(keepOrder(["c", "a", "b"], null), ["c", "a", "b"]);
  assert.deepEqual(keepOrder(["c", "a", "b"], new Map([["a", 0], ["b", 1], ["c", 2]])), ["a", "b", "c"]);
  assert.deepEqual(keepOrder(["new", "a", "b"], new Map([["b", 0], ["a", 1]])), ["new", "b", "a"], "the latest new one at the top");
  assert.deepEqual(keepOrder(["a", "new", "b"], new Map([["b", 0], ["a", 1]])), ["b", "a", "new"], "right after the one it follows");
  const views = [view("root", 30), view("kid", 0, "root"), view("other", 5)];
  assert.deepEqual(ids(sessionTree(views, { ...allOpen, held: new Map([["other", 0], ["root", 1]]) })), ["other", "root", "-kid"],
    "a held order keeps the parent where it was, and its new sub-agent under it");
});

test("sub-agents are folded until opened, as the user left them, and open by themselves for an ask that came since", () => {
  const values = new Map<string, string>();
  const store = { get: (key: string) => values.get(key) ?? null, set: (key: string, value: string) => { values.set(key, value); } };
  const folds = createFolds(store);
  assert.equal(folds.open("root", ""), false, "folded at first");
  assert.equal(folds.open("root", "kid:ask1"), true, "a sub-agent waits for the user: opened");
  folds.set("root", false, "kid:ask1");
  assert.equal(folds.open("root", "kid:ask1"), false, "folded by the user while it waited: it stays folded");
  assert.equal(folds.open("root", "kid:ask2"), true, "another ask opens it again");
  folds.set("other", true, "");
  assert.equal(createFolds(store).open("other", ""), true, "kept on the phone");
  assert.deepEqual(Object.keys(JSON.parse(values.get(foldKey)!)), ["root", "other"]);
  values.set(foldKey, "not json");
  assert.equal(createFolds(store).open("root", ""), false, "a broken record is no choice");
  const many = createFolds(store);
  for (let index = 0; index < 205; index++) many.set(`s${index}`, true, "");
  assert.equal(Object.keys(JSON.parse(values.get(foldKey)!)).length, 200, "the latest 200 choices");
  assert.equal(createFolds(store).open("s204", ""), true);
});
