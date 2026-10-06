import assert from "node:assert/strict";
import test from "node:test";
import { activateEditorFile, adoptLegacyFiles, closeEditorFile, closeEditorFiles, cycleEditorFile, defaultEditorPreferences, editorFileLimit, editorSideWidth, editorStorageKey, editorTabNames,
  emptyEditorFiles, emptyStoredEditor, moveEditorFile, movedPath, openEditorFile, pinEditorFile, removeEditorPath, renameEditorPath, restoreEditorStorage, storedEditor,
  underPath, updateEditorStorage, type EditorFiles } from "./editorWorkbench";

const paths = (state: EditorFiles) => state.open.map(file => file.preview ? `${file.path}?` : file.path);
const open = (...names: string[]) => names.reduce((state, name) => openEditorFile(state, name), emptyEditorFiles);

test("a file opens beside the one shown, and an open file is shown again instead of opened twice", () => {
  let state = open("a.ts", "b.ts", "c.ts");
  assert.deepEqual([paths(state), state.active, state.recent], [["a.ts", "b.ts", "c.ts"], "c.ts", ["c.ts", "b.ts", "a.ts"]]);
  state = activateEditorFile(state, "a.ts");
  state = openEditorFile(state, "d.ts");
  assert.deepEqual([paths(state), state.active], [["a.ts", "d.ts", "b.ts", "c.ts"], "d.ts"]);
  const again = openEditorFile(state, "b.ts");
  assert.deepEqual([paths(again), again.active, again.recent[0]], [paths(state), "b.ts", "b.ts"]);
  assert.equal(openEditorFile(again, "b.ts"), again);
  assert.equal(activateEditorFile(again, "b.ts"), again);
  assert.equal(activateEditorFile(again, "missing.ts"), again);
});

test("a previewed file takes over the preview tab until it is pinned", () => {
  let state = openEditorFile(open("a.ts"), "b.ts", true);
  assert.deepEqual(paths(state), ["a.ts", "b.ts?"]);
  state = openEditorFile(state, "c.ts", true);
  assert.deepEqual([paths(state), state.active, state.recent], [["a.ts", "c.ts?"], "c.ts", ["c.ts", "a.ts"]]);
  // Opening it for good, or pinning it, keeps it; the next preview gets a tab of its own.
  assert.deepEqual(paths(openEditorFile(state, "c.ts")), ["a.ts", "c.ts"]);
  state = pinEditorFile(state, "c.ts");
  assert.equal(pinEditorFile(state, "c.ts"), state);
  state = openEditorFile(state, "d.ts", true);
  assert.deepEqual(paths(state), ["a.ts", "c.ts", "d.ts?"]);
  // Previewing a pinned file shows it and leaves it pinned; a preview tab that is held is not replaced.
  assert.deepEqual(paths(openEditorFile(state, "a.ts", true)), ["a.ts", "c.ts", "d.ts?"]);
  assert.deepEqual(paths(openEditorFile(state, "e.ts", true, path => path === "d.ts")), ["a.ts", "c.ts", "d.ts?", "e.ts?"]);
});

test("closing the file shown shows the one shown before it, or else its neighbor", () => {
  let state = activateEditorFile(open("a.ts", "b.ts", "c.ts", "d.ts"), "b.ts");
  state = closeEditorFile(state, "b.ts");
  assert.deepEqual([paths(state), state.active], [["a.ts", "c.ts", "d.ts"], "d.ts"]);
  assert.equal(closeEditorFile(state, "missing.ts"), state);
  // An inactive file closes without changing what is shown.
  assert.equal(closeEditorFile(state, "a.ts").active, "d.ts");
  const last = closeEditorFiles(state, ["a.ts", "c.ts", "d.ts"]);
  assert.deepEqual([last.open, last.active, last.recent], [[], null, []]);
  // Without a history the neighbor on the right, then the last tab.
  const plain: EditorFiles = { open: state.open, active: "c.ts", recent: [] };
  assert.equal(closeEditorFile(plain, "c.ts").active, "d.ts");
  assert.equal(closeEditorFile({ ...plain, active: "d.ts" }, "d.ts").active, "c.ts");
});

test("tabs move within the strip and cycle around it", () => {
  const state = open("a.ts", "b.ts", "c.ts");
  assert.deepEqual(paths(moveEditorFile(state, "c.ts", 0)), ["c.ts", "a.ts", "b.ts"]);
  assert.deepEqual(paths(moveEditorFile(state, "a.ts", 99)), ["b.ts", "c.ts", "a.ts"]);
  assert.equal(moveEditorFile(state, "b.ts", 1), state);
  assert.equal(moveEditorFile(state, "missing.ts", 0), state);
  assert.deepEqual([cycleEditorFile(state, 1), cycleEditorFile(state, -1)], ["a.ts", "b.ts"]);
  assert.equal(cycleEditorFile(open("a.ts"), 1), null);
  assert.equal(cycleEditorFile(emptyEditorFiles, -1), null);
});

test("open files follow a rename or a move, and close with what was deleted", () => {
  assert.ok(underPath("src/a.ts", "src") && underPath("src", "src") && !underPath("source/a.ts", "src") && !underPath("src", "src/a.ts"));
  assert.deepEqual([movedPath("src/a.ts", "src", "lib"), movedPath("src", "src", "lib"), movedPath("source/a.ts", "src", "lib")], ["lib/a.ts", "lib", "source/a.ts"]);
  const state = activateEditorFile(open("src/a.ts", "src/deep/b.ts", "readme.md"), "src/deep/b.ts");
  const folder = renameEditorPath(state, "src", "lib");
  assert.deepEqual([paths(folder), folder.active, folder.recent], [["lib/a.ts", "lib/deep/b.ts", "readme.md"], "lib/deep/b.ts", ["lib/deep/b.ts", "readme.md", "lib/a.ts"]]);
  const file = renameEditorPath(state, "readme.md", "docs/guide.md");
  assert.deepEqual(paths(file), ["src/a.ts", "src/deep/b.ts", "docs/guide.md"]);
  assert.equal(renameEditorPath(state, "other", "x"), state);
  const removed = removeEditorPath(state, "src");
  assert.deepEqual([paths(removed), removed.active], [["readme.md"], "readme.md"]);
  assert.equal(removeEditorPath(state, "nothing"), state);
});

test("at the limit the file shown longest ago makes room, unless it holds unsaved edits", () => {
  let state = emptyEditorFiles;
  for (let index = 0; index < editorFileLimit; index++) state = openEditorFile(state, `f${index}.ts`);
  const next = openEditorFile(state, "extra.ts", false, path => path === "f0.ts");
  assert.equal(next.open.length, editorFileLimit);
  assert.ok(next.open.some(file => file.path === "f0.ts") && !next.open.some(file => file.path === "f1.ts"));
  assert.deepEqual([next.active, next.recent.includes("f1.ts"), next.recent.length], ["extra.ts", false, editorFileLimit]);
  assert.equal(openEditorFile(state, "extra.ts", false, () => true), state);
});

test("a name several tabs share is followed by its folder", () => {
  const names = editorTabNames(open("src/index.ts", "lib/index.ts", "index.ts", "readme.md").open);
  assert.deepEqual([...names], [["src/index.ts", { name: "index.ts", folder: "src" }], ["lib/index.ts", { name: "index.ts", folder: "lib" }],
    ["index.ts", { name: "index.ts", folder: "." }], ["readme.md", { name: "readme.md", folder: null }]]);
});

test("each project's editor and the preferences are stored without undoing what another editor wrote", () => {
  assert.equal(editorStorageKey, "codealta.desktop.editor.v1");
  let stored: string | null = null;
  const read = () => stored, write = (value: string) => { stored = value; };
  assert.deepEqual(restoreEditorStorage(read), { preferences: defaultEditorPreferences, projects: {} });
  assert.equal(storedEditor(restoreEditorStorage(read), "p"), emptyStoredEditor);

  assert.ok(updateEditorStorage(read, write, { projectId: "p", editor: { files: ["a.ts", "b.ts"], active: "b.ts", side: null, expanded: ["src"] } }));
  assert.ok(updateEditorStorage(read, write, { preferences: { width: 9999, wrap: false, minimap: true, ignored: true, previews: ["markdown"] } }));
  assert.ok(updateEditorStorage(read, write, { projectId: "q", editor: { files: [], active: null, side: "search", expanded: [] } }));
  const storage = restoreEditorStorage(read);
  assert.deepEqual(storage.preferences, { width: editorSideWidth(9999), wrap: false, minimap: true, ignored: true, previews: ["markdown"] });
  assert.deepEqual(storedEditor(storage, "p"), { files: ["a.ts", "b.ts"], active: "b.ts", side: null, expanded: ["src"] });
  assert.deepEqual(storedEditor(storage, "q"), { files: [], active: null, side: "search", expanded: [] });
  assert.deepEqual([editorSideWidth(10), editorSideWidth(300.4)], [170, 300]);

  // The project written last is the newest: the oldest ones go when there are too many.
  for (let index = 0; index < 30; index++) updateEditorStorage(read, write, { projectId: `n${index}`, editor: emptyStoredEditor });
  updateEditorStorage(read, write, { projectId: "n5", editor: { ...emptyStoredEditor, files: ["kept.ts"] } });
  const many = restoreEditorStorage(read);
  assert.equal(Object.keys(many.projects).length, 24);
  assert.ok(!("p" in many.projects) && !("n3" in many.projects) && many.projects.n5.files[0] === "kept.ts" && "n29" in many.projects);
  assert.equal(updateEditorStorage(read, () => { throw new Error("denied"); }, { preferences: defaultEditorPreferences }), false);
});

test("malformed storage restores the defaults, entry by entry", () => {
  const json = (value: unknown) => () => JSON.stringify(value);
  for (const read of [() => null, () => "", () => "{", () => { throw new Error("denied"); }, () => "x".repeat(300000), json(null), json([]), json({ version: 2, projects: { p: emptyStoredEditor } })])
    assert.deepEqual(restoreEditorStorage(read), { preferences: defaultEditorPreferences, projects: {} });
  assert.deepEqual(restoreEditorStorage(json({ version: 1, preferences: { previews: ["markdown", "pdf", 4, "svg"] } })).preferences.previews, ["svg", "markdown"]);
  assert.deepEqual(restoreEditorStorage(json({ version: 1, preferences: { previews: [] } })).preferences.previews, []);
  const storage = restoreEditorStorage(json({ version: 1, preferences: { width: "wide", wrap: "yes", minimap: true, previews: "all" }, projects: {
    good: { files: ["a.ts", "a.ts", "b.ts"], active: "missing.ts", side: "panel", expanded: ["src", "src"] },
    bad: { files: [4], expanded: [] }, worse: "text", "": emptyStoredEditor, empty: { files: [], active: "a.ts", expanded: [] } } }));
  assert.deepEqual(storage.preferences, { ...defaultEditorPreferences, minimap: true });
  assert.deepEqual(storage.projects, { good: { files: ["a.ts", "b.ts"], active: "a.ts", side: null, expanded: ["src"] }, empty: { files: [], active: null, side: null, expanded: [] } });
});

test("the files that were tabs of their own become the files of their project's editor, once", () => {
  let stored: string | null = null;
  const read = () => stored, write = (value: string) => { stored = value; };
  adoptLegacyFiles(read, write, new Map());
  assert.equal(stored, null);
  updateEditorStorage(read, write, { projectId: "busy", editor: { files: ["kept.ts"], active: "kept.ts", side: "files", expanded: [] } });
  updateEditorStorage(read, write, { projectId: "idle", editor: { files: [], active: null, side: "search", expanded: ["src"] } });
  adoptLegacyFiles(read, write, new Map([["new", ["a.md", "src/b.ts"]], ["busy", ["other.ts"]], ["idle", ["c.ts"]]]));
  const storage = restoreEditorStorage(read);
  // Opened on its files alone, the last one shown; an editor that has files is left as it is.
  assert.deepEqual(storage.projects.new, { files: ["a.md", "src/b.ts"], active: "src/b.ts", side: null, expanded: [] });
  assert.deepEqual(storage.projects.busy, { files: ["kept.ts"], active: "kept.ts", side: "files", expanded: [] });
  assert.deepEqual(storage.projects.idle, { files: ["c.ts"], active: "c.ts", side: null, expanded: ["src"] });
});
