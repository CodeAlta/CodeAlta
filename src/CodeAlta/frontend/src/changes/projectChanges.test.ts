import assert from "node:assert/strict";
import test from "node:test";
import { changeBar, changeCommitsReply, changeContent, changeContentNotice, changeDiffLimit, changeHistoryHeight, changeLetter, changeLineHeight, changeListReply,
  changeListRows, changeListWidth, changeScopeKey, changesViewLabel, changesViews, changeTreeRows, defaultChangesPreferences, estimatedDiffHeight, filterChanges,
  fittedDiffHeight, orderChanges, persistChangesPreferences, projectRelativePath, restoreChangesPreferences, sectionAt, selectedChange,
  type ChangedFile } from "./projectChanges";

const file = (path: string, values: Partial<ChangedFile> = {}): ChangedFile =>
  ({ path, originalPath: null, status: "modified", insertions: 1, deletions: 0, binary: false, revision: `r-${path}`, ...values });
const wire = (files: unknown[], values: Record<string, unknown> = {}) => ({ status: "ok", projectId: "p", revision: "rev", root: "C:\\repo", prefix: "", branch: "main",
  detached: false, comparison: "head", baseReference: null, baseAhead: null, files, insertions: 3, deletions: 1, truncated: false, ...values });

test("a list answer is taken whole for the asked project, with its files and the base of its branch", () => {
  const reply = changeListReply(wire([file("src/a.ts", { insertions: 3, deletions: 1 }), file("b.bin", { binary: true, insertions: null, deletions: null, status: "untracked" }),
    file("new.ts", { status: "renamed", originalPath: "old.ts" })], { baseReference: "origin/main", baseAhead: 2 }), "p", null);
  assert.equal(reply.kind, "list");
  if (reply.kind !== "list") return;
  assert.deepEqual(reply.list.files.map(value => [value.path, value.status, value.insertions, value.binary, value.originalPath]),
    [["src/a.ts", "modified", 3, false, null], ["b.bin", "untracked", null, true, null], ["new.ts", "renamed", 1, false, "old.ts"]]);
  assert.deepEqual([reply.list.branch, reply.list.comparison, reply.list.baseReference, reply.list.baseAhead, reply.list.insertions, reply.list.deletions],
    ["main", "head", "origin/main", 2, 3, 1]);
  // A base is a reference with its count, or nothing.
  const partial = changeListReply(wire([], { baseReference: "origin/main", baseAhead: null }), "p", null);
  assert.deepEqual(partial.kind === "list" && [partial.list.baseReference, partial.list.baseAhead], [null, null]);
  assert.equal(changeListReply(wire([], { comparison: "commit" }), "p", null).kind, "list");
});

test("the same list is answered with unchanged only for the revision that was named; refusals keep their status", () => {
  assert.deepEqual(changeListReply({ status: "unchanged", projectId: "p", revision: "rev" }, "p", "rev"), { kind: "unchanged" });
  assert.deepEqual(changeListReply({ status: "unchanged", projectId: "p", revision: "other" }, "p", "rev"), { kind: "failed", status: "read_failed" });
  assert.deepEqual(changeListReply({ status: "unchanged", projectId: "p", revision: "rev" }, "p", null), { kind: "failed", status: "read_failed" });
  assert.deepEqual(changeListReply({ status: "unchanged", projectId: "q", revision: "rev" }, "p", "rev"), { kind: "failed", status: "read_failed" });
  for (const status of ["not_repository", "git_failed", "unknown_project", "stale_epoch"])
    assert.deepEqual(changeListReply({ status }, "p", null), { kind: "failed", status });
  assert.deepEqual(changeListReply({ status: "<b>" }, "p", null), { kind: "failed", status: "read_failed" });
});

test("a list that is not well formed is a failed read, never a partial one", () => {
  const good = file("a.ts");
  for (const reply of [null, "ok", {}, wire([good], { projectId: "q" }), wire([good], { revision: "" }), wire([good], { branch: "" }), wire([good], { comparison: "index" }),
    wire([good], { insertions: -1 }), wire([good], { truncated: "no" }), wire("files" as never), wire([good, good]), wire([{ ...good, path: "/rooted" }]),
    wire([{ ...good, path: "a/../b" }]), wire([{ ...good, path: "a//b" }]), wire([{ ...good, path: "a\nb" }]), wire([{ ...good, status: "staged" }]),
    wire([{ ...good, originalPath: "../x" }]), wire([{ ...good, insertions: 1.5 }]), wire([{ ...good, binary: 1 }]), wire([{ ...good, revision: null }]), wire([null])])
    assert.deepEqual(changeListReply(reply, "p", null), { kind: "failed", status: "read_failed" });
});

test("the contents of a file are taken for the asked file; a side is a text or the reason it has none", () => {
  const reply = { status: "ok", projectId: "p", path: "a.ts", revision: "r", original: "one\n", originalState: "text", modified: null, modifiedState: "absent" };
  assert.deepEqual(changeContent(reply, "p", "a.ts"), { path: "a.ts", revision: "r", original: "one\n", originalState: "text", modified: "", modifiedState: "absent" });
  assert.equal(changeContentNotice(changeContent(reply, "p", "a.ts") as never), null);
  const binary = changeContent({ ...reply, original: null, originalState: "binary", modified: "x", modifiedState: "text" }, "p", "a.ts");
  assert.equal(typeof binary === "object" && changeContentNotice(binary), "binary");
  const large = changeContent({ ...reply, modified: null, modifiedState: "too_large" }, "p", "a.ts");
  assert.equal(typeof large === "object" && changeContentNotice(large), "too_large");
  assert.equal(changeContent({ status: "not_changed" }, "p", "a.ts"), "not_changed");
  for (const bad of [null, { ...reply, path: "b.ts" }, { ...reply, projectId: "q" }, { ...reply, original: null }, { ...reply, modified: "text", modifiedState: "absent" },
    { ...reply, originalState: "html" }, { ...reply, revision: "" }, { status: "No" }])
    assert.equal(changeContent(bad, "p", "a.ts"), "read_failed");
});

test("the history is the commits of the asked project, or nothing at all", () => {
  const commit = { id: "a".repeat(40), shortId: "aaaaaaa", author: "Ada", time: "2026-10-06T07:00:00.0000000Z", subject: "Fix it" };
  const reply = { status: "ok", projectId: "p", revision: "h", commits: [commit, { ...commit, id: "b".repeat(64), subject: "" }], more: true };
  const read = changeCommitsReply(reply, "p", null);
  assert.ok(read && read !== "unchanged");
  assert.deepEqual([read.revision, read.more, read.commits.map(value => value.subject)], ["h", true, ["Fix it", ""]]);
  assert.equal(changeCommitsReply({ status: "unchanged", projectId: "p", revision: "h" }, "p", "h"), "unchanged");
  for (const bad of [null, { ...reply, projectId: "q" }, { status: "unchanged", projectId: "p", revision: "h" }, { ...reply, more: 1 },
    { ...reply, commits: [{ ...commit, id: "A".repeat(40) }] }, { ...reply, commits: [{ ...commit, id: "abc" }] }, { ...reply, commits: [{ ...commit, time: "yesterday" }] },
    { ...reply, commits: [{ ...commit, author: 7 }] }, { status: "not_repository", projectId: "p", revision: "h" }])
    assert.equal(changeCommitsReply(bad, "p", null), null);
  assert.equal(changeScopeKey({ kind: "head" }), "head");
  assert.equal(changeScopeKey({ kind: "commit", id: "abc" }), "commit:abc");
});

test("the tree puts folders before files, joins a chain of single folders and leaves out what a collapsed folder holds", () => {
  const files = [file("README.md"), file("src/app/views/b.tsx", { insertions: 4, deletions: 2 }), file("src/app/views/a.tsx"), file("src/app/main.ts"),
    file("docs/guide/intro.md"), file("Zeta.txt")];
  const describe = (collapsed: string[] = []) => changeTreeRows(files, new Set(collapsed)).map(row => row.kind === "folder"
    ? `${"  ".repeat(row.depth)}[${row.name}] ${row.files} +${row.insertions} -${row.deletions}${row.collapsed ? " closed" : ""}` : `${"  ".repeat(row.depth)}${row.name}`);
  assert.deepEqual(describe(), ["[docs/guide] 1 +1 -0", "  intro.md", "[src/app] 3 +6 -2", "  [views] 2 +5 -2", "    a.tsx", "    b.tsx", "  main.ts", "README.md", "Zeta.txt"]);
  assert.deepEqual(describe(["src/app"]), ["[docs/guide] 1 +1 -0", "  intro.md", "[src/app] 3 +6 -2 closed", "README.md", "Zeta.txt"]);
  // The flat list and the keyboard follow the order of the tree.
  assert.deepEqual(orderChanges(files).map(value => value.path),
    ["docs/guide/intro.md", "src/app/views/a.tsx", "src/app/views/b.tsx", "src/app/main.ts", "README.md", "Zeta.txt"]);
  assert.deepEqual(changeListRows(files.slice(1, 2)).map(row => row.kind === "file" && [row.name, row.folder, row.depth]), [["b.tsx", "src/app/views", 0]]);
});

test("the filter keeps the files whose path has every word; letters and bars summarize a file", () => {
  const files = [file("src/App.tsx"), file("src/app.test.ts"), file("docs/app.md")];
  assert.deepEqual(filterChanges(files, " APP  src ").map(value => value.path), ["src/App.tsx", "src/app.test.ts"]);
  assert.equal(filterChanges(files, "  "), files);
  assert.deepEqual(filterChanges(files, "nothing"), []);
  assert.deepEqual((["modified", "added", "deleted", "renamed", "copied", "conflicted", "untracked"] as const).map(changeLetter), ["M", "A", "D", "R", "C", "!", "U"]);
  assert.deepEqual(changeBar(null, null), { added: 0, removed: 0 });
  assert.deepEqual(changeBar(1, 0), { added: 1, removed: 0 });
  assert.deepEqual(changeBar(0, 3), { added: 0, removed: 1 });
  assert.deepEqual(changeBar(500, 500), { added: 3, removed: 2 });
  assert.deepEqual(changeBar(999, 1), { added: 4, removed: 1 }, "A removal is never rounded away.");
  assert.deepEqual(changeBar(1, 999), { added: 1, removed: 4 }, "Nor an addition.");
});

test("the selection stays on its file, or moves to the one that took its place", () => {
  const before = [file("a"), file("b"), file("c"), file("d")];
  assert.equal(selectedChange([], before, null), "a");
  assert.equal(selectedChange(before, before, "c"), "c");
  assert.equal(selectedChange(before, [file("a"), file("d")], "b"), "d", "The next file that is still listed.");
  assert.equal(selectedChange(before, [file("a"), file("b")], "d"), "b", "The last one when nothing follows.");
  assert.equal(selectedChange(before, [file("x")], "unknown"), "x");
  assert.equal(selectedChange(before, [], "a"), null);
});

test("a changed file is opened by its path inside the project, when it is inside", () => {
  assert.equal(projectRelativePath("", "src/a.ts"), "src/a.ts");
  assert.equal(projectRelativePath("packages/app/", "packages/app/src/a.ts"), "src/a.ts");
  assert.equal(projectRelativePath("packages/app/", "packages/other/a.ts"), null);
  assert.equal(projectRelativePath("packages/app/", "packages/app/"), null);
});

test("the layout of the tab is kept; a stored value that is not one restores the default", () => {
  let stored = "";
  const value = { ...defaultChangesPreferences, layout: "list" as const, view: "all" as const, sideBySide: false, autoRefresh: false, listWidth: 333, historyHeight: 120 };
  assert.equal(persistChangesPreferences(text => { stored = text; }, value), true);
  assert.deepEqual(restoreChangesPreferences(() => stored), value);
  assert.equal(persistChangesPreferences(() => { throw new Error("denied"); }, value), false);
  for (const read of [() => null, () => "{", () => "null", () => "x".repeat(2000), () => { throw new Error("denied"); }])
    assert.deepEqual(restoreChangesPreferences(read), defaultChangesPreferences);
  assert.deepEqual(restoreChangesPreferences(() => JSON.stringify({ layout: "grid", view: "every", wrap: "yes", listWidth: 5000, historyHeight: 1 })),
    { ...defaultChangesPreferences, listWidth: 560, historyHeight: 64 });
  // One file at a time unless all of them were chosen: what was kept before the choice existed says nothing about it.
  assert.equal(defaultChangesPreferences.view, "file");
  assert.equal(restoreChangesPreferences(() => JSON.stringify({ layout: "list", sideBySide: false })).view, "file");
  assert.deepEqual(changesViews.map(view => [view, changesViewLabel(view)]), [["file", "One file at a time"], ["all", "All files in one view"]]);
  assert.deepEqual([changeListWidth(10), changeListWidth(300.4), changeHistoryHeight(5000)], [180, 300, 900]);
});

test("in the view of all files a diff takes the height of its content, and a long one the height of the view", () => {
  assert.equal(fittedDiffHeight(312.4, 600), 313);
  assert.equal(fittedDiffHeight(changeDiffLimit, 600), changeDiffLimit);
  // Longer than what is shown whole: it scrolls by itself in what the view shows at once, never in less than a few lines.
  assert.equal(fittedDiffHeight(changeDiffLimit + 1, 600.7), 600);
  assert.equal(fittedDiffHeight(changeDiffLimit + 1, 0), 12 * changeLineHeight);
  assert.equal(fittedDiffHeight(changeDiffLimit + 1, 10 * changeDiffLimit), changeDiffLimit);
  assert.deepEqual([fittedDiffHeight(-5, 600), fittedDiffHeight(Number.NaN, 600)], [0, 0]);

  // Before it is read, a file stands for its changed lines and some lines around them; a new or a deleted file for all its lines.
  assert.equal(estimatedDiffHeight(file("a.ts", { insertions: 10, deletions: 2 }), 600), 12 + 20 * changeLineHeight);
  assert.equal(estimatedDiffHeight(file("new.ts", { status: "untracked", insertions: 40, deletions: 0 }), 600), 12 + 40 * changeLineHeight);
  assert.equal(estimatedDiffHeight(file("gone.ts", { status: "deleted", insertions: 0, deletions: 7 }), 600), 12 + 7 * changeLineHeight);
  assert.equal(estimatedDiffHeight(file("b.bin", { binary: true, insertions: null, deletions: null }), 600), 12 + 2 * changeLineHeight);
  assert.equal(estimatedDiffHeight(file("huge.json", { status: "added", insertions: 50_000 }), 600), 600);
});

test("the file at the top of the view of all files is the last one that starts at or above where the view is", () => {
  const tops = [0, 100, 250, 900];
  const at = (offset: number) => sectionAt(tops.length, index => tops[index], offset);
  assert.deepEqual([at(0), at(99), at(100), at(249.5), at(250), at(899), at(5000)], [0, 0, 1, 1, 2, 2, 3]);
  // Above the first one (a view pulled past its start) it is still the first; without sections there is none.
  assert.equal(at(-20), 0);
  assert.equal(sectionAt(0, () => 0, 10), -1);
});
