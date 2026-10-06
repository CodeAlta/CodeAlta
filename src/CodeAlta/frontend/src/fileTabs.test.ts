import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { WorkspaceSnapshot } from "#neoastra";
import { activateFileTab, changesTab, closeFileTab, cycleTab, editorTab, emptyFileTabs, fileNodeId, isChangesTab, isEditorTab, fileTabKey, fileTabLimit, openFileTab,
  persistFileTabs, reconcileFileTabs, reopenTabKind, resolveFileTab, restoreFileTabs, restoreLegacyFiles, sameFileTab, type FileTab } from "./fileTabs";
import { FileTabLabel } from "./SessionTabStrip";
import { ShellLanguageContext } from "./shellLanguage";
import { locales, translate } from "./localization";

const editor = (projectId = "p"): FileTab => editorTab({ id: projectId, path: `/${projectId}` });
const changes = (projectId = "p"): FileTab => changesTab({ id: projectId, path: `/${projectId}` });
const names = (tabs: readonly FileTab[]) => tabs.map(tab => `${tab.view}:${tab.projectId}`);
const catalog: WorkspaceSnapshot = { configured: true, projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false,
  projects: [{ id: "p", name: "Project", path: "/p", archived: false }, { id: "old", name: "Old", path: "/old", archived: true }], sessions: [] };

test("a project has one editor tab and one changes tab; the node id comes from the identity", () => {
  assert.deepEqual(editor(), { projectId: "p", projectPath: "/p", view: "editor" });
  assert.deepEqual(changes(), { projectId: "p", projectPath: "/p", view: "changes" });
  assert.equal(fileTabKey(editor()), '["p","editor"]');
  assert.equal(fileNodeId(changes()), 'file:["p","changes"]');
  assert.notEqual(fileNodeId(editor()), fileNodeId(editor("q")));
  assert.notEqual(fileNodeId(editor()), fileNodeId(changes()));
  assert.ok(isEditorTab(editor()) && !isEditorTab(changes()) && isChangesTab(changes()) && !isChangesTab(editor()));
  assert.ok(sameFileTab(editor(), { ...editor(), projectPath: "/moved" }));
  assert.ok(!sameFileTab(editor(), null) && sameFileTab(null, null) && !sameFileTab(editor(), changes()));
});

test("opening activates an existing tab instead of adding one; closing remembers the tab and leaves none active", () => {
  let state = openFileTab(openFileTab(emptyFileTabs(), editor()), changes());
  assert.deepEqual(names(state.open), ["editor:p", "changes:p"]);
  assert.ok(sameFileTab(state.active, changes()));
  const again = openFileTab(state, editor());
  assert.deepEqual(again.open, state.open);
  assert.equal(again.active, state.open[0]);
  assert.equal(openFileTab(again, editor()), again);
  state = closeFileTab(again, editor());
  assert.deepEqual(names(state.open), ["changes:p"]);
  assert.equal(state.active, null);
  assert.deepEqual(names(state.closed), ["editor:p"]);
  assert.equal(closeFileTab(state, editor("missing")), state);
  const inactive = closeFileTab(openFileTab(state, editor("q")), changes());
  assert.ok(sameFileTab(inactive.active, editor("q")));
  const reopened = openFileTab(inactive, inactive.closed.at(-1)!);
  assert.ok(sameFileTab(reopened.active, changes()));
  assert.deepEqual(names(reopened.closed), ["editor:p"]);
});

test("activation only names an open tab; null returns to the session selection", () => {
  const state = openFileTab(openFileTab(emptyFileTabs(), editor()), changes());
  assert.equal(activateFileTab(state, changes()), state);
  assert.equal(activateFileTab(state, editor("zzz")), state);
  assert.equal(activateFileTab(state, editor()).active, state.open[0]);
  const none = activateFileTab(state, null);
  assert.equal(none.active, null);
  assert.equal(activateFileTab(none, null), none);
  assert.deepEqual(none.open, state.open);
});

test("at the limit the oldest inactive tab without unsaved edits makes room; held tabs are never evicted", () => {
  let state = emptyFileTabs();
  for (let index = 0; index < fileTabLimit; index++) state = openFileTab(state, editor(`f${index}`));
  const next = openFileTab(state, editor("extra"), tab => tab.projectId === "f0");
  assert.equal(next.open.length, fileTabLimit);
  assert.ok(next.open.some(tab => tab.projectId === "f0") && !next.open.some(tab => tab.projectId === "f1"));
  assert.ok(sameFileTab(next.active, editor("extra")));
  assert.equal(openFileTab(state, editor("extra"), () => true), state);
});

test("restore reconciles against the catalog: a missing, archived or moved project drops its tabs", () => {
  assert.ok(resolveFileTab(catalog, editor()) && resolveFileTab(catalog, changes()));
  assert.equal(resolveFileTab(catalog, editor("old")), undefined);
  assert.equal(resolveFileTab(catalog, { ...editor(), projectPath: "/elsewhere" }), undefined);
  assert.equal(resolveFileTab({ ...catalog, projects: [...catalog.projects, catalog.projects[0]] }, editor()), undefined);
  const state = { open: [editor(), editor("gone"), changes("old")], active: editor("gone"), closed: [changes("gone")] };
  assert.deepEqual(reconcileFileTabs(state, catalog), { open: [editor()], active: null, closed: [] });
  const kept = openFileTab(emptyFileTabs(), editor());
  assert.equal(reconcileFileTabs(kept, catalog), kept);
});

test("persisted tabs round-trip; malformed, duplicate, oversized or foreign values restore nothing", () => {
  const state = openFileTab(openFileTab(emptyFileTabs(), editor()), changes());
  let stored = "";
  assert.equal(persistFileTabs(value => { stored = value; }, closeFileTab(openFileTab(state, editor("q")), editor("q"))), true);
  assert.deepEqual(restoreFileTabs(() => stored), { ...state, active: null, closed: [] });
  persistFileTabs(value => { stored = value; }, state);
  assert.deepEqual(restoreFileTabs(() => stored), { ...state, closed: [] });
  assert.equal(restoreLegacyFiles(() => stored).size, 0);
  assert.equal(persistFileTabs(() => { throw new Error("denied"); }, state), false);
  const json = (value: unknown) => () => JSON.stringify(value);
  for (const read of [() => null, () => "", () => "{", () => { throw new Error("denied"); }, () => "x".repeat(200000),
    json(null), json({ version: 2, open: [], active: null }), json({ version: 1, open: {}, active: null }),
    json({ version: 1, open: [editor(), editor()], active: null }), json({ version: 1, open: [changes(), changes()], active: null }),
    json({ version: 1, open: [editor()], active: changes() }), json({ version: 1, open: [{ projectId: "p", view: "editor" }], active: null }),
    json({ version: 1, open: [{ ...editor(), view: "history" }], active: null }), json({ version: 1, open: [{ ...changes(), path: "a.ts" }], active: null }),
    json({ version: 1, open: [{ ...editor(), projectId: 4 }], active: null }), json({ version: 1, open: [{ projectId: "p", projectPath: "/p" }], active: null }),
    json({ version: 1, open: Array.from({ length: fileTabLimit + 1 }, (_value, index) => editor(`f${index}`)), active: null })]) {
    assert.equal(restoreFileTabs(read), null);
    assert.equal(restoreLegacyFiles(read).size, 0);
  }
  assert.deepEqual(restoreFileTabs(json({ version: 1, open: [{ ...editor(), extra: true, path: "" }], active: null })), { open: [editor()], active: null, closed: [] });
});

test("the tabs that each held one file become the editor of their project, with those files", () => {
  const file = (path: string, projectId = "p") => ({ projectId, projectPath: `/${projectId}`, path });
  const stored = () => JSON.stringify({ version: 1, active: file("src/b.ts"),
    open: [file("a.md"), { ...changes(), path: "" }, file("src/b.ts"), file("c.ts", "q")] });

  // One editor per project, where the first of its files was; the active file's project keeps the front.
  assert.deepEqual(restoreFileTabs(stored), { open: [editor(), changes(), editor("q")], active: editor(), closed: [] });
  assert.deepEqual([...restoreLegacyFiles(stored)], [["p", ["a.md", "src/b.ts"]], ["q", ["c.ts"]]]);
  // A stored editor tab beside files of the same project is the same tab.
  assert.deepEqual(restoreFileTabs(() => JSON.stringify({ version: 1, open: [file("a.md"), file("b.md")], active: null }))?.open, [editor()]);
});

test("reopen follows the order in which session and project tabs were closed", () => {
  assert.equal(reopenTabKind(["session", "file"], 1, 1), "file");
  assert.equal(reopenTabKind(["file", "session"], 1, 1), "session");
  assert.equal(reopenTabKind(["session", "file"], 1, 0), "session");
  assert.equal(reopenTabKind([], 0, 2), "file");
  assert.equal(reopenTabKind([], 3, 0), "session");
  assert.equal(reopenTabKind(["file"], 0, 0), null);
});

test("next and previous tab walk one ring: the new-session tab, the sessions, then the tabs of projects", () => {
  assert.deepEqual(cycleTab(2, 1, { kind: "draft" }, 1), { kind: "session", index: 0 });
  assert.deepEqual(cycleTab(2, 1, { kind: "session", index: 1 }, 1), { kind: "file", index: 0 });
  assert.deepEqual(cycleTab(2, 1, { kind: "file", index: 0 }, 1), { kind: "draft" });
  assert.deepEqual(cycleTab(2, 1, { kind: "draft" }, -1), { kind: "file", index: 0 });
  assert.deepEqual(cycleTab(0, 2, { kind: "file", index: 0 }, -1), { kind: "draft" });
  assert.deepEqual(cycleTab(0, 2, { kind: "file", index: 0 }, 1), { kind: "file", index: 1 });
  assert.deepEqual(cycleTab(1, 0, { kind: "session", index: 0 }, 1), { kind: "draft" });
});

test("an editor tab is labeled with its project and marks unsaved edits in every language; a changes tab holds none", () => {
  for (const locale of locales) {
    const render = (tab: FileTab, dirty: boolean) => renderToStaticMarkup(createElement(ShellLanguageContext.Provider,
      { value: { locale, choice: locale, setLanguage: () => assert.fail("rendering must not dispatch") } },
      createElement(FileTabLabel, { tab, project: "<Project>", dirty })));
    const html = render(editor(), true);
    assert.ok(html.includes(translate(locale, "Editor")) && html.includes("&lt;Project&gt;"), html);
    assert.ok(html.includes(translate(locale, "Unsaved changes")), html);
    assert.ok(!render(editor(), false).includes("session-tab-dirty"));
    const changed = render(changes(), true);
    assert.ok(changed.includes(translate(locale, "Changes")) && changed.includes("&lt;Project&gt;"), changed);
    assert.ok(!changed.includes("session-tab-dirty"), "The changes hold no edit.");
  }
});
