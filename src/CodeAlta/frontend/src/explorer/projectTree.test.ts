import assert from "node:assert/strict";
import test from "node:test";
import { collapseAllScopes, emptyProjectTree, expandScope, globalScope, isExpanded, isFavorite, persistProjectTree, projectTreeLimit, restoreProjectTree,
  scopeKey, setFavorite, toggleScope, type ProjectTree } from "./projectTree";

test("scopes open and close one by one, and all close at once", () => {
  let tree = expandScope(expandScope(emptyProjectTree, "a"), null);
  assert.deepEqual(tree.expanded, ["a", globalScope]);
  assert.deepEqual([isExpanded(tree, "a"), isExpanded(tree, null), isExpanded(tree, "b")], [true, true, false]);
  // Opening what is open, or closing all of nothing, changes nothing: the same tree comes back.
  assert.equal(expandScope(tree, "a"), tree);
  assert.equal(collapseAllScopes(emptyProjectTree), emptyProjectTree);
  tree = toggleScope(tree, "b");
  tree = toggleScope(tree, "a");
  assert.deepEqual(tree.expanded, [globalScope, "b"]);
  tree = toggleScope(tree, null);
  assert.deepEqual(tree.expanded, ["b"]);
  assert.deepEqual(collapseAllScopes(setFavorite(tree, "b", true)), { expanded: [], favorites: ["b"] });
  assert.equal(scopeKey(null), globalScope);
});

test("a project is made a favorite once, and no longer one", () => {
  let tree = setFavorite(setFavorite(emptyProjectTree, "a", true), "b", true);
  assert.deepEqual(tree.favorites, ["a", "b"]);
  assert.equal(setFavorite(tree, "a", true), tree);
  assert.equal(setFavorite(tree, "missing", false), tree);
  assert.equal(setFavorite(tree, "", true), tree);
  tree = setFavorite(tree, "a", false);
  assert.deepEqual([tree.favorites, isFavorite(tree, "a"), isFavorite(tree, "b")], [["b"], false, true]);
  // What is open is not touched by a favorite.
  assert.deepEqual(setFavorite(expandScope(tree, "a"), "a", true), { expanded: ["a"], favorites: ["b", "a"] });
});

test("the newest ids are kept when a list is full", () => {
  let tree: ProjectTree = emptyProjectTree;
  for (let index = 0; index < projectTreeLimit + 3; index++) tree = setFavorite(expandScope(tree, `p${index}`), `p${index}`, true);
  assert.equal(tree.expanded.length, projectTreeLimit);
  assert.equal(tree.favorites.length, projectTreeLimit);
  assert.deepEqual([tree.expanded[0], tree.expanded.at(-1), tree.favorites[0]], ["p3", `p${projectTreeLimit + 2}`, "p3"]);
});

test("the tree is stored and read back; what cannot be read is no tree at all", () => {
  const tree = setFavorite(expandScope(expandScope(emptyProjectTree, "a"), null), "b", true);
  let stored = "";
  assert.equal(persistProjectTree(value => { stored = value; }, tree), true);
  assert.deepEqual(restoreProjectTree(() => stored), tree);
  assert.equal(persistProjectTree(() => { throw Error("denied"); }, tree), false);

  assert.equal(restoreProjectTree(() => null), null);
  for (const value of ["", "{", "[]", "null", "3", '"text"']) assert.equal(restoreProjectTree(() => value), null, value);
  assert.equal(restoreProjectTree(() => { throw Error("unavailable"); }), null);
  // A stored object is a tree, whatever else it holds: lists that are not lists are empty.
  assert.deepEqual(restoreProjectTree(() => '{"expanded":"a","favorites":{"a":1}}'), emptyProjectTree);
  assert.deepEqual(restoreProjectTree(() => "{}"), emptyProjectTree);
  // Only ids are kept, once each; the global sessions can be open but are no favorite.
  const long = "x".repeat(129);
  assert.deepEqual(restoreProjectTree(() => JSON.stringify({ expanded: ["a", 1, null, "a", "", long, ["b"]], favorites: ["", "b", "b", {}, long, "c"] })),
    { expanded: ["a", ""], favorites: ["b", "c"] });
  const many = Array.from({ length: projectTreeLimit + 20 }, (_, index) => `p${index}`);
  assert.equal(restoreProjectTree(() => JSON.stringify({ expanded: many, favorites: many }))!.favorites.length, projectTreeLimit);
});
