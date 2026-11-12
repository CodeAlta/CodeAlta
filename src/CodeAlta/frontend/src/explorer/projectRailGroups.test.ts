import assert from "node:assert/strict";
import test from "node:test";
import type { WorkspaceProject } from "#neoastra";
import type { Label } from "../labels/labels";
import { projectRailGroups, projectRailProjection, projectTitle, repositoryProjectIds, subProjectWindows, type RailGroup } from "./projectRail";
import { sidebarFilterView } from "./sidebarFilter";

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

test("the sub-projects of an archived repository leave the Explorer with it, also those used", () => {
  const snapshot = { configured: true, projects: [{ ...project("contoso"), archived: true }, sub("billing", "contoso"), project("beta")],
    sessions: [], projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false };
  assert.deepEqual(projectRailProjection(snapshot, "name").projects.map(value => value.id), ["beta", "contoso"]);
});

// platform-map › billing › Apps › invoices-api: billing is a sub-project of platform-map with a map of its own (#433).
const nestedProjects = () => [project("platform"), sub("billing", "platform"), sub("checkout", "platform"), sub("invoices", "billing"), sub("ledger", "billing"),
  project("solo")];
const nestedLabels = () => [label("platform.repos", ["billing", "checkout"], { repository: "platform" }),
  label("billing.apps", ["invoices"], { repository: "billing" }), label("billing.libs", ["ledger"], { repository: "billing" })];

test("a sub-project with a map of its own is a group nested in its repository, under its label of the map, with its own labels", () => {
  const groups = projectRailGroups(nestedProjects(), 0, nestedLabels());
  assert.deepEqual(shape(groups), ["repository:platform@0:platform", "platform.repos@1:checkout", "repository:billing@2:billing",
    "billing.apps@3:invoices", "billing.libs@3:ledger", "other:solo"]);
  const [platform, , billing, apps] = groups;
  assert.ok(platform.kind === "label" && billing.kind === "label" && apps.kind === "label");
  assert.deepEqual(platform.label.projectIds, ["platform", "billing", "checkout", "invoices", "ledger"], "The group of the repository counts the nested ones.");
  assert.deepEqual(billing.ancestors, ["repository:platform", "platform.repos"], "Folding the repository or the label hides it.");
  assert.equal(billing.repository?.id, "billing");
  assert.deepEqual(apps.ancestors, ["repository:platform", "platform.repos", "repository:billing"]);
});

test("a nested repository without a label of the map comes right after the projects of its repository", () => {
  const groups = projectRailGroups(nestedProjects(), 0, [label("billing.apps", ["invoices"], { repository: "billing" })]);
  assert.deepEqual(shape(groups), ["repository:platform@0:platform,checkout", "repository:billing@1:billing,ledger", "billing.apps@2:invoices", "other:solo"]);
});

test("a nested repository whose repository is not listed is a group of its own", () => {
  const projects = nestedProjects().filter(value => value.id !== "platform");
  assert.deepEqual(shape(projectRailGroups(projects, 0, nestedLabels())), ["repository:billing@0:billing", "billing.apps@1:invoices", "billing.libs@1:ledger",
    "other:checkout,solo"]);
});

test("the sub-projects of a nested map follow their repositories in the one list, and leave with any repository hidden above them", () => {
  const snapshot = (projects: WorkspaceProject[]) => ({ configured: true, projects, sessions: [], projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false });
  assert.deepEqual(projectRailProjection(snapshot(nestedProjects()), "name").projects.map(value => value.id),
    ["platform", "billing", "invoices", "ledger", "checkout", "solo"]);
  const hidden = nestedProjects().map(value => value.id === "platform" ? { ...value, archived: true } : value);
  assert.deepEqual(projectRailProjection(snapshot(hidden), "name").projects.map(value => value.id), ["platform", "solo"]);
  assert.deepEqual([...repositoryProjectIds("platform", nestedProjects())], ["platform", "billing", "checkout", "invoices", "ledger"]);
});

test("the menu of a sub-project opens its own sub-projects and those of its repository", () => {
  const projects = nestedProjects();
  const byId = (id: string) => projects.find(value => value.id === id)!;
  assert.deepEqual(subProjectWindows(byId("platform")), ["own"]);
  assert.deepEqual(subProjectWindows(byId("billing")), ["own", "repository"]);
  // One that lists no sub-project has its own window too: its map may declare only optional folders, or be started there.
  assert.deepEqual(subProjectWindows(byId("checkout")), ["own", "repository"]);
  assert.deepEqual(subProjectWindows(byId("solo")), ["own"]);
});

test("while the list is filtered, a sub-project found keeps the repositories above it as a frame, unopened", () => {
  const snapshot = { configured: true, projects: nestedProjects(), sessions: [], projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false };
  const view = sidebarFilterView(snapshot, ["invoices"]);
  assert.deepEqual([...view.projects], ["invoices"]);
  assert.deepEqual([...view.frames ?? []].sort(), ["billing", "platform"]);
  const listed = nestedProjects().filter(value => view.projects.has(value.id) || view.frames?.has(value.id));
  assert.deepEqual(shape(projectRailGroups(listed, 0, nestedLabels(), false)), ["repository:platform@0:platform", "platform.repos@1:",
    "repository:billing@2:billing", "billing.apps@3:invoices"]);
});

test("while the list is filtered, a repository whose projects do not match is left out", () => {
  const projects = [project("solo")];
  assert.deepEqual(shape(projectRailGroups(projects, 0, [label("contoso.backend", ["billing"], { repository: "contoso" })], false)), []);
});
