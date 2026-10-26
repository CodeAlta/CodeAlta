import assert from "node:assert/strict";
import test from "node:test";
import type { LabelItem } from "#neoastra";
import { labelMoveTarget } from "./labelDrag";
import { canMoveLabel, labelPath, labelsOfSpace, readLabels, type Label } from "./labels";
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
