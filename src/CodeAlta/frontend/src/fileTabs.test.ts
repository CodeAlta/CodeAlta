import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { TerminalItem, WorkspaceSnapshot } from "#neoastra";
import { activateFileTab, automationsTab, canvasTab, isCanvasTab, refreshCanvasTab, isWorkItemsTab, workItemsTab, changesTab, closeFileTab, cycleTab, diskEditorTab, diskFolderPrefix, isDiskFolderTab, editorTab, emptyFileTabs, fileNodeId, isAutomationsTab, isChangesTab, isEditorTab, isFolderTab, isPluginTab, isReadOnlyTab, isSkillTab, isTerminalTab, fileTabKey, fileTabLimit, openFileTab,
  persistFileTabs, pluginEditorTab, pluginFolderPrefix, reconcileFileTabs, reconcileTerminalTabs, reopenTabKind, resolveFileTab, restoreFileTabs, restoreLegacyFiles, sameFileTab, skillEditorTab, skillFolderPrefix,
  skillReadOnly, terminalTab, type FileTab } from "./fileTabs";
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
    json({ version: 1, open: [editor()], active: changes() }),
    json({ version: 1, open: Array.from({ length: fileTabLimit + 1 }, (_value, index) => editor(`f${index}`)), active: null })]) {
    assert.equal(restoreFileTabs(read), null);
    assert.equal(restoreLegacyFiles(read).size, 0);
  }
  assert.deepEqual(restoreFileTabs(json({ version: 1, open: [{ ...editor(), extra: true, path: "" }], active: null })), { open: [editor()], active: null, closed: [] });
});

test("a stored tab that is not understood is left out, and the tabs beside it are restored", () => {
  const json = (value: unknown) => () => JSON.stringify(value);
  // A tab of a kind that a newer build stored, among the tabs this one knows.
  const canvas = { projectId: "p", projectPath: "/p", view: "board" };
  assert.deepEqual(restoreFileTabs(json({ version: 1, open: [editor(), canvas, changes(), automationsTab, workItemsTab], active: changes() })),
    { open: [editor(), changes(), automationsTab, workItemsTab], active: changes(), closed: [] });
  // It was the active one: no tab of a project is active, and the session selection shows.
  assert.deepEqual(restoreFileTabs(json({ version: 1, open: [editor(), canvas, changes()], active: canvas })), { open: [editor(), changes()], active: null, closed: [] });
  assert.deepEqual(restoreFileTabs(json({ version: 1, open: [canvas], active: canvas })), emptyFileTabs());
  // The files that were tabs of their own are kept beside it.
  const files = json({ version: 1, open: [{ projectId: "p", projectPath: "/p", path: "a.md" }, canvas, { projectId: "q", projectPath: "/q", path: "c.ts" }], active: null });
  assert.deepEqual(restoreFileTabs(files)?.open, [editor(), editor("q")]);
  assert.deepEqual([...restoreLegacyFiles(files)], [["p", ["a.md"]], ["q", ["c.ts"]]]);
  // So is a tab of a known kind that is not as this build stores it, and what is no tab at all.
  for (const tab of [{ projectId: "p", view: "editor" }, { ...editor(), view: "history" }, { ...changes(), path: "a.ts" }, { ...editor(), projectId: 4 },
    { projectId: "p", projectPath: "/p" }, null, 4, "editor", []]) {
    assert.deepEqual(restoreFileTabs(json({ version: 1, open: [tab], active: null })), emptyFileTabs(), JSON.stringify(tab));
    assert.deepEqual(restoreFileTabs(json({ version: 1, open: [changes(), tab, editor("q")], active: editor("q") })),
      { open: [changes(), editor("q")], active: editor("q"), closed: [] }, JSON.stringify(tab));
  }
});

const board = (key?: string) => canvasTab({ pluginKey: "builtin:board", canvasId: "board", key }, { title: "Board", icon: "list-checks", plugin: "plugin:global:board" });
const projectCanvas = (projectId = "p", canvasId = "notes") => canvasTab({ pluginKey: "k", canvasId, project: { id: projectId, path: `/${projectId}` } });
const sessionCanvas = (sessionId = "s1") => canvasTab({ pluginKey: "k", canvasId: "run", project: { id: "p", path: "/p" }, sessionId });

test("a canvas tab is identified by its plugin, its canvas, its project or session and its key, not by its title", () => {
  assert.deepEqual(board(), { projectId: "", projectPath: "", view: "canvas", pluginKey: "builtin:board", canvasId: "board", name: "Board", icon: "list-checks", plugin: "plugin:global:board" });
  assert.ok(isCanvasTab(board()) && !isCanvasTab(editor()) && !isEditorTab(board()) && !isChangesTab(board()) && !isTerminalTab(board()));
  assert.equal(fileTabKey(board()), '["","canvas","builtin:board","board","",""]');
  // A session names its instance by itself: the project it was opened from is not part of it, so one session is one tab.
  assert.equal(fileTabKey(sessionCanvas()), '["","canvas","k","run","s1",""]');
  assert.ok(sameFileTab(sessionCanvas(), canvasTab({ pluginKey: "k", canvasId: "run", sessionId: "s1" })));
  // The same identity is one tab, whatever the look the plugin gave it.
  assert.ok(sameFileTab(board(), canvasTab({ pluginKey: "builtin:board", canvasId: "board" }, { title: "Renamed" })));
  const identities = [board(), board("a"), board("b"), projectCanvas(), projectCanvas("q"), projectCanvas("p", "other"), sessionCanvas(), sessionCanvas("s2"),
    canvasTab({ pluginKey: "other", canvasId: "board" }), editor(), changes()];
  assert.equal(new Set(identities.map(fileNodeId)).size, identities.length);
  const state = openFileTab(openFileTab(emptyFileTabs(), board()), board("a"));
  assert.equal(state.open.length, 2);
  assert.equal(openFileTab(state, canvasTab({ pluginKey: "builtin:board", canvasId: "board" }, { title: "Other" })).open[0], state.open[0], "Asked again, the one that is open is shown, as it is.");
});

test("the look a plugin gives a canvas tab is kept in the tab, in its place, and the identity does not change", () => {
  let state = openFileTab(openFileTab(openFileTab(emptyFileTabs(), editor()), board()), board("a"));
  state = activateFileTab(state, board());
  const renamed = refreshCanvasTab(state, board(), { title: "3 of 8 done", icon: "check" });
  assert.deepEqual(renamed.open.map(fileTabKey), state.open.map(fileTabKey));
  assert.equal(renamed.open[1].name, "3 of 8 done");
  assert.equal(renamed.open[1].icon, "check");
  assert.equal(renamed.open[1].plugin, "plugin:global:board", "what the look does not say stays");
  assert.ok(sameFileTab(renamed.active, board()), "the tab in front stays in front");
  assert.equal(renamed.active, renamed.open[1], "the active tab is the one that was refreshed");
  assert.equal(renamed.open[0], state.open[0]);
  assert.equal(renamed.open[2], state.open[2]);
  assert.equal(refreshCanvasTab(renamed, board(), { title: "3 of 8 done" }), renamed, "nothing changed");
  assert.equal(refreshCanvasTab(renamed, board("zzz"), { title: "x" }), renamed, "a tab that is not open");
  // A null look removes what was there: a blank title brings back the one of the canvas.
  assert.equal(refreshCanvasTab(renamed, board(), { title: null }).open[1].name, undefined);
  // A tab that was closed and can be reopened has the look too.
  const closed = closeFileTab(renamed, board());
  assert.equal(refreshCanvasTab(closed, board(), { title: "Later" }).closed.at(-1)!.name, "Later");
});

test("a canvas outlives the projects when it is about the application, and goes with its project or its session otherwise", () => {
  const sessions = { ...catalog, sessions: [{ id: "s1" } as WorkspaceSnapshot["sessions"][number]] };
  const state = { open: [board(), projectCanvas(), projectCanvas("gone"), projectCanvas("old"), sessionCanvas(), sessionCanvas("s2")], active: sessionCanvas("s2"),
    closed: [projectCanvas("gone", "later")] };
  assert.deepEqual(reconcileFileTabs(state, sessions), { open: [board(), projectCanvas(), sessionCanvas()], active: null, closed: [] });
  assert.deepEqual(reconcileFileTabs({ open: [board()], active: board(), closed: [] }, { ...catalog, projects: [] }), { open: [board()], active: board(), closed: [] });
  assert.equal(reconcileTerminalTabs(openFileTab(emptyFileTabs(), board()), new Set()).open.length, 1, "no terminal is needed");
  // A session canvas of a chat has no project.
  const chat = canvasTab({ pluginKey: "k", canvasId: "run", sessionId: "s1" });
  assert.deepEqual(reconcileFileTabs(openFileTab(emptyFileTabs(), chat), sessions).open, [chat]);
});

test("canvas tabs are kept for the next start with their look, and a canvas tab that is not well formed is left out", () => {
  const tabs = [board(), board("a"), projectCanvas(), sessionCanvas()];
  const state = tabs.reduce((opened, tab) => openFileTab(opened, tab), emptyFileTabs());
  let stored = "";
  assert.equal(persistFileTabs(value => { stored = value; }, state), true);
  assert.deepEqual(restoreFileTabs(() => stored), { ...state, closed: [] });
  assert.deepEqual(restoreFileTabs(() => stored)?.open.map(tab => tab.name), ["Board", "Board", undefined, undefined]);
  const json = (value: unknown) => () => JSON.stringify(value);
  const valid = { projectId: "", projectPath: "", view: "canvas", pluginKey: "k", canvasId: "c" };
  assert.deepEqual(restoreFileTabs(json({ version: 1, open: [valid], active: valid }))?.open, [canvasTab({ pluginKey: "k", canvasId: "c" })]);
  for (const bad of [{ ...valid, pluginKey: undefined }, { ...valid, pluginKey: "" }, { ...valid, pluginKey: "x".repeat(513) }, { ...valid, canvasId: undefined }, { ...valid, canvasId: "bad id" },
    { ...valid, canvasId: "x".repeat(65) }, { ...valid, projectId: "", projectPath: "/p" }, { ...valid, projectId: "p", projectPath: undefined }, { ...valid, projectId: 4 },
    { ...valid, sessionId: "" }, { ...valid, key: 5 }, { ...valid, key: "x".repeat(129) }, { ...valid, path: "a.ts" }]) {
    assert.deepEqual(restoreFileTabs(json({ version: 1, open: [editor(), bad, changes()], active: changes() })), { open: [editor(), changes()], active: changes(), closed: [] }, JSON.stringify(bad));
  }
  // A look that is not as this build stores it is dropped; the tab stays.
  assert.deepEqual(restoreFileTabs(json({ version: 1, open: [{ ...valid, name: "", icon: 4, plugin: "x".repeat(600) }], active: null }))?.open, [canvasTab({ pluginKey: "k", canvasId: "c" })]);
  // The tab of a canvas is kept in the state of a space that has others of its kind.
  assert.equal(restoreFileTabs(json({ version: 1, open: [valid, valid], active: null })), null, "the same tab twice is no state this build writes");
});

test("a canvas tab is labeled with its title and its project in every language", () => {
  for (const locale of locales) {
    const render = (tab: FileTab) => renderToStaticMarkup(createElement(ShellLanguageContext.Provider,
      { value: { locale, choice: locale, setLanguage: () => assert.fail("rendering must not dispatch") } },
      createElement(FileTabLabel, { tab, project: "<Project>", dirty: false })));
    const html = render(canvasTab({ pluginKey: "k", canvasId: "c", project: { id: "p", path: "/p" } }, { title: "<Board>" }));
    assert.ok(html.includes("&lt;Board&gt;") && html.includes("&lt;Project&gt;"), html);
    const application = render(board());
    assert.ok(application.includes("Board") && !application.includes("&lt;Project&gt;"), application);
    assert.ok(!application.includes("session-tab-dirty"));
    assert.ok(render(canvasTab({ pluginKey: "k", canvasId: "c" })).includes(translate(locale, "Canvas")), "a tab with no title is a canvas");
  }
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

test("the work items have one tab, of no project, that outlives the projects and is kept for the next start", () => {
  assert.deepEqual(workItemsTab, { projectId: "", projectPath: "", view: "workItems" });
  assert.ok(isWorkItemsTab(workItemsTab) && !isWorkItemsTab(automationsTab) && !isAutomationsTab(workItemsTab) && !isEditorTab(workItemsTab));
  const state = openFileTab(openFileTab(openFileTab(emptyFileTabs(), editor()), automationsTab), workItemsTab);
  assert.equal(state.active, workItemsTab);
  assert.equal(openFileTab(state, { ...workItemsTab }).open.length, 3, "Asked again, the one that is open is shown.");
  assert.deepEqual(reconcileFileTabs(state, { ...catalog, projects: [] }), { open: [automationsTab, workItemsTab], active: workItemsTab, closed: [] });
  let stored = "";
  persistFileTabs(value => { stored = value; }, state);
  assert.deepEqual(restoreFileTabs(() => stored), { ...state, closed: [] });
  assert.deepEqual(restoreFileTabs(() => JSON.stringify({ version: 1, open: [{ projectId: "p", projectPath: "/p", view: "workItems" }], active: null })), emptyFileTabs());
  for (const locale of locales) {
    const html = renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale, choice: locale, setLanguage: () => assert.fail("rendering must not dispatch") } },
      createElement(FileTabLabel, { tab: workItemsTab, project: "", dirty: false })));
    assert.ok(html.includes(translate(locale, "Work items")), html);
  }
});

test("the automations have one tab, of no project, that outlives the projects and is kept for the next start", () => {
  assert.deepEqual(automationsTab, { projectId: "", projectPath: "", view: "automations" });
  assert.ok(isAutomationsTab(automationsTab) && !isAutomationsTab(editor()) && !isEditorTab(automationsTab) && !isChangesTab(automationsTab) && !isTerminalTab(automationsTab));
  const state = openFileTab(openFileTab(emptyFileTabs(), editor()), automationsTab);
  assert.deepEqual(names(state.open), ["editor:p", "automations:"]);
  assert.equal(state.active, automationsTab);
  assert.equal(openFileTab(state, { ...automationsTab }).open.length, 2, "Asked again, the one that is open is shown.");
  // It belongs to no project: it stays when the projects go.
  assert.deepEqual(reconcileFileTabs(state, { ...catalog, projects: [] }), { open: [automationsTab], active: automationsTab, closed: [] });
  assert.equal(reconcileTerminalTabs(state, new Set()), state);
  let stored = "";
  persistFileTabs(value => { stored = value; }, state);
  assert.deepEqual(restoreFileTabs(() => stored), { ...state, closed: [] });
  // A stored tab that names a project is not the tab of the automations.
  assert.deepEqual(restoreFileTabs(() => JSON.stringify({ version: 1, open: [{ projectId: "p", projectPath: "/p", view: "automations" }], active: null })), emptyFileTabs());
  for (const locale of locales) {
    const html = renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale, choice: locale, setLanguage: () => assert.fail("rendering must not dispatch") } },
      createElement(FileTabLabel, { tab: automationsTab, project: "", dirty: false })));
    assert.ok(html.includes(translate(locale, "Automations")) && !html.includes("session-tab-dirty"), html);
  }
});

test("the code editor on the folder of a skill has a tab that names the skill, and a folder that is not the user's is only read", () => {
  const skill = skillEditorTab({ id: "skill:global:UserCommon:release-notes", path: "/home/.agents/skills/release-notes", name: "release-notes" });
  assert.deepEqual(skill, { projectId: "skill:global:UserCommon:release-notes", projectPath: "/home/.agents/skills/release-notes", view: "editor", name: "release-notes" });
  assert.ok(skill.projectId.startsWith(skillFolderPrefix));
  assert.ok(isSkillTab(skill) && isEditorTab(skill) && isFolderTab(skill) && !isPluginTab(skill) && !isSkillTab(editor()) && !isSkillTab(changes("skill:global:UserAlta:a")) && !isFolderTab(editor()));
  assert.ok(isFolderTab(pluginEditorTab({ id: "plugin:global:notes", path: "/home/.alta/plugins/notes", name: "notes" })));
  // One tab for a skill, beside the tabs of the projects; the same name from another source or project is another folder.
  const state = openFileTab(openFileTab(emptyFileTabs(), editor()), skill);
  assert.deepEqual(names(state.open), ["editor:p", "editor:skill:global:UserCommon:release-notes"]);
  assert.equal(openFileTab(state, { ...skill }).open.length, 2, "Asked again, the one that is open is shown.");
  assert.notEqual(fileNodeId(skill), fileNodeId(skillEditorTab({ id: "skill:project:p:ProjectAlta:release-notes", path: "/p/.alta/skills/release-notes", name: "release-notes" })));
  // The folder is no project of the workspace: the tab stays when the projects go, and is kept for the next start.
  assert.equal(resolveFileTab(catalog, skill), undefined);
  assert.deepEqual(reconcileFileTabs(state, { ...catalog, projects: [] }), { open: [skill], active: skill, closed: [] });
  let stored = "";
  persistFileTabs(value => { stored = value; }, state);
  assert.deepEqual(restoreFileTabs(() => stored), { ...state, closed: [] });
  for (const tab of [{ ...skill, name: undefined }, { ...skill, name: "" }, { ...skill, view: "changes" }, { ...skill, view: undefined, path: "SKILL.md" }])
    assert.deepEqual(restoreFileTabs(() => JSON.stringify({ version: 1, open: [tab], active: null })), emptyFileTabs(), JSON.stringify(tab));
  // The id says where the skill comes from: the skills of the user and of a project are edited, the others are read.
  for (const source of ["UserAlta", "UserCommon", "ProjectAlta", "ProjectCommon"]) {
    assert.equal(skillReadOnly(source), false, source);
    assert.equal(isReadOnlyTab(skillEditorTab({ id: `skill:global:${source}:a`, path: "/a", name: "a" })), false, source);
    assert.equal(isReadOnlyTab(skillEditorTab({ id: `skill:project:p:${source}:a:b`, path: "/a", name: "a:b" })), false, source);
  }
  for (const source of ["Builtin", "Plugin", "Temporary", "Other"]) {
    assert.equal(skillReadOnly(source), true, source);
    assert.equal(isReadOnlyTab(skillEditorTab({ id: `skill:global:${source}:a`, path: "/a", name: "a" })), true, source);
    assert.equal(isReadOnlyTab(skillEditorTab({ id: `skill:project:p:${source}:UserAlta`, path: "/a", name: "UserAlta" })), true, source);
  }
  assert.equal(isReadOnlyTab(skillEditorTab({ id: "skill:unknown", path: "/a", name: "a" })), true, "An id that is not understood is not written to.");
  assert.ok(!isReadOnlyTab(editor()) && !isReadOnlyTab(changes()) && !isReadOnlyTab(pluginEditorTab({ id: "plugin:global:notes", path: "/n", name: "notes" })));
  for (const locale of locales) {
    const html = renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale, choice: locale, setLanguage: () => assert.fail("rendering must not dispatch") } },
      createElement(FileTabLabel, { tab: skill, project: "release-notes", dirty: true })));
    assert.ok(html.includes(translate(locale, "Skill")) && html.includes("release-notes") && html.includes(translate(locale, "Unsaved changes")), html);
  }
});

test("the code editor on the folder of a file that no project has has a tab that names the folder, and is not kept for the next start", () => {
  const folder = diskEditorTab({ id: "folder:0123456789abcdef01234567", path: "/tmp/elsewhere", name: "elsewhere" });
  assert.deepEqual(folder, { projectId: "folder:0123456789abcdef01234567", projectPath: "/tmp/elsewhere", view: "editor", name: "elsewhere" });
  assert.ok(folder.projectId.startsWith(diskFolderPrefix));
  assert.ok(isDiskFolderTab(folder) && isFolderTab(folder) && isEditorTab(folder) && !isPluginTab(folder) && !isSkillTab(folder) && !isReadOnlyTab(folder)
    && !isDiskFolderTab(editor()) && !isDiskFolderTab(changes("folder:0123456789abcdef01234567")));
  const state = openFileTab(openFileTab(emptyFileTabs(), editor()), folder);
  assert.deepEqual(names(state.open), ["editor:p", "editor:folder:0123456789abcdef01234567"]);
  assert.equal(openFileTab(state, { ...folder }).open.length, 2, "Asked again, the one that is open is shown.");
  // The folder is no project of the workspace: the tab stays when the projects go.
  assert.deepEqual(reconcileFileTabs(state, { ...catalog, projects: [] }), { open: [folder], active: folder, closed: [] });
  // The host that gave the id knows the folder while it runs: the tab is not stored, and a stored one is not restored.
  let stored = "";
  persistFileTabs(value => { stored = value; }, state);
  assert.deepEqual(restoreFileTabs(() => stored), { open: [editor()], active: null, closed: [] });
  assert.deepEqual(restoreFileTabs(() => JSON.stringify({ version: 1, open: [editor(), folder], active: folder })), { open: [editor()], active: null, closed: [] });
  // A tab the host opened for one file of a folder, or to be read, has nothing created, renamed or removed in it.
  for (const id of ["folder:file:0123456789abcdef01234567", "folder:view:0123456789abcdef01234567"]) {
    const fixed = diskEditorTab({ id, path: "/home/.alta", name: "config.toml" });
    assert.ok(isDiskFolderTab(fixed) && isFolderTab(fixed) && isReadOnlyTab(fixed), id);
  }
});

test("the code editor on the folder of a plugin has a tab that names the plugin, outlives the projects and is kept for the next start", () => {
  const plugin = pluginEditorTab({ id: "plugin:global:notes", path: "/home/.alta/plugins/notes", name: "notes" });
  assert.deepEqual(plugin, { projectId: "plugin:global:notes", projectPath: "/home/.alta/plugins/notes", view: "editor", name: "notes" });
  assert.ok(plugin.projectId.startsWith(pluginFolderPrefix));
  assert.ok(isPluginTab(plugin) && isEditorTab(plugin) && !isPluginTab(editor()) && !isPluginTab(changes("plugin:global:notes")) && !isPluginTab(automationsTab));
  // One tab for a folder, beside the tabs of the projects.
  const state = openFileTab(openFileTab(emptyFileTabs(), editor()), plugin);
  assert.deepEqual(names(state.open), ["editor:p", "editor:plugin:global:notes"]);
  assert.equal(openFileTab(state, { ...plugin }).open.length, 2, "Asked again, the one that is open is shown.");
  assert.notEqual(fileNodeId(plugin), fileNodeId(pluginEditorTab({ id: "plugin:project:p:notes", path: "/p/.alta/plugins/notes", name: "notes" })));
  // The folder is no project of the workspace: the tab stays when the projects go, and its editor says when the folder is gone.
  assert.equal(resolveFileTab(catalog, plugin), undefined);
  assert.deepEqual(reconcileFileTabs(state, { ...catalog, projects: [] }), { open: [plugin], active: plugin, closed: [] });
  assert.equal(reconcileTerminalTabs(state, new Set()), state);
  let stored = "";
  persistFileTabs(value => { stored = value; }, state);
  assert.deepEqual(restoreFileTabs(() => stored), { ...state, closed: [] });
  assert.equal(restoreLegacyFiles(() => stored).size, 0);
  // A stored tab on such a folder is the editor, with the name it shows; anything else is not restored.
  for (const tab of [{ ...plugin, name: undefined }, { ...plugin, name: "" }, { ...plugin, name: 4 }, { ...plugin, name: "x".repeat(129) }, { ...plugin, view: "changes" }, { ...plugin, view: undefined, path: "plugin.cs" }])
    assert.deepEqual(restoreFileTabs(() => JSON.stringify({ version: 1, open: [tab], active: null })), emptyFileTabs(), JSON.stringify(tab));
  for (const locale of locales) {
    const render = (dirty: boolean) => renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale, choice: locale, setLanguage: () => assert.fail("rendering must not dispatch") } },
      createElement(FileTabLabel, { tab: plugin, project: "notes", dirty })));
    const html = render(true);
    assert.ok(html.includes(translate(locale, "Plugin")) && html.includes("notes") && html.includes(translate(locale, "Unsaved changes")), html);
    assert.ok(!render(false).includes("session-tab-dirty"));
  }
});

test("each terminal shown has a tab of its own, which lasts as long as the terminal and is not kept for the next start", () => {
  const first = terminalTab({ id: "t1", projectId: "p", folder: "/p/src" }), second = terminalTab({ id: "t2", projectId: "p", folder: "/p" });
  const free = terminalTab({ id: "t3", projectId: null, folder: "/home/me" });
  assert.deepEqual(first, { projectId: "p", projectPath: "/p/src", view: "terminal", terminalId: "t1" });
  assert.equal(free.projectId, "");
  assert.equal(fileTabKey(first), '["p","terminal","t1"]');
  assert.notEqual(fileNodeId(first), fileNodeId(second));
  assert.ok(isTerminalTab(first) && !isTerminalTab(editor()) && !isEditorTab(first) && !isChangesTab(first));
  // The folder its shell moved to does not make it another tab.
  assert.ok(sameFileTab(first, { ...first, projectPath: "/p" }) && !sameFileTab(first, second));

  let state = [editor(), first, second, free].reduce((tabs, tab) => openFileTab(tabs, tab), emptyFileTabs());
  assert.deepEqual(state.open.map(tab => tab.terminalId ?? tab.view), ["editor", "t1", "t2", "t3"]);
  // The catalog decides for the tabs of projects only: a terminal is not one of its entries.
  assert.deepEqual(reconcileFileTabs(state, { ...catalog, projects: [] }).open.map(tab => tab.terminalId), ["t1", "t2", "t3"]);
  assert.equal(reconcileFileTabs(state, catalog), state);

  // Its terminal decides: a tab that is open and one that could be reopened both go with it.
  state = closeFileTab(state, second);
  assert.equal(reconcileTerminalTabs(state, new Set(["t1", "t2", "t3"])), state);
  const lasting = reconcileTerminalTabs(state, new Set(["t1"]));
  assert.deepEqual(lasting.open.map(tab => tab.terminalId ?? tab.view), ["editor", "t1"]);
  assert.deepEqual(lasting.closed, []);
  assert.equal(lasting.active, null, "The terminal of the active tab is gone.");
  assert.ok(sameFileTab(reconcileTerminalTabs(activateFileTab(state, first), new Set(["t1"])).active, first));

  // A terminal does not outlive the application: its tab is not stored.
  let stored: string | null = null;
  persistFileTabs(value => { stored = value; }, activateFileTab(state, first));
  assert.deepEqual(restoreFileTabs(() => stored), { open: [editor()], active: null, closed: [] });
  assert.deepEqual(restoreFileTabs(() => JSON.stringify({ version: 1, open: [editor(), first], active: first })), { open: [editor()], active: null, closed: [] },
    "A stored terminal tab is not a tab to restore.");
});

test("the tab of a terminal shows its title or its folder, and a mark once it has ended or asks to be looked at", () => {
  const terminal = (more: Partial<TerminalItem> = {}): TerminalItem => ({
    id: "t1", projectId: "p", sessionId: null, title: "/p/src", titled: false, folder: "/p/src", profile: "sh", profileName: "<sh>", programTitle: null, running: true, exitCode: null,
    integrated: true, busy: false, command: null, lastExitCode: null, columns: 120, rows: 30, created: "2026-10-06T00:00:00Z", open: true, attention: false, agent: false, ...more });
  const tab = terminalTab({ id: "t1", projectId: "p", folder: "/p" });
  for (const locale of locales) {
    const render = (shown?: TerminalItem) => renderToStaticMarkup(createElement(ShellLanguageContext.Provider,
      { value: { locale, choice: locale, setLanguage: () => assert.fail("rendering must not dispatch") } },
      createElement(FileTabLabel, { tab, project: "Project", dirty: true, terminal: shown })));
    const running = render(terminal());
    assert.ok(running.includes(`${translate(locale, "Terminal")} src`) && running.includes("&lt;sh&gt;"), running);
    assert.ok(!running.includes("terminal-tab-mark") && !running.includes("session-tab-dirty"), running);
    assert.ok(render(terminal({ title: "<dev> server", titled: true })).includes("&lt;dev&gt; server"));
    const ended = render(terminal({ running: false, exitCode: 3 }));
    assert.ok(ended.includes('data-kind="ended"') && ended.includes(translate(locale, "Ended (exit code {code})", { code: 3 })), ended);
    assert.ok(render(terminal({ attention: true })).includes('data-kind="attention"'));
    // Before the host has said anything of it, the tab is a terminal and nothing more.
    assert.ok(render().includes(`>${translate(locale, "Terminal")}<`));
  }
});
