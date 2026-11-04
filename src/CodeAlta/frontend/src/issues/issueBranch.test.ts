import assert from "node:assert/strict";
import test from "node:test";
import { branchSchemeProblem, branchSlug, branchType, defaultBranchScheme, isBranchName, isTypeText, readOwnBranch, suggestBranchName, templateProblem, writeOwnBranch } from "./issueBranch";

const item = (change: { id?: string; title?: string; type?: string | null; labels?: string[] } = {}) => ({
  id: change.id ?? "12345", title: change.title ?? "Export the list as CSV", type: change.type ?? null,
  labels: (change.labels ?? []).map(name => ({ name, color: null })),
});

test("the branch of an issue is its type, its number and the words of its title", () => {
  assert.equal(suggestBranchName(item()), "feature/12345-export-the-list-as-csv");
  assert.equal(suggestBranchName(item({ type: "Bug", title: "Crash on start" })), "fix/12345-crash-on-start");
  assert.equal(suggestBranchName(item({ type: "User Story" })), "feature/12345-export-the-list-as-csv");
  // A label says it is a bug where the tracker has no type, as on GitHub.
  assert.equal(suggestBranchName(item({ labels: ["ui", "bug"], title: "Crash" })), "fix/12345-crash");
  // A key of Jira stays as it is written.
  assert.equal(suggestBranchName(item({ id: "PROJ-12", title: "Login" })), "feature/PROJ-12-login");
  assert.equal(suggestBranchName(item({ title: "!!!" })), "feature/12345");
});

test("the words of a title are plain, short and cut at a whole word", () => {
  assert.equal(branchSlug("Größe der Übersicht ändern"), "groesse-der-uebersicht-aendern");
  assert.equal(branchSlug("Café: crème brûlée"), "cafe-creme-brulee");
  assert.equal(branchSlug("Fix   the [parser] (again) -- now"), "fix-the-parser-again-now");
  assert.equal(branchSlug("one two three four five six seven eight nine ten"), "one-two-three-four-five-six-seven-eight");
  assert.ok(branchSlug("a".repeat(60)).length <= 40);
});

test("the scheme of the project names the branch", () => {
  const scheme = { template: "{type}/{id}_{title}", titleLength: 12, types: [{ name: "User Story", value: "story" }, { name: "bug", value: "bugfix" }, { name: "*", value: "task" }] };
  assert.equal(suggestBranchName(item({ type: "User Story" }), scheme), "story/12345_export-the");
  // The names of the types are compared without case, and a label counts where the type does not.
  assert.equal(suggestBranchName(item({ type: "BUG", title: "Crash" }), scheme), "bugfix/12345_crash");
  assert.equal(suggestBranchName(item({ labels: ["Bug"], title: "Crash" }), scheme), "bugfix/12345_crash");
  assert.equal(suggestBranchName(item({ type: "Epic", title: "Crash" }), scheme), "task/12345_crash");
  // Without `*`, the others are features.
  assert.equal(suggestBranchName(item({ type: "Epic", title: "Crash" }), { ...scheme, types: [] }), "feature/12345_crash");
  assert.equal(suggestBranchName(item({ title: "Crash" }), { ...scheme, template: "users/bwi/{id}-{title}" }), "users/bwi/12345-crash");
  // An empty part leaves no separator behind.
  assert.equal(suggestBranchName(item({ title: "Crash" }), { ...scheme, template: "{type}/{id}-{title}", types: [{ name: "*", value: "" }] }), "12345-crash");
});

test("a placeholder the template should not hold stays, and is no branch", () => {
  const name = suggestBranchName(item({ title: "Crash" }), { template: "{type}/{nummer}-{title}", titleLength: 40, types: [] });
  assert.equal(name, "feature/{nummer}-crash");
  assert.equal(isBranchName(name), false);
});

test("only a bug is a fix", () => {
  assert.equal(branchType({ type: "Defect", labels: [] }), "fix");
  assert.equal(branchType({ type: "Task", labels: [{ name: "enhancement", color: null }] }), "feature");
});

test("a name git refuses is not a branch", () => {
  for (const name of ["feature/12345-export", "hotfix/1-a", "fix/PROJ-12_login.v2"]) assert.equal(isBranchName(name), true, name);
  for (const name of ["", "-x", "feature/a b", "feature/", "/feature", "feature//a", "feature/.a", "feature/a.lock", "feature/a.lock/b", "a..b", "a@{b", "a:b", "a~b", "HEAD", "refs/heads/a", "a."])
    assert.equal(isBranchName(name), false, name);
});

test("a template holds only type, id and title, in closed braces", () => {
  assert.equal(templateProblem("{type}/{id}-{title}"), null);
  assert.equal(templateProblem("users/bwi/{id}"), null);
  assert.deepEqual(templateProblem("{type}/{nummer}"), { key: "The template has an unknown placeholder: {name}. It can hold type, id and title.", values: { name: "{nummer}" } });
  assert.equal(templateProblem("{type}/{id")?.key, "A brace of the template is not closed.");
  assert.equal(templateProblem("type}/{id}")?.key, "A brace of the template is not opened.");
  assert.equal(templateProblem("  ")?.key, "The template is empty.");
});

test("a scheme is saved only when the host can read it back", () => {
  assert.equal(branchSchemeProblem(defaultBranchScheme), null);
  assert.equal(branchSchemeProblem({ ...defaultBranchScheme, titleLength: 0 })?.key, "The longest title is a number from 1 to 120.");
  assert.equal(branchSchemeProblem({ ...defaultBranchScheme, titleLength: 2.5 })?.key, "The longest title is a number from 1 to 120.");
  assert.deepEqual(branchSchemeProblem({ ...defaultBranchScheme, types: [{ name: "Bug", value: "fix" }, { name: "bug ", value: "hotfix" }] }),
    { key: "The type {type} is there twice.", values: { type: "bug" } });
  assert.equal(branchSchemeProblem({ ...defaultBranchScheme, types: [{ name: " ", value: "fix" }] })?.key, "A type has no name.");
  assert.equal(branchSchemeProblem({ ...defaultBranchScheme, types: [{ name: "Bug", value: "fix it" }] })?.key, "The text for {type} is not a part of a branch name.");
  const many = Array.from({ length: 63 }, (_, index) => ({ name: `T${index}`, value: "x" }));
  assert.equal(branchSchemeProblem({ ...defaultBranchScheme, types: [...many, { name: "*", value: "feature" }] }), null, "64 rows, the row of all others included");
  assert.equal(branchSchemeProblem({ ...defaultBranchScheme, types: [...many, { name: "T63", value: "x" }, { name: "*", value: "feature" }] })?.key, "There are more than 64 types.");
  assert.equal(branchSchemeProblem({ ...defaultBranchScheme, types: [{ name: "a".repeat(65), value: "x" }] })?.key, "The name of a type is longer than 64 characters, or holds a control character.");
  assert.equal(branchSchemeProblem({ ...defaultBranchScheme, types: [{ name: "Bug\tFix", value: "x" }] })?.key, "The name of a type is longer than 64 characters, or holds a control character.");
  for (const text of ["fix", "", "users/bwi", "v1.2_x"]) assert.equal(isTypeText(text), true, text);
  for (const text of ["fix it", ".x", "/x", "x/", "a..b", "x:y"]) assert.equal(isTypeText(text), false, text);
});

test("the choice of a branch of its own is kept by project, and nothing is chosen until the user does", () => {
  const values = new Map<string, string>();
  const storage = { getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => void values.set(key, value) };
  assert.equal(readOwnBranch("p1", storage), null);
  writeOwnBranch("p1", true, storage);
  writeOwnBranch("p2", false, storage);
  assert.deepEqual([readOwnBranch("p1", storage), readOwnBranch("p2", storage), readOwnBranch("p3", storage)], [true, false, null]);
  values.set("codealta.desktop.issue-own-branch.v1", "not json");
  assert.equal(readOwnBranch("p1", storage), null, "A storage that does not parse chooses nothing.");
});
