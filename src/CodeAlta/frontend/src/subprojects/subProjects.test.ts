import assert from "node:assert/strict";
import test from "node:test";
import type { SubProjectEntryView, SubProjectLabelView, SubProjectsMapView } from "#neoastra";
import { addableCandidates, entryChanges, entryDraft, entryName, entryState, filterEntries, keyFrom, mapLabelPath, normalizeFolder, parentChoices, validKey } from "./subProjects";

const entry = (key: string, look: Partial<SubProjectEntryView> = {}): SubProjectEntryView => ({ key, path: `services/${key}`, name: null, description: null, labels: [],
  optional: false, write: "repository", folder: "ok", submodule: false, projectId: `id-${key}`, projectName: null, hidden: false, claimedBy: null, subProjects: 0, ...look });
const label = (key: string, parent: string | null = null): SubProjectLabelView => ({ key, id: `contoso.${key}`, name: key.toUpperCase(), parent, icon: null, color: null, description: null });

test("an entry is shown, hidden, optional or unavailable, and named after its project, the map or its folder", () => {
  assert.equal(entryState(entry("a")), "shown");
  assert.equal(entryState(entry("a", { hidden: true })), "hidden");
  assert.equal(entryState(entry("a", { projectId: null, optional: true })), "optional");
  assert.equal(entryState(entry("a", { folder: "missing", projectId: null })), "missing");
  assert.equal(entryState(entry("a", { folder: "outside", projectId: null })), "outside");
  assert.equal(entryState(entry("a", { claimedBy: "other", projectId: null })), "elsewhere");
  assert.equal(entryName(entry("billing")), "billing");
  assert.equal(entryName(entry("billing", { name: "Billing" })), "Billing");
  assert.equal(entryName(entry("billing", { name: "Billing", projectName: "Invoices" })), "Invoices", "The name the user gave wins.");
});

test("the filter looks at names, folders, keys and the paths of labels", () => {
  const labels = [label("backend"), label("payments", "backend")];
  const entries = [entry("billing", { labels: ["payments"] }), entry("web", { path: "apps/web" })];
  assert.deepEqual(filterEntries(entries, labels, "apps").map(value => value.key), ["web"]);
  assert.deepEqual(filterEntries(entries, labels, "backend").map(value => value.key), ["billing"]);
  assert.deepEqual(filterEntries(entries, labels, "  ").map(value => value.key), ["billing", "web"]);
  assert.equal(mapLabelPath("payments", labels), "BACKEND › PAYMENTS");
});

test("a form says only what changed", () => {
  const value = entry("billing", { name: "Billing", labels: ["a"] });
  assert.equal(entryChanges(value, entryDraft(value)), null);
  assert.deepEqual(entryChanges(value, { ...entryDraft(value), name: " Billing ", path: "services\\billing/" }), null, "Spaces and separators are not changes.");
  assert.deepEqual(entryChanges(value, { ...entryDraft(value), name: "", folderOnly: true, labels: ["a", "b"] }),
    { name: "", path: null, description: null, labels: ["a", "b"], optional: null, write: "folder" });
});

test("a label goes under none of its own, nor deeper than four levels", () => {
  const labels = [label("a"), label("b", "a"), label("c", "b"), label("d", "c"), label("x")];
  assert.deepEqual(parentChoices("b", labels).map(value => value.key), ["a", "x"]);
  assert.deepEqual(parentChoices(null, labels).map(value => value.key), ["a", "b", "c", "x"], "A new label goes under one of the first three levels.");
});

test("keys and folders are written as the map writes them", () => {
  assert.equal(keyFrom("apps/My App"), "my-app");
  assert.equal(keyFrom("日本"), "project");
  assert.ok(validKey("legacy-reports"));
  assert.ok(!validKey("Bad"));
  assert.equal(normalizeFolder(" ./libs\\core/ "), "libs/core");
});

test("a folder declared already is not offered again", () => {
  const map = { entries: [entry("billing")] } as unknown as SubProjectsMapView;
  const candidates = [{ path: "services/billing", key: "billing", markers: [], submodule: false, declared: null },
    { path: "services/web", key: "web", markers: [], submodule: false, declared: null }, { path: "x", key: "x", markers: [], submodule: false, declared: "x" }];
  assert.deepEqual(addableCandidates(candidates, map).map(value => value.path), ["services/web"]);
});
