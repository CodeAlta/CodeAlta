import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { WorkspaceProject, WorkspaceSession, WorkspaceSnapshot } from "#neoastra";
import { locales, translate } from "../localization";
import { sessionHierarchy } from "../sessionHierarchy";
import { ShellLanguageContext } from "../shellLanguage";
import { ExplorerSessions, sessionTwistKey } from "./ExplorerSessions";
import { projectRailProjection } from "./projectRail";
import { ProjectRailRows, type ProjectTreeView } from "./ProjectRailRows";
import { sessionList } from "./sessionTree";

const never = () => assert.fail("rendering must not act");
const project = (id: string, name: string, archived = false): WorkspaceProject => ({ id, name, path: `/${id}`, archived });
const session = (id: string, projectId: string | null, parentSessionId: string | null = null): WorkspaceSession => ({
  messageCount: null, automationId: null, worktreePath: null, worktreeRoot: null, worktreeName: null, worktreeMissing: false, createdAt: null, id, title: `title of ${id}`, fullTitle: `title of ${id}`, fullTitleTruncated: false, parentSessionId,
  scopeKind: projectId === null ? "global" : "project", projectId, lineageIssue: null, workspacePath: projectId === null ? "/home" : `/${projectId}`,
  providerKey: "codex", updatedAt: "2026-01-01T00:00:00Z",
});
const snapshot = (projects: WorkspaceProject[], sessions: WorkspaceSession[] = []): WorkspaceSnapshot => ({
  configured: true, projects, sessions, projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false,
});
const tree = (expanded: readonly (string | null)[], favorites: readonly string[] = []): ProjectTreeView => ({
  expanded: id => expanded.includes(id), toggle: never, favorite: id => favorites.includes(id), setFavorite: never,
  sessions: id => createElement("span", null, `sessions of ${id ?? "global"}`),
});
// What a row holds, from its opening tag to the next row. The first one is the row of the chats.
const rows = (html: string) => html.split('<li class="project-action-row"').slice(1);

test("favorite projects are listed first, in the chosen order, and the others follow", () => {
  const value = snapshot([project("c", "Gamma"), project("a", "Alpha"), project("b", "Beta"), project("d", "Delta")]);
  const listed = (favorites: string[]) => {
    const result = projectRailProjection(value, "name", favorites);
    return [result.projects.map(row => row.id).join(""), result.favorites];
  };
  assert.deepEqual(listed([]), ["abdc", 0]);
  // The order they were made favorites in does not matter, nor do favorites that are no longer projects.
  assert.deepEqual(listed(["d", "missing", "b"]), ["bdac", 2]);
  assert.deepEqual(listed(["a", "b", "c", "d"]), ["abdc", 4]);
});

test("the projects are under two titles only when some are favorites and some are not", () => {
  const projects = [project("a", "Alpha"), project("b", "Beta"), project("c", "Gamma")];
  const render = (favorites: number, locale: typeof locales[number] = "en") => renderToStaticMarkup(createElement(ShellLanguageContext.Provider,
    { value: { locale, choice: locale, setLanguage: never } }, createElement(ProjectRailRows, { projects, favorites, selectedId: null, onSelect: never,
      canRename: true, renameBusy: false, onRename: never, tree: tree([], projects.slice(0, favorites).map(row => row.id)) })));
  assert.doesNotMatch(render(0), /project-section/);
  for (const locale of locales) {
    const html = render(1, locale);
    const [first, others] = [html.indexOf(translate(locale, "Favorites")), html.indexOf(translate(locale, "Other projects"))];
    assert.ok(first >= 0 && first < html.indexOf("Alpha") && html.indexOf("Alpha") < others && others < html.indexOf("Beta"), `${locale}: ${html}`);
    assert.ok(html.includes(translate(locale, "Remove {name} from favorites", { name: "Alpha" })) && html.includes(translate(locale, "Add {name} to favorites", { name: "Beta" })), locale);
  }
  const all = render(3);
  assert.match(all, /Favorites/);
  assert.doesNotMatch(all, /Other projects/);
});

test("a project row opens its code editor and its changes, and shows which are open", () => {
  const html = renderToStaticMarkup(createElement(ProjectRailRows, { projects: [project("a", "Alpha"), project("b", "Beta"), project("z", "Old", true)], selectedId: "a",
    onSelect: never, canRename: true, renameBusy: false, onRename: never, tree: tree(["a"], ["b"]),
    editor: { open: id => id === "a", unsaved: row => row.id === "a", show: never }, changes: { open: id => id === "b", show: never } }));
  const [chats, alpha, beta, old] = rows(html);
  // The chats are no project: nothing to edit, no changes, no favorite.
  assert.doesNotMatch(chats, /project-editor-trigger|project-changes-trigger|project-favorite-trigger/);
  assert.match(alpha, /class="icon-button project-row-action project-editor-trigger" data-open="true" data-unsaved="true" aria-label="Code editor of Alpha" title="Show the code editor"/);
  assert.match(alpha, /class="icon-button project-row-action project-changes-trigger" data-open="false" aria-label="Changes of Alpha" title="Open the changes"/);
  assert.match(beta, /project-changes-trigger" data-open="true" aria-label="Changes of Beta" title="Show the changes"/);
  assert.match(beta, /project-editor-trigger" data-open="false" data-unsaved="false"/);
  assert.match(beta, /project-favorite-trigger" data-on="true" aria-label="Remove Beta from favorites"/);
  // An archived project has no tab to open; it can still be a favorite.
  assert.doesNotMatch(old, /project-editor-trigger|project-changes-trigger/);
  assert.match(old, /project-favorite-trigger" data-on="false" aria-label="Add Old to favorites"/);
  // Only the row itself is "pressed": the selection of the list is found by that.
  assert.equal(html.split('aria-pressed="true"').length - 1, 1);
  assert.match(alpha, /aria-pressed="true" aria-expanded="true" data-scope="a"/);
});

test("a project row opens a new terminal in its folder, and shows that the project has some", () => {
  const html = renderToStaticMarkup(createElement(ProjectRailRows, { projects: [project("a", "Alpha"), project("b", "Beta"), project("c", "Gamma"), project("z", "Old", true)], selectedId: null,
    onSelect: never, canRename: true, renameBusy: false, onRename: never, tree: { ...tree(["a"]), after: id => createElement("span", null, `terminals of ${id ?? "global"}`) },
    terminals: { count: id => id === "a" ? 1 : id === "b" ? 3 : 0, create: never } }));
  const [, alpha, beta, gamma, old] = rows(html);
  assert.match(alpha, /class="icon-button project-row-action project-terminal-trigger" data-open="true" aria-label="New terminal in Alpha" title="New terminal \(1 terminal\)"/);
  assert.match(beta, /project-terminal-trigger" data-open="true" aria-label="New terminal in Beta" title="New terminal \(3 terminals\)"/);
  assert.match(gamma, /project-terminal-trigger" data-open="false" aria-label="New terminal in Gamma" title="New terminal"/);
  assert.doesNotMatch(old, /project-terminal-trigger/, "An archived project opens no terminal.");
  // The terminals of an open project follow its sessions.
  assert.ok(alpha.indexOf("sessions of a") > 0 && alpha.indexOf("terminals of a") > alpha.indexOf("sessions of a"), alpha);
  assert.doesNotMatch(beta, /terminals of b/);
  // Without terminals to open (a window that owns no host) the rows have no such button.
  const plain = renderToStaticMarkup(createElement(ProjectRailRows, { projects: [project("a", "Alpha")], selectedId: null, onSelect: never, canRename: true, renameBusy: false, onRename: never, tree: tree([]) }));
  assert.doesNotMatch(plain, /project-terminal-trigger/);
});

test("every open scope shows its sessions; the selected one keeps its own while it is closed", () => {
  const projects = [project("a", "Alpha"), project("b", "Beta"), project("c", "Gamma")];
  const render = (selectedId: string | null, expanded: (string | null)[]) => renderToStaticMarkup(createElement(ProjectRailRows, { projects, selectedId, onSelect: never,
    canRename: true, renameBusy: false, onRename: never, tree: tree(expanded) }, createElement("span", null, "sessions of the selection")));
  const open = render("a", ["a", "c", null]);
  const [global, alpha, beta, gamma] = rows(open);
  assert.ok(open.indexOf('aria-label="Chats"') >= 0 && open.indexOf('aria-label="Chats"') < open.indexOf('id="project-list"'), "The chats come before the projects.");
  assert.match(alpha, /aria-expanded="true"[\s\S]*<li class="project-session-branch"><span>sessions of the selection<\/span>/);
  assert.match(beta, /aria-expanded="false"/);
  assert.doesNotMatch(beta, /project-session-branch/);
  assert.match(gamma, /aria-expanded="true"[\s\S]*<li class="project-session-branch"><span>sessions of c<\/span>/);
  assert.match(global, /aria-pressed="false" aria-expanded="true" data-scope=""[\s\S]*<li class="project-session-branch"><span>sessions of global<\/span>/);
  // A scope is open or closed whatever is selected. The closed selection keeps its sessions in the page, hidden.
  const closed = render("a", ["b"]);
  assert.match(rows(closed)[1], /aria-pressed="true" aria-expanded="false"[\s\S]*<li class="project-session-branch" hidden=""><span>sessions of the selection<\/span>/);
  assert.match(rows(closed)[2], /aria-expanded="true"[\s\S]*sessions of b/);
  assert.match(rows(render(null, []))[0], /aria-pressed="true" aria-expanded="false"[\s\S]*hidden=""><span>sessions of the selection/);
  // An open folder for an open project, a closed one otherwise.
  assert.match(rows(open)[1], /lucide-folder-open/);
  assert.doesNotMatch(rows(open)[2], /lucide-folder-open/);
});

test("the sessions of an open scope are rows that open them, with more on demand", () => {
  const value = snapshot([project("a", "Alpha")], [session("parent", "a"), session("child", "a", "parent"), { ...session("other", "a"), automationId: "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d77" }]);
  const all = sessionHierarchy(value.sessions, value.sessions, "a");
  const tree = { toggle: never, more: never, fewer: never };
  const list = (rows: typeof all, collapsed: readonly string[] = [], subCount = 4, extra = 0) =>
    sessionList(rows, { count: 50, subCount, active: null, extra: () => extra, collapsed: id => collapsed.includes(id) }).entries;
  const renderList = (entries: ReturnType<typeof list>, more = 0, extended = false, global = false) => renderToStaticMarkup(createElement(ExplorerSessions, { entries, global, more, extended, tree,
    access: () => ({ rename: true, delete: true }), marks: row => createElement("i", null, `marks of ${row.id}`), onAction: never, onMore: never, onFewer: never }));
  const render = (rows: typeof all, more: number, extended: boolean, global = false) => renderList(list(rows), more, extended, global);
  const html = render(all, 0, false);
  const listed = html.split('class="session-row"').slice(1);
  assert.equal(listed.length, 3);
  const child = listed.find(row => row.includes("title of child"))!;
  // A session started by another one is pushed in under it, with an icon of its own.
  assert.match(child, /padding-left:26px[\s\S]*data-file-tone="teal"[\s\S]*<i>marks of child<\/i>/);
  // A session shows the logo of its provider, named by its tooltip; a session started by another one has both icons.
  assert.match(listed.find(row => row.includes("title of parent"))!, /aria-pressed="false"[\s\S]*padding-left:14px[\s\S]*<span class="session-icon session-provider" title="codex"><svg class="brand-icon" data-brand="codex"/);
  assert.match(child, /data-file-tone="teal"[\s\S]*data-brand="codex"/);
  // A provider of no known brand keeps the icon of a session.
  const plain = render(sessionHierarchy([{ ...session("plain", "a"), providerKey: "my-proxy" }, { ...session("none", "a"), providerKey: null }], [], "a"), 0, false);
  assert.equal(plain.split('class="session-icon session-provider" data-file-tone="purple"').length - 1, 2);
  assert.match(plain, /title="my-proxy"><svg[^>]*lucide-bot/);
  assert.doesNotMatch(plain, /brand-icon/);
  // A session started by an automation says so with its icon.
  assert.match(listed.find(row => row.includes("title of other"))!, /class="session-icon" data-file-tone="gold" title="Started by an automation"/);
  assert.doesNotMatch(listed.find(row => row.includes("title of parent"))!, /Started by an automation/);
  assert.match(child, /aria-label="Actions for title of child \(ID: child\)" aria-haspopup="menu" aria-expanded="false"/);
  assert.doesNotMatch(html, /Show more|Show fewer|role="menu"/);
  assert.match(render(all.slice(0, 1), 2, false), /Show more…[\s\S]*\(2\)/);
  assert.match(render(all, 0, true), /Show fewer/);
  assert.match(render([], 0, false), /No sessions in this project\./);
  assert.match(render([], 0, false, true), /No chats\./);
});

test("a session that has sub-agents hides them and shows them, and lists more of them on demand", () => {
  const sessions = [session("parent", "a"), ...["c1", "c2", "c3"].map(id => session(id, "a", "parent")), session("leaf", "a")];
  const all = sessionHierarchy(snapshot([project("a", "Alpha")], sessions).sessions, sessions, "a");
  const tree = { toggle: never, more: never, fewer: never };
  const render = (collapsed: readonly string[], subCount: number, extra = 0) => renderToStaticMarkup(createElement(ExplorerSessions, {
    entries: sessionList(all, { count: 50, subCount, active: null, extra: () => extra, collapsed: id => collapsed.includes(id) }).entries, global: false, more: 0, extended: false, tree,
    access: () => ({ rename: true, delete: true }), marks: () => null, onAction: never, onMore: never, onFewer: never }));
  const open = render([], 4);
  const rowOf = (html: string, id: string) => html.split('class="session-row"').slice(1).find(row => row.includes(`title of ${id}`))!;
  // Only the session that has sub-agents says whether they are shown, and has the twist that hides them.
  assert.match(rowOf(open, "parent"), /aria-expanded="true"[\s\S]*class="tree-twist session-twist" title="Hide the sub-agents"[\s\S]*tree-chevron expanded/);
  assert.doesNotMatch(rowOf(open, "leaf"), /aria-expanded="(true|false)" title|tree-twist/);
  assert.doesNotMatch(rowOf(open, "c1"), /tree-twist/);
  assert.doesNotMatch(open, /Show more|Show fewer/);
  const closed = render(["parent"], 4);
  assert.match(rowOf(closed, "parent"), /aria-expanded="false"[\s\S]*title="Show the sub-agents"/);
  assert.doesNotMatch(rowOf(closed, "parent"), /tree-chevron expanded/);
  assert.equal(closed.split('class="session-row"').length - 1, 2);
  // The sub-agents beyond the count are behind a line of their own, pushed in like them.
  const capped = render([], 1);
  assert.equal(capped.split('class="session-row"').length - 1, 3);
  assert.match(capped, /class="session-list-disclosure sub-agent-disclosure" style="padding-left:26px"><button[^>]*>Show more… <span class="muted-text">\(2\)<\/span><\/button><\/div>/);
  assert.match(render([], 1, 1), /sub-agent-disclosure"[^>]*><button[^>]*>Show more… <span class="muted-text">\(1\)<\/span><\/button><button[^>]*>Show fewer<\/button>/);
  assert.match(render([], 1, 2), /sub-agent-disclosure"[^>]*><button[^>]*>Show fewer<\/button>/);
});

test("left hides the sub-agents of a row and right shows them; any other key is left to the Explorer", () => {
  const press = (key: string, collapsed: boolean | null, modifiers: Partial<Record<"altKey" | "ctrlKey" | "metaKey" | "shiftKey", boolean>> = {}) => {
    const done: string[] = [];
    const event = { key, altKey: false, ctrlKey: false, metaKey: false, shiftKey: false, ...modifiers,
      preventDefault: () => done.push("default"), stopPropagation: () => done.push("stop") } as unknown as Parameters<typeof sessionTwistKey>[0];
    sessionTwistKey(event, collapsed === null ? undefined : { collapsed, toggle: () => done.push("toggle") });
    return done.join(" ");
  };
  assert.equal(press("ArrowLeft", false), "default stop toggle");
  assert.equal(press("ArrowRight", true), "default stop toggle");
  // Nothing to hide or to show: left then goes to the project, as on a session that has no sub-agent.
  for (const [key, collapsed] of [["ArrowLeft", true], ["ArrowRight", false], ["ArrowLeft", null], ["ArrowDown", false], ["Enter", true]] as const)
    assert.equal(press(key, collapsed), "", `${key} ${collapsed}`);
  assert.equal(press("ArrowLeft", false, { ctrlKey: true }), "");
  assert.equal(press("ArrowRight", true, { altKey: true }), "");
});
