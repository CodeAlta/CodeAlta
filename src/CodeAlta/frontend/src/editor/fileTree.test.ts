import assert from "node:assert/strict";
import test from "node:test";
import type { ProjectFileFolder } from "#neoastra";
import { absoluteTreePath, applyTreeListing, collapseTree, emptyFileTree, expandTreeFolders, joinTreePath, parentTreePath, removeTreePath, renameTreePath, toggleTreeFolder, treeAncestors,
  treeBaseName, treeDecorations, treeEntryRows, treeNameProblem, treeQueries, treeQueryLimit, treeRows, treeTypeAhead, type FileTree, type TreeEntry } from "./fileTree";

const entry = (name: string, ignored = false): TreeEntry => name.endsWith("/") ? { name: name.slice(0, -1), directory: true, ignored } : { name, directory: false, ignored };
const listed = (path: string, revision: string, ...names: string[]): ProjectFileFolder =>
  ({ path, status: "ok", revision, entries: names.map(name => entry(name)), truncated: false });
const answer = (path: string, status: string): ProjectFileFolder => ({ path, status, revision: null, entries: [], truncated: false });
const shown = (tree: FileTree) => treeRows(tree).map(row => row.kind === "entry" ? `${"  ".repeat(row.depth)}${row.name}${row.directory ? row.expanded ? "/ v" : "/" : ""}${row.loading ? " …" : ""}`
  : `${"  ".repeat(row.depth)}<${row.kind === "note" ? row.note : "input"}>`);

test("paths are joined, split and walked from the project folder down", () => {
  assert.deepEqual([joinTreePath("", "a.ts"), joinTreePath("src", "a.ts")], ["a.ts", "src/a.ts"]);
  assert.deepEqual([parentTreePath("a.ts"), parentTreePath("src/deep/a.ts")], ["", "src/deep"]);
  assert.deepEqual([treeBaseName("a.ts"), treeBaseName("src/deep/a.ts")], ["a.ts", "a.ts"]);
  assert.deepEqual([treeAncestors("a.ts"), treeAncestors("a/b/c.ts")], [[], ["a", "a/b"]]);
});

test("the full path of an entry is written as its root folder is", () => {
  assert.deepEqual([absoluteTreePath("C:\\code\\app", "src/deep/a.ts"), absoluteTreePath("C:\\code\\app\\", "a.ts"), absoluteTreePath("C:\\", "a.ts")],
    ["C:\\code\\app\\src\\deep\\a.ts", "C:\\code\\app\\a.ts", "C:\\a.ts"]);
  assert.deepEqual([absoluteTreePath("/home/me/app", "src/a.ts"), absoluteTreePath("/home/me/app/", "a.ts"), absoluteTreePath("/", "a.ts")],
    ["/home/me/app/src/a.ts", "/home/me/app/a.ts", "/a.ts"]);
  // The folder itself has the path it was given.
  assert.deepEqual([absoluteTreePath("C:\\code\\app", ""), absoluteTreePath("/home/me/app", "")], ["C:\\code\\app", "/home/me/app"]);
});

test("a folder is read when it is opened: only the project folder and the open folders under it are asked for", () => {
  let tree = applyTreeListing(emptyFileTree, [listed("", "r1", "docs/", "src/", "readme.md")]);
  assert.deepEqual(treeQueries(emptyFileTree), [{ path: "", knownRevision: null }]);
  assert.deepEqual(treeQueries(tree), [{ path: "", knownRevision: "r1" }]);
  assert.deepEqual(shown(tree), ["docs/", "src/", "readme.md"]);

  tree = toggleTreeFolder(tree, "src");
  assert.deepEqual(shown(tree), ["docs/", "src/ v …", "readme.md"]);
  assert.deepEqual(treeQueries(tree), [{ path: "", knownRevision: "r1" }, { path: "src", knownRevision: null }]);
  tree = applyTreeListing(tree, [answer("", "unchanged"), listed("src", "s1", "deep/", "app.ts")]);
  assert.deepEqual(shown(tree), ["docs/", "src/ v", "  deep/", "  app.ts", "readme.md"]);

  // A folder open under a closed one is kept open for later, and not asked for meanwhile.
  tree = toggleTreeFolder(toggleTreeFolder(tree, "src/deep"), "src");
  assert.deepEqual(shown(tree), ["docs/", "src/", "readme.md"]);
  assert.deepEqual(treeQueries(tree).map(query => query.path), [""]);
  tree = toggleTreeFolder(tree, "src");
  assert.deepEqual(treeQueries(tree).map(query => query.path), ["", "src", "src/deep"]);
  assert.deepEqual(shown(tree), ["docs/", "src/ v", "  deep/ v …", "  app.ts", "readme.md"]);
  assert.equal(collapseTree(tree).expanded.size, 0);
  assert.equal(collapseTree(emptyFileTree), emptyFileTree);
});

test("an answer that changes nothing keeps the tree; a folder that is gone closes with what it held", () => {
  let tree = expandTreeFolders(applyTreeListing(emptyFileTree, [listed("", "r1", "src/", "a.ts")]), ["src", "src/deep"]);
  tree = applyTreeListing(tree, [listed("src", "s1", "deep/", "b.ts"), listed("src/deep", "d1", "c.ts")]);
  assert.equal(applyTreeListing(tree, [answer("", "unchanged"), answer("src", "unchanged"), answer("src/deep", "unchanged")]), tree);
  assert.equal(expandTreeFolders(tree, ["src", ""]), tree);

  // Deleted elsewhere: the parent no longer names it.
  const renamed = applyTreeListing(tree, [listed("", "r2", "source/", "a.ts"), answer("src", "not_found"), answer("src/deep", "not_found")]);
  assert.deepEqual(shown(renamed), ["source/", "a.ts"]);
  assert.deepEqual([[...renamed.folders.keys()], [...renamed.expanded]], [[""], []]);
  // Only the inner folder went away.
  const inner = applyTreeListing(tree, [answer("", "unchanged"), listed("src", "s2", "b.ts"), answer("src/deep", "not_found")]);
  assert.deepEqual([shown(inner), [...inner.expanded]], [["src/ v", "  b.ts", "a.ts"], ["src"]]);
});

test("a folder that cannot be read says so and keeps what it showed", () => {
  let tree = expandTreeFolders(applyTreeListing(emptyFileTree, [listed("", "r1", "locked/", "src/")]), ["locked", "src"]);
  tree = applyTreeListing(tree, [answer("locked", "read_failed"), listed("src", "s1", "a.ts")]);
  assert.deepEqual(shown(tree), ["locked/ v", "  <failed>", "src/ v", "  a.ts"]);
  assert.equal(applyTreeListing(tree, [answer("locked", "read_failed")]), tree);
  const failing = applyTreeListing(tree, [answer("src", "read_failed")]);
  assert.deepEqual([shown(failing), failing.folders.get("src")?.revision], [shown(tree), null]);
  assert.equal(applyTreeListing(failing, [listed("src", "s1", "a.ts")]).folders.get("src")?.failure, null);
  // The project folder itself is never forgotten.
  assert.equal(applyTreeListing(tree, [answer("", "not_found")]).folders.get("")?.failure, "not_found");
  const more = applyTreeListing(emptyFileTree, [{ ...listed("", "r1", "a.ts"), truncated: true }]);
  assert.deepEqual(shown(more), ["a.ts", "<truncated>"]);
  const many = expandTreeFolders(emptyFileTree, Array.from({ length: treeQueryLimit + 20 }, (_value, index) => `f${index}`));
  assert.equal(treeQueries(many).length, treeQueryLimit);
});

test("a typed name has its row: a new entry at the top of its folder, a renamed one in its place", () => {
  let tree = expandTreeFolders(applyTreeListing(emptyFileTree, [listed("", "r1", "src/", "a.ts")]), ["src"]);
  tree = applyTreeListing(tree, [listed("src", "s1", "b.ts")]);
  assert.deepEqual(treeRows(tree, { parent: "", directory: false, path: null }).map(row => row.kind), ["input", "entry", "entry", "entry"]);
  const inner = treeRows(tree, { parent: "src", directory: true, path: null });
  assert.deepEqual(inner.map(row => [row.kind, row.depth]), [["entry", 0], ["input", 1], ["entry", 1], ["entry", 0]]);
  const renaming = treeRows(tree, { parent: "src", directory: false, path: "src/b.ts" });
  assert.deepEqual(renaming.map(row => row.kind === "input" ? `input:${row.path}` : row.kind), ["entry", "input:src/b.ts", "entry"]);
  assert.deepEqual(treeEntryRows(renaming).map(row => row.path), ["src", "a.ts"]);
});

test("what is ignored stays marked under an ignored folder, and letters jump to the next name that starts with them", () => {
  let tree = expandTreeFolders(applyTreeListing(emptyFileTree, [{ path: "", status: "ok", revision: "r", truncated: false,
    entries: [entry("bin/", true), entry("src/"), entry("app.ts"), entry("apple.ts"), entry("banana.ts")] }]), ["bin"]);
  tree = applyTreeListing(tree, [listed("bin", "b", "out.dll")]);
  const rows = treeEntryRows(treeRows(tree));
  assert.deepEqual(rows.map(row => [row.path, row.ignored]), [["bin", true], ["bin/out.dll", true], ["src", false], ["app.ts", false], ["apple.ts", false], ["banana.ts", false]]);
  assert.equal(treeTypeAhead(rows, null, "a"), "app.ts");
  assert.equal(treeTypeAhead(rows, "app.ts", "a"), "apple.ts");
  assert.equal(treeTypeAhead(rows, "apple.ts", "A"), "app.ts");
  assert.equal(treeTypeAhead(rows, "app.ts", "appl"), "apple.ts");
  assert.equal(treeTypeAhead(rows, "apple.ts", "appl"), "apple.ts");
  assert.equal(treeTypeAhead(rows, "app.ts", "o"), "bin/out.dll");
  assert.equal(treeTypeAhead(rows, "app.ts", "z"), null);
  assert.equal(treeTypeAhead([], null, "a"), null);
});

test("a rename keeps what was read and open under the entry; a delete forgets it", () => {
  let tree = expandTreeFolders(applyTreeListing(emptyFileTree, [listed("", "r1", "src/", "a.ts")]), ["src", "src/deep"]);
  tree = applyTreeListing(tree, [listed("src", "s1", "deep/"), listed("src/deep", "d1", "c.ts")]);
  const renamed = renameTreePath(tree, "src", "lib");
  assert.deepEqual([[...renamed.folders.keys()], [...renamed.expanded]], [["", "lib", "lib/deep"], ["lib", "lib/deep"]]);
  const removed = removeTreePath(tree, "src/deep");
  assert.deepEqual([[...removed.folders.keys()], [...removed.expanded]], [["", "src"], ["src"]]);
});

test("a typed name is checked before it is sent", () => {
  const siblings = [entry("src/"), entry("Readme.md")];
  const problem = (name: string, current: string | null = null, nested = true) => treeNameProblem(name, siblings, current, nested);
  assert.equal(problem("new.ts"), null);
  assert.equal(problem("deep/er/new.ts"), null);
  assert.equal(problem("src/inside.ts"), null, "An existing folder can receive the new file.");
  assert.equal(problem("  "), "A name is required.");
  for (const name of ["a:b", "a*b", "what?", "a|b", "<a>", "a\tb", "..", ".", "a//b", "a/../b", "x".repeat(300)]) assert.equal(problem(name), "This name is not valid.", name);
  for (const name of [" a.ts", "a.ts ", "a.", "a/ b/c"]) assert.equal(problem(name), "A name cannot start or end with a space, or end with a dot.", name);
  assert.equal(problem("readme.MD"), "A file or folder with this name already exists.");
  assert.equal(problem("SRC"), "A file or folder with this name already exists.");
  assert.equal(problem("Readme.md/inside.ts"), "A file or folder with this name already exists.", "A file is not a folder.");
  // Renaming: the entry may keep its name in another case, and stays in its folder.
  assert.equal(problem("README.md", "Readme.md", false), null);
  assert.equal(problem("src", "Readme.md", false), "A file or folder with this name already exists.");
  assert.equal(problem("sub/name.md", "Readme.md", false), "A name cannot contain a slash.");
  assert.equal(problem("sub\\name.md", "Readme.md", false), "A name cannot contain a slash.");
});

test("git statuses are those of the project's own files; a folder is marked when it holds a change", () => {
  const changes = [{ path: "src/app/a.ts", status: "modified" }, { path: "src/new.ts", status: "untracked" }, { path: "gone.ts", status: "deleted" }];
  const whole = treeDecorations(changes, "");
  assert.deepEqual([[...whole.files], [...whole.folders]], [[["src/app/a.ts", "modified"], ["src/new.ts", "untracked"]], ["src", "src/app"]]);
  // The project is the folder "src" of the work tree.
  const inner = treeDecorations([...changes, { path: "srcx/b.ts", status: "added" }, { path: "docs/c.md", status: "modified" }], "src/");
  assert.deepEqual([[...inner.files], [...inner.folders]], [[["app/a.ts", "modified"], ["new.ts", "untracked"]], ["app"]]);
});
