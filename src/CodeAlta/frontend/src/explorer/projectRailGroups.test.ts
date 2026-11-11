import assert from "node:assert/strict";
import test from "node:test";
import type { WorkspaceProject } from "#neoastra";
import type { Label } from "../labels/labels";
import { projectRailGroups, projectRailProjection, projectTitle, type RailGroup } from "./projectRail";

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

test("projects are listed under each of their labels, each sub-label one level deeper, the rest come last", () => {
  const projects = ["fav", "a", "b", "c", "d", "e"].map(project);
  const labels = [label("infra", ["a", "b", "fav"]), label("helm", ["b"], { parent: "infra" }), label("charts", ["c"], { parent: "helm" }),
    label("local", ["e"], { parent: "charts" }), label("tools", [])];
  const groups = projectRailGroups(projects, 1, labels);
  assert.deepEqual(shape(groups), [
    "favorites:fav", "infra@0:a,b", "helm@1:b", "charts@2:c", "local@3:e", "tools@0:", "other:d",
  ]);
  const local = groups[4];
  assert.ok(local.kind === "label");
  assert.deepEqual(local.ancestors, ["infra", "helm", "charts"], "A label is listed in the last label above it.");
});

test("a favorite label comes first; a favorite sub-label moves up alone, with its path, and its parent stays", () => {
  const projects = ["a", "b", "c"].map(project);
  const labels = [label("infra", ["a"]), label("helm", ["b"], { parent: "infra", favorite: true }), label("old", ["c"], { parent: "helm" }),
    label("ops", ["c"], { favorite: true }), label("inner", [], { parent: "ops", favorite: true })];
  const groups = projectRailGroups(projects, 0, labels);
  assert.deepEqual(shape(groups), ["infra/helm@0:b", "old@1:c", "ops@0:c", "inner@1:", "infra@0:a"]);
  const [helm, old] = groups;
  assert.ok(helm.kind === "label" && old.kind === "label");
  assert.deepEqual(old.ancestors, ["helm"], "A label stays in the label above it.");
  assert.deepEqual(helm.ancestors, [], "A label moved up is not in its parent.");
});

test("while the list is filtered, a label without a project listed is left out, unless a label under it has one", () => {
  const labels = [label("infra", []), label("helm", ["b"], { parent: "infra" }), label("empty", [])];
  assert.deepEqual(shape(projectRailGroups([project("b")], 0, labels, false)), ["infra@0:", "helm@1:b"]);
});

const sub = (id: string, parent: string, path = `/${parent}/${id}`): WorkspaceProject =>
  ({ id, name: id, path, archived: false, parentId: parent, subPath: id, repositoryName: parent.toUpperCase() });

test("a repository with sub-projects is a group: its own row first, its labels of the map nested, the rest after", () => {
  const projects = [project("contoso"), sub("billing", "contoso"), sub("web", "contoso"), sub("tools", "contoso"), project("solo")];
  const labels = [label("contoso.backend", ["billing"], { repository: "contoso" }), label("contoso.frontend", ["web"], { repository: "contoso" })];
  const groups = projectRailGroups(projects, 0, labels);
  assert.deepEqual(shape(groups), ["repository:contoso@0:contoso,tools", "contoso.backend@1:billing", "contoso.frontend@1:web", "other:solo"]);
  const group = groups[0];
  assert.ok(group.kind === "label" && group.repository?.id === "contoso");
  const backend = groups[1];
  assert.ok(backend.kind === "label");
  assert.deepEqual(backend.ancestors, ["repository:contoso"], "The labels of a map are listed in the group of their repository.");
});

test("a repository goes under the first label of the user it has, and its sub-projects keep the labels of the user", () => {
  const projects = [project("contoso"), sub("billing", "contoso"), project("solo")];
  const labels = [label("work", ["contoso", "billing"]), label("contoso.backend", ["billing"], { repository: "contoso" })];
  assert.deepEqual(shape(projectRailGroups(projects, 0, labels)), ["work@0:billing", "repository:contoso@1:contoso", "contoso.backend@2:billing", "other:solo"]);
});

test("a sub-project whose repository is not listed is a project like the others", () => {
  assert.deepEqual(projectRailGroups([sub("billing", "contoso")], 0, []), []);
  assert.deepEqual(projectTitle(sub("billing", "contoso")), { repository: "CONTOSO", name: "billing" });
  assert.deepEqual(projectTitle(project("solo")), { repository: null, name: "solo" });
});

test("in the one list, a sub-project follows its repository, and one the user hid is left out", () => {
  const snapshot = { configured: true, projects: [sub("admin", "contoso"), project("contoso"), project("beta"), { ...sub("old", "contoso"), archived: true }],
    sessions: [], projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false };
  assert.deepEqual(projectRailProjection(snapshot, "name").projects.map(value => value.id), ["beta", "contoso", "admin"]);
});

test("while the list is filtered, a repository whose projects do not match is left out", () => {
  const projects = [project("solo")];
  assert.deepEqual(shape(projectRailGroups(projects, 0, [label("contoso.backend", ["billing"], { repository: "contoso" })], false)), []);
});
