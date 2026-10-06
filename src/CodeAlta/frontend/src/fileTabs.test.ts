import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { WorkspaceSnapshot } from "#neoastra";
import { activateFileTab, changesTab, closeFileTab, cycleTab, emptyFileTabs, fileNodeId, isChangesTab, fileTabKey, fileTabLimit, fileTabName, openFileTab, persistFileTabs,
  reconcileFileTabs, reopenTabKind, resolveFileTab, restoreFileTabs, sameFileTab, type FileTab } from "./fileTabs";
import { FileTabLabel } from "./SessionTabStrip";
import { ShellLanguageContext } from "./shellLanguage";
import { locales, translate } from "./localization";

const file = (path: string, projectId = "p"): FileTab => ({ projectId, projectPath: `/${projectId}`, path });
const catalog: WorkspaceSnapshot = { configured: true, projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false,
  projects: [{ id: "p", name: "Project", path: "/p", archived: false }, { id: "old", name: "Old", path: "/old", archived: true }], sessions: [] };

test("a file has one tab per project and path; its node id and name come from the identity", () => {
  assert.equal(fileTabKey(file("src/a.ts")), '["p","src/a.ts"]');
  assert.equal(fileNodeId(file("src/a.ts")), 'file:["p","src/a.ts"]');
  assert.notEqual(fileNodeId(file("src/a.ts")), fileNodeId(file("src/a.ts", "q")));
  assert.equal(fileTabName(file("src/deep/a.ts")), "a.ts");
  assert.equal(fileTabName(file("README.md")), "README.md");
  assert.ok(sameFileTab(file("a"), { ...file("a"), projectPath: "/moved" }));
  assert.ok(!sameFileTab(file("a"), null) && sameFileTab(null, null));
});

test("opening activates an existing tab instead of adding one; closing remembers the tab and leaves no file active", () => {
  let state = openFileTab(openFileTab(emptyFileTabs(), file("a")), file("b"));
  assert.deepEqual(state.open.map(tab => tab.path), ["a", "b"]);
  assert.equal(state.active?.path, "b");
  const again = openFileTab(state, file("a"));
  assert.deepEqual(again.open, state.open);
  assert.equal(again.active, state.open[0]);
  assert.equal(openFileTab(again, file("a")), again);
  state = closeFileTab(again, file("a"));
  assert.deepEqual(state.open.map(tab => tab.path), ["b"]);
  assert.equal(state.active, null);
  assert.deepEqual(state.closed.map(tab => tab.path), ["a"]);
  assert.equal(closeFileTab(state, file("missing")), state);
  const inactive = closeFileTab(openFileTab(state, file("c")), file("b"));
  assert.equal(inactive.active?.path, "c");
  const reopened = openFileTab(inactive, inactive.closed.at(-1)!);
  assert.equal(reopened.active?.path, "b");
  assert.deepEqual(reopened.closed.map(tab => tab.path), ["a"]);
});

test("activation only names an open tab; null returns to the session selection", () => {
  const state = openFileTab(openFileTab(emptyFileTabs(), file("a")), file("b"));
  assert.equal(activateFileTab(state, file("b")), state);
  assert.equal(activateFileTab(state, file("zzz")), state);
  assert.equal(activateFileTab(state, file("a")).active, state.open[0]);
  const none = activateFileTab(state, null);
  assert.equal(none.active, null);
  assert.equal(activateFileTab(none, null), none);
  assert.deepEqual(none.open, state.open);
});

test("at the limit the oldest inactive tab without unsaved edits makes room; held tabs are never evicted", () => {
  let state = emptyFileTabs();
  for (let index = 0; index < fileTabLimit; index++) state = openFileTab(state, file(`f${index}`));
  const next = openFileTab(state, file("extra"), tab => tab.path === "f0");
  assert.equal(next.open.length, fileTabLimit);
  assert.ok(next.open.some(tab => tab.path === "f0") && !next.open.some(tab => tab.path === "f1"));
  assert.equal(next.active?.path, "extra");
  assert.equal(openFileTab(state, file("extra"), () => true), state);
});

test("restore reconciles against the catalog: a missing, archived or moved project drops its tabs", () => {
  assert.ok(resolveFileTab(catalog, file("a")));
  assert.equal(resolveFileTab(catalog, file("a", "old")), undefined);
  assert.equal(resolveFileTab(catalog, { ...file("a"), projectPath: "/elsewhere" }), undefined);
  assert.equal(resolveFileTab({ ...catalog, projects: [...catalog.projects, catalog.projects[0]] }, file("a")), undefined);
  const state = { open: [file("a"), file("b", "gone"), file("c", "old")], active: file("b", "gone"), closed: [file("d", "gone")] };
  assert.deepEqual(reconcileFileTabs(state, catalog), { open: [file("a")], active: null, closed: [] });
  const kept = openFileTab(emptyFileTabs(), file("a"));
  assert.equal(reconcileFileTabs(kept, catalog), kept);
});

test("persisted tabs round-trip; malformed, duplicate, oversized or foreign values restore nothing", () => {
  const state = openFileTab(openFileTab(emptyFileTabs(), file("a")), file("src/b.ts"));
  let stored = "";
  assert.equal(persistFileTabs(value => { stored = value; }, closeFileTab(openFileTab(state, file("c")), file("c"))), true);
  assert.deepEqual(restoreFileTabs(() => stored), { ...state, active: null, closed: [] });
  persistFileTabs(value => { stored = value; }, state);
  assert.deepEqual(restoreFileTabs(() => stored), { ...state, closed: [] });
  assert.equal(persistFileTabs(() => { throw new Error("denied"); }, state), false);
  const json = (value: unknown) => () => JSON.stringify(value);
  for (const read of [() => null, () => "", () => "{", () => { throw new Error("denied"); }, () => "x".repeat(200000),
    json(null), json({ version: 2, open: [], active: null }), json({ version: 1, open: {}, active: null }),
    json({ version: 1, open: [file("a"), file("a")], active: null }), json({ version: 1, open: [file("a")], active: file("b") }),
    json({ version: 1, open: [{ projectId: "p", path: "a" }], active: null }), json({ version: 1, open: [{ ...file("a"), path: "" }], active: null }),
    json({ version: 1, open: [{ ...file("a"), projectId: 4 }], active: null }),
    json({ version: 1, open: Array.from({ length: fileTabLimit + 1 }, (_value, index) => file(`f${index}`)), active: null })])
    assert.equal(restoreFileTabs(read), null);
  assert.deepEqual(restoreFileTabs(json({ version: 1, open: [{ ...file("a"), extra: true }], active: null })), { open: [file("a")], active: null, closed: [] });
});

test("reopen follows the order in which session and file tabs were closed", () => {
  assert.equal(reopenTabKind(["session", "file"], 1, 1), "file");
  assert.equal(reopenTabKind(["file", "session"], 1, 1), "session");
  assert.equal(reopenTabKind(["session", "file"], 1, 0), "session");
  assert.equal(reopenTabKind([], 0, 2), "file");
  assert.equal(reopenTabKind([], 3, 0), "session");
  assert.equal(reopenTabKind(["file"], 0, 0), null);
});

test("next and previous tab walk one ring: the new-session tab, the sessions, then the files", () => {
  assert.deepEqual(cycleTab(2, 1, { kind: "draft" }, 1), { kind: "session", index: 0 });
  assert.deepEqual(cycleTab(2, 1, { kind: "session", index: 1 }, 1), { kind: "file", index: 0 });
  assert.deepEqual(cycleTab(2, 1, { kind: "file", index: 0 }, 1), { kind: "draft" });
  assert.deepEqual(cycleTab(2, 1, { kind: "draft" }, -1), { kind: "file", index: 0 });
  assert.deepEqual(cycleTab(0, 2, { kind: "file", index: 0 }, -1), { kind: "draft" });
  assert.deepEqual(cycleTab(0, 2, { kind: "file", index: 0 }, 1), { kind: "file", index: 1 });
  assert.deepEqual(cycleTab(1, 0, { kind: "session", index: 0 }, 1), { kind: "draft" });
});

test("a file tab label shows the file name, keeps the path literal in its tooltip and marks unsaved edits in every language", () => {
  const tab = file("src/<b>&name.ts");
  for (const locale of locales) {
    const render = (dirty: boolean) => renderToStaticMarkup(createElement(ShellLanguageContext.Provider,
      { value: { locale, choice: locale, setLanguage: () => assert.fail("rendering must not dispatch") } },
      createElement(FileTabLabel, { tab, project: "Project", dirty })));
    const html = render(true);
    assert.ok(html.includes(">&lt;b&gt;&amp;name.ts</span>"), html);
    assert.ok(html.includes("src/&lt;b&gt;&amp;name.ts\nProject"), html);
    assert.ok(html.includes(translate(locale, "Unsaved changes")), html);
    assert.ok(!render(false).includes("session-tab-dirty"));
  }
});

test("the changes of a project have one tab of their own, which is kept like a file tab", () => {
  const changes = changesTab({ id: "p", path: "/p" });
  assert.deepEqual(changes, { projectId: "p", projectPath: "/p", path: "", view: "changes" });
  assert.ok(isChangesTab(changes) && !isChangesTab(file("a")));
  assert.equal(fileTabKey(changes), '["p","","changes"]');
  assert.notEqual(fileNodeId(changes), fileNodeId(changesTab({ id: "q", path: "/q" })));
  // Opening them again activates the tab; they close, reopen and survive a restart like a file.
  let state = openFileTab(openFileTab(openFileTab(emptyFileTabs(), file("a")), changes), file("b"));
  state = openFileTab(state, changesTab({ id: "p", path: "/p" }));
  assert.equal(state.open.length, 3);
  assert.ok(sameFileTab(state.active, changes));
  assert.ok(resolveFileTab(catalog, changes));
  let stored = "";
  persistFileTabs(value => { stored = value; }, state);
  assert.deepEqual(restoreFileTabs(() => stored), { open: [file("a"), changes, file("b")], active: changes, closed: [] });
  assert.deepEqual(reconcileFileTabs(openFileTab(emptyFileTabs(), changesTab({ id: "old", path: "/old" })), catalog).open, [], "An archived project has no tab.");
  const json = (value: unknown) => () => JSON.stringify(value);
  for (const read of [json({ version: 1, open: [{ ...changes, path: "a.ts" }], active: null }), json({ version: 1, open: [{ ...changes, view: "history" }], active: null }),
    json({ version: 1, open: [changes, changes], active: null })])
    assert.equal(restoreFileTabs(read), null);
});

test("a changes tab is labeled with its project in every language", () => {
  for (const locale of locales) {
    const html = renderToStaticMarkup(createElement(ShellLanguageContext.Provider,
      { value: { locale, choice: locale, setLanguage: () => assert.fail("rendering must not dispatch") } },
      createElement(FileTabLabel, { tab: changesTab({ id: "p", path: "/p" }), project: "<Project>", dirty: true })));
    assert.ok(html.includes(translate(locale, "Changes")) && html.includes("&lt;Project&gt;"), html);
    assert.ok(!html.includes("session-tab-dirty"), "The changes hold no edit.");
  }
});
