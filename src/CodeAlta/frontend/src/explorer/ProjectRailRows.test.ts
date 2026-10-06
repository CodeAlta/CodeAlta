import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { WorkspaceProject, WorkspaceSession, WorkspaceSnapshot } from "#neoastra";
import { locales, translate } from "../localization";
import { sessionHierarchy } from "../sessionHierarchy";
import { ShellLanguageContext } from "../shellLanguage";
import { ExplorerSessions } from "./ExplorerSessions";
import { projectRailProjection } from "./projectRail";
import { ProjectRailRows, type ProjectTreeView } from "./ProjectRailRows";

const never = () => assert.fail("rendering must not act");
const project = (id: string, name: string, archived = false): WorkspaceProject => ({ id, name, path: `/${id}`, archived });
const session = (id: string, projectId: string | null, parentSessionId: string | null = null): WorkspaceSession => ({
  messageCount: null, createdAt: null, id, title: `title of ${id}`, fullTitle: `title of ${id}`, fullTitleTruncated: false, parentSessionId,
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
// What a row holds, from its opening tag to the next row.
const rows = (html: string) => html.split('<li class="project-action-row"').slice(1);

test("favorite projects are listed first, in the chosen order, and the others follow", () => {
  const value = snapshot([project("c", "Gamma"), project("a", "Alpha"), project("b", "Beta"), project("d", "Delta")]);
  const listed = (filter: string, favorites: string[]) => {
    const result = projectRailProjection(value, filter, "name", favorites);
    return [result.projects.map(row => row.id).join(""), result.favorites];
  };
  assert.deepEqual(listed("", []), ["abdc", 0]);
  // The order they were made favorites in does not matter, nor do favorites that are no longer projects.
  assert.deepEqual(listed("", ["d", "missing", "b"]), ["bdac", 2]);
  assert.deepEqual(listed("a", ["d", "b"]), ["bdac", 2]);
  assert.deepEqual(listed("alp", ["d", "b"]), ["a", 0]);
  assert.deepEqual(listed("delta", ["d", "b"]), ["d", 1]);
  assert.deepEqual(listed("", ["a", "b", "c", "d"]), ["abdc", 4]);
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
  const [alpha, beta, old] = rows(html);
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

test("every open scope shows its sessions; the selected one keeps its own while it is closed", () => {
  const projects = [project("a", "Alpha"), project("b", "Beta"), project("c", "Gamma")];
  const render = (selectedId: string | null, expanded: (string | null)[]) => renderToStaticMarkup(createElement(ProjectRailRows, { projects, selectedId, onSelect: never,
    canRename: true, renameBusy: false, onRename: never, tree: tree(expanded) }, createElement("span", null, "sessions of the selection")));
  const open = render("a", ["a", "c", null]);
  const [alpha, beta, gamma, global] = rows(open);
  assert.match(alpha, /aria-expanded="true"[\s\S]*<li class="project-session-branch"><span>sessions of the selection<\/span>/);
  assert.match(beta, /aria-expanded="false"/);
  assert.doesNotMatch(beta, /project-session-branch/);
  assert.match(gamma, /aria-expanded="true"[\s\S]*<li class="project-session-branch"><span>sessions of c<\/span>/);
  assert.match(global, /aria-pressed="false" aria-expanded="true" data-scope=""[\s\S]*<li class="project-session-branch"><span>sessions of global<\/span>/);
  // A scope is open or closed whatever is selected. The closed selection keeps its sessions in the page, hidden.
  const closed = render("a", ["b"]);
  assert.match(rows(closed)[0], /aria-pressed="true" aria-expanded="false"[\s\S]*<li class="project-session-branch" hidden=""><span>sessions of the selection<\/span>/);
  assert.match(rows(closed)[1], /aria-expanded="true"[\s\S]*sessions of b/);
  assert.match(rows(render(null, []))[3], /aria-pressed="true" aria-expanded="false"[\s\S]*hidden=""><span>sessions of the selection/);
  // An open folder for an open project, a closed one otherwise.
  assert.match(rows(open)[0], /lucide-folder-open/);
  assert.doesNotMatch(rows(open)[1], /lucide-folder-open/);
});

test("the sessions of an open scope are rows that open them, with more on demand", () => {
  const value = snapshot([project("a", "Alpha")], [session("parent", "a"), session("child", "a", "parent"), session("other", "a")]);
  const all = sessionHierarchy(value.sessions, value.sessions, "", "a");
  const render = (rows: typeof all, more: number, extended: boolean, global = false) => renderToStaticMarkup(createElement(ExplorerSessions, { rows, global, more, extended,
    access: () => ({ rename: true, delete: true }), marks: row => createElement("i", null, `marks of ${row.id}`), onAction: never, onMore: never, onFewer: never }));
  const html = render(all, 0, false);
  const listed = html.split('class="session-row"').slice(1);
  assert.equal(listed.length, 3);
  const child = listed.find(row => row.includes("title of child"))!;
  // A session started by another one is pushed in under it, with an icon of its own.
  assert.match(child, /padding-left:23px[\s\S]*data-file-tone="teal"[\s\S]*<i>marks of child<\/i>/);
  assert.match(listed.find(row => row.includes("title of parent"))!, /aria-pressed="false"[\s\S]*padding-left:11px[\s\S]*data-file-tone="purple"/);
  assert.match(child, /aria-label="Actions for title of child \(ID: child\)" aria-haspopup="menu" aria-expanded="false"/);
  assert.doesNotMatch(html, /Show more|Show fewer|role="menu"/);
  assert.match(render(all.slice(0, 1), 2, false), /Show more…[\s\S]*\(2\)/);
  assert.match(render(all, 0, true), /Show fewer/);
  assert.match(render([], 0, false), /No sessions in this project\./);
  assert.match(render([], 0, false, true), /No global sessions\./);
});
