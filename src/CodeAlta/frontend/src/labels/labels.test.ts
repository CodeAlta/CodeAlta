import assert from "node:assert/strict";
import test from "node:test";
import type { LabelItem } from "#neoastra";
import { labelMoveTarget } from "./labelDrag";
import { canMoveLabel, hasLabelWords, labelPath, labelsOfSpace, labelSubtreeProjects, projectLabelsOnly, readLabels, sameLabels, sessionLabels, sessionLabelsOnly, splitLabelWords, type Label } from "./labels";
import { steppedPlace } from "./LabelSettings";

const item = (id: string, look: Partial<LabelItem> = {}): LabelItem =>
  ({ id, name: id.toUpperCase(), description: null, icon: null, color: null, order: 1, parent: null, favorite: false, space: null, path: id, projectIds: [], ...look });
// infra › helm › charts, then tools; ops is in the space work.
const labels: readonly Label[] = readLabels([item("infra"), item("helm", { parent: "infra" }), item("charts", { parent: "helm" }), item("tools"), item("ops", { space: "work" })]);

test("the labels the host lists are read safely: a parent that is not listed is read as none", () => {
  const read = readLabels([item("aa", { parent: "gone", color: "red" }), item("Not An Id"), item("aa"), { ...item("bb"), projectIds: ["p", 3 as unknown as string] }]);
  assert.deepEqual(read.map(label => [label.id, label.parent, label.color]), [["aa", null, null], ["bb", null, null]]);
  assert.deepEqual(read[1].projectIds, ["p"]);
  assert.equal(labelPath(labels[2], labels), "INFRA › HELM › CHARTS");
  assert.deepEqual(labelsOfSpace(labels, "personal").map(label => label.id), ["infra", "helm", "charts", "tools"]);
  assert.deepEqual(labelsOfSpace(labels, "work").map(label => label.id), ["infra", "helm", "charts", "tools", "ops"]);
});

test("the labels for sessions are a tree of their own, and a session names its labels", () => {
  const all = readLabels([item("infra"), item("audit", { forSessions: true }), item("weekly", { parent: "audit", forSessions: true }), item("bug", { forSessions: true })]);
  assert.deepEqual(projectLabelsOnly(all).map(label => label.id), ["infra"]);
  assert.deepEqual(sessionLabelsOnly(all).map(label => label.id), ["audit", "weekly", "bug"]);
  assert.equal(projectLabelsOnly(labels), labels, "Without labels for sessions, the labels are the same list.");
  assert.deepEqual(sessionLabels({ labels: ["bug", "gone", "audit"] }, sessionLabelsOnly(all)).map(label => label.id), ["audit", "bug"], "In the order of their tree; one not listed is left out.");
  assert.deepEqual(sessionLabels({}, all), []);
  assert.equal(sameLabels(all, readLabels([item("infra"), item("audit"), item("weekly", { parent: "audit", forSessions: true }), item("bug", { forSessions: true })])), false);
});

test("a word label: of a filter keeps the sessions with a label whose id starts with it", () => {
  assert.deepEqual(splitLabelWords(["login", "label:Audit", "label:pr"]), { words: ["login"], labels: ["audit", "pr"] });
  const words = ["login"];
  assert.equal(splitLabelWords(words).words, words);
  assert.equal(hasLabelWords({ labels: ["audit", "pr-review"] }, ["audit", "pr"]), true);
  assert.equal(hasLabelWords({ labels: ["audit"] }, ["audit", "pr"]), false, "Every label word is needed.");
  assert.equal(hasLabelWords({ labels: null }, []), true);
  assert.equal(hasLabelWords({}, ["audit"]), false);
});

test("a label never goes under itself, under its own sub-labels, or deeper than labels go", () => {
  assert.equal(canMoveLabel("infra", "charts", labels), false);
  assert.equal(canMoveLabel("tools", "charts", labels), true);
  assert.equal(canMoveLabel("helm", "tools", labels), true);
  const deep = readLabels([item("l1"), item("l2", { parent: "l1" }), item("l3", { parent: "l2" }), item("l4", { parent: "l3" }), item("xx"), item("yy", { parent: "xx" })]);
  assert.equal(canMoveLabel("xx", "l3", deep), false, "xx and yy would be at levels 4 and 5.");
  assert.equal(canMoveLabel("yy", "l3", deep), true);
});

test("a drop before, after or on a label gives the new parent and the place among the siblings without the label moved", () => {
  assert.deepEqual(labelMoveTarget(labels, "tools", { labelId: "infra", place: "before" }), { parent: null, index: 0 });
  assert.deepEqual(labelMoveTarget(labels, "infra", { labelId: "tools", place: "after" }), { parent: null, index: 1 });
  assert.deepEqual(labelMoveTarget(labels, "tools", { labelId: "helm", place: "into" }), { parent: "helm", index: -1 });
  assert.equal(labelMoveTarget(labels, "tools", { labelId: "tools", place: "into" }), null);
  assert.equal(labelMoveTarget(labels, "tools", { labelId: "", place: "into" }), null);
});

test("the buttons of a row move a label one step: up, down, under the label above, or out beside its parent", () => {
  assert.equal(steppedPlace(labels, "infra", "up"), null);
  assert.deepEqual(steppedPlace(labels, "infra", "down"), { parent: null, index: 1 });
  assert.deepEqual(steppedPlace(labels, "tools", "in"), { parent: "infra", index: -1 });
  assert.equal(steppedPlace(labels, "infra", "in"), null);
  assert.deepEqual(steppedPlace(labels, "charts", "out"), { parent: "infra", index: 1 });
  assert.equal(steppedPlace(labels, "tools", "out"), null);
});

test("a label speaks for its projects and for those of the labels under it", () => {
  const tree = readLabels([item("infra", { projectIds: ["a"] }), item("helm", { parent: "infra", projectIds: ["b", "a"] }),
    item("charts", { parent: "helm", projectIds: ["c"] }), item("tools", { projectIds: ["d"] })]);
  assert.deepEqual([...labelSubtreeProjects("infra", tree)].sort(), ["a", "b", "c"]);
  assert.deepEqual([...labelSubtreeProjects("helm", tree)].sort(), ["a", "b", "c"]);
  assert.deepEqual([...labelSubtreeProjects("tools", tree)], ["d"]);
  assert.equal(labelSubtreeProjects("gone", tree).size, 0);
});
