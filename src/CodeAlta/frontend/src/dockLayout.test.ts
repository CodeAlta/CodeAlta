import assert from "node:assert/strict";
import test from "node:test";
import { Actions, DockLocation, RowNode, TabSetNode, type Model } from "flexlayout-react";
import { restoreDockLayout, serializeDockLayout } from "./dockLayout";
import { createSessionTabModel } from "./sessionTabLayout";

const tabs = (model: Model, ...ids: string[]) => { for (const id of ids) model.doAction(Actions.addTab({ type: "tab", id, name: id, component: "session" }, model.getActiveTabset()!.getId(), DockLocation.CENTER, -1, false)); };
const panes = (model: Model) => { const result: string[][] = []; model.visitNodes(node => { if (node instanceof TabSetNode) result.push(node.getChildren().map(child => child.getId())); }); return result; };
const root = (model: Model) => { let row: RowNode | undefined; model.visitNodes(node => { if (!row && node instanceof RowNode) row = node; }); return row!; };
const weights = (model: Model) => root(model).getChildren().map(child => Math.round((child as TabSetNode).getWeight()));
const front = (model: Model) => root(model).getChildren().map(child => (child as TabSetNode).getSelectedNode()?.getId());

// Three tabs in two panes, 70% and 30% of the window, the second tab in front of the first pane.
function arranged() {
  const model = createSessionTabModel();
  tabs(model, "a", "b", "c");
  model.doAction(Actions.moveNode("c", model.getActiveTabset()!.getId(), DockLocation.RIGHT, -1, true));
  model.doAction(Actions.adjustWeights(root(model).getId(), [70, 30]));
  model.doAction(Actions.selectTab("b"));
  return model;
}

test("the panes of a dock come back as they were: their tabs, their sizes and the tab in front of each", () => {
  const model = arranged();
  assert.deepEqual(panes(model), [["a", "b"], ["c"]]);
  const text = serializeDockLayout(model)!;
  const restored = restoreDockLayout(text, new Set(["a", "b", "c"]))!;
  assert.deepEqual(panes(restored), [["a", "b"], ["c"]]);
  assert.deepEqual(weights(restored), [70, 30], "the sizes are the shares of the window, not pixels");
  assert.deepEqual(front(restored), ["b", "c"]);
});

test("a tab that is no longer there leaves its place, and a pane that holds none leaves with them", () => {
  const text = serializeDockLayout(arranged())!;
  assert.deepEqual(panes(restoreDockLayout(text, new Set(["a", "c"]))!), [["a"], ["c"]]);
  assert.deepEqual(panes(restoreDockLayout(text, new Set(["a", "b"]))!), [["a", "b"]]);
  assert.deepEqual(front(restoreDockLayout(text, new Set(["a", "b"]))!), ["b"], "the tab in front stays in front");
  // The tab in front is gone: the first one of its pane is in front.
  assert.deepEqual(front(restoreDockLayout(text, new Set(["a", "c"]))!), ["a", "c"]);
  assert.equal(restoreDockLayout(text, new Set(["other"])), null, "no tab of the window was in it: the dock has its single pane");
  // Tabs it did not know take no place: the window adds them to a pane when it shows them.
  assert.deepEqual(panes(restoreDockLayout(text, new Set(["a", "b", "c", "d"]))!), [["a", "b"], ["c"]]);
});

test("an arrangement that cannot be read is no arrangement, and a dock without tabs has none to keep", () => {
  const valid = new Set(["a"]);
  for (const text of [null, undefined, "", "not json", "{}", "[]", JSON.stringify({ version: 2, layout: {} }), JSON.stringify({ version: 1 }),
    JSON.stringify({ version: 1, layout: { type: "tabset", children: [{ type: "tab", id: "a" }] } }), JSON.stringify({ version: 1, layout: { type: "row", children: "a" } }),
    JSON.stringify({ version: 1, layout: { type: "row", children: [{ type: "tabset", children: [{ type: "tab", id: "a" }, { type: "tab", id: "a" }] }] } })])
    assert.equal(restoreDockLayout(text, valid), null, String(text));
  assert.equal(restoreDockLayout("x".repeat(262_145), valid), null);
  assert.equal(serializeDockLayout(createSessionTabModel()), null);
  const model = createSessionTabModel();
  tabs(model, ...Array.from({ length: 6000 }, (_, at) => `session:${"x".repeat(60)}${at}`));
  assert.equal(serializeDockLayout(model), null, "too long for a page to keep");
});
