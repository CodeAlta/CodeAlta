import assert from "node:assert/strict";
import test from "node:test";
import type { WorkspaceProject } from "#neoastra";
import type { Label } from "../labels/labels";
import { projectRailGroups, railGroupHidden, type RailGroup } from "./projectRail";

const project = (id: string): WorkspaceProject => ({ id, name: id, path: `/${id}`, archived: false });
const label = (id: string, projectIds: string[], look: Partial<Label> = {}): Label =>
  ({ id, name: id.toUpperCase(), description: null, icon: null, color: null, order: 1, parent: null, favorite: false, space: null, projectIds, ...look });
// Each part as a line: its kind or label, its path and step, and its projects.
const shape = (groups: readonly RailGroup[]) => groups.map(group => group.kind === "label"
  ? `${group.path.map(above => above.id + "/").join("")}${group.label.id}@${group.depth}:${group.projects.map(value => value.id).join(",")}`
  : `${group.kind}:${group.projects.map(value => value.id).join(",")}`);

test("a space without labels keeps the list as it is", () => {
  assert.deepEqual(projectRailGroups([project("a")], 0, []), []);
});

test("projects are listed under each of their labels, sub-labels step in once, the rest come last", () => {
  const projects = ["fav", "a", "b", "c", "d"].map(project);
  const labels = [label("infra", ["a", "b", "fav"]), label("helm", ["b"], { parent: "infra" }), label("charts", ["c"], { parent: "helm" }), label("tools", [])];
  assert.deepEqual(shape(projectRailGroups(projects, 1, labels)), [
    "favorites:fav", "infra@0:a,b", "helm@1:b", "charts@1:c", "tools@0:", "other:d",
  ]);
});

test("a favorite label comes first; a favorite sub-label moves up alone, with its path, and its parent stays", () => {
  const projects = ["a", "b", "c"].map(project);
  const labels = [label("infra", ["a"]), label("helm", ["b"], { parent: "infra", favorite: true }), label("old", ["c"], { parent: "helm" }),
    label("ops", ["c"], { favorite: true }), label("inner", [], { parent: "ops", favorite: true })];
  const groups = projectRailGroups(projects, 0, labels);
  assert.deepEqual(shape(groups), ["infra/helm@0:b", "old@1:c", "ops@0:c", "inner@1:", "infra@0:a"]);
  const old = groups[1];
  assert.ok(old.kind === "label");
  assert.equal(railGroupHidden(old, id => id === "helm"), true, "Folding a label hides the labels under it.");
  assert.equal(railGroupHidden(old, id => id === "infra"), false, "A label moved up is not hidden by its parent.");
});

test("while the list is filtered, a label without a project listed is left out, unless a label under it has one", () => {
  const labels = [label("infra", []), label("helm", ["b"], { parent: "infra" }), label("empty", [])];
  assert.deepEqual(shape(projectRailGroups([project("b")], 0, labels, false)), ["infra@0:", "helm@1:b"]);
});
