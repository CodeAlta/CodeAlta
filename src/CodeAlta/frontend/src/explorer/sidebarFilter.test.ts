import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { WorkspaceClaudeCodeConversation, WorkspaceProject, WorkspaceSession, WorkspaceSnapshot } from "#neoastra";
import type { SessionHierarchyRow } from "../sessionHierarchy";
import { ShellLanguageContext } from "../shellLanguage";
import { SidebarFilterBox } from "./SidebarFilterBox";
import { filterOpens, filterSessionRows, sessionMatches, sidebarFilterView, toggleFilterFold, unplacedKey } from "./sidebarFilter";

const project = (id: string, name: string): WorkspaceProject => ({ id, name, path: `C:\\code\\${name}`, archived: false });
const session = (id: string, projectId: string | null, title: string, parentSessionId: string | null = null): WorkspaceSession => ({
  messageCount: null, automationId: null, worktreePath: null, worktreeRoot: null, worktreeName: null, worktreeMissing: false, forkedFromSessionId: null, forkedFromTitle: null,
  forkKind: null, forkTurnCount: 0, rewindCount: 0, createdAt: null, id, title, fullTitle: title, fullTitleTruncated: false, parentSessionId,
  scopeKind: projectId === null ? "global" : "project", projectId, lineageIssue: null, workspacePath: null, providerKey: null, updatedAt: "2026-10-17T10:00:00Z", archived: false, archivedAt: null, spaceId: null });
const row = (value: WorkspaceSession, depth = 0): SessionHierarchyRow => ({ session: value, depth, diagnostic: null, tooltip: value.title });
const conversation = (id: string, title: string, projectId: string | null): WorkspaceClaudeCodeConversation =>
  ({ cliSessionId: id, title, lastChangedAt: "2026-10-17T09:00:00Z", origin: "vscode", projectId, tag: projectId ? null : "C:\\Users\\me", startDirectory: null });

test("a session is found by every word of its title, whatever their case and the marks of Markdown", () => {
  assert.equal(sessionMatches(session("s1", "p1", "**Fix** the shader cache"), ["shader", "fix"]), true);
  assert.equal(sessionMatches(session("s1", "p1", "Fix the shader cache"), ["shader", "docs"]), false);
});

test("the filter keeps the sessions found and the sessions they are under, in the order of the tree", () => {
  const rows = [row(session("a", "p1", "Plan the release")), row(session("a1", "p1", "Write the shader notes", "a"), 1), row(session("a2", "p1", "Other child", "a"), 1),
    row(session("b", "p1", "Shader cache")), row(session("c", "p1", "Unrelated"))];
  assert.deepEqual(filterSessionRows(rows, ["shader"]).map(value => value.session.id), ["a", "a1", "b"], "a sub-agent found stays under its session");
  assert.deepEqual(filterSessionRows(rows, []).map(value => value.session.id), ["a", "a1", "a2", "b", "c"]);
});

test("the projects shown are those whose name, a session or a Claude Code session is found; the Claude Code ones by their group", () => {
  const snapshot: WorkspaceSnapshot = { configured: true, projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false,
    projects: [project("p1", "Stride"), project("p2", "Docs"), project("p3", "Shaders"), project("p4", "Empty")],
    sessions: [session("s1", "p1", "Shader cache"), session("s2", null, "A shader chat"), session("s3", "p4", "Nothing")] };
  const view = sidebarFilterView(snapshot, ["shader"], [conversation("c1", "Shader review", "p2"), conversation("c2", "Shader crash", null),
    conversation("c3", "Docs", "p2"), conversation("c4", "Shader of another space", "p9")]);
  assert.deepEqual([...view.scopes].sort(), ["p1", null].sort());
  assert.deepEqual([...view.projects].sort(), ["p1", "p2", "p3"], "a session, a Claude Code session, or the name");
  assert.deepEqual(view.claudeCode.get("p2")?.map(value => value.cliSessionId), ["c1"]);
  assert.deepEqual(view.claudeCode.get(unplacedKey)?.map(value => [value.cliSessionId, value.tag, value.held]), [["c2", "C:\\Users\\me", false]]);
  assert.equal(view.claudeCode.has("p9"), false, "a project of another space is not shown");
});

test("the filter box says what it filters, offers to clear what is typed, and says when nothing matches", () => {
  const render = (props: object) => renderToStaticMarkup(createElement(ShellLanguageContext.Provider,
    { value: { locale: "fr", choice: "fr", setLanguage: () => { } } }, createElement(SidebarFilterBox as never, props as never)));
  const empty = render({ value: "", onChange: () => { } });
  assert.match(empty, /placeholder="Filtrer les sessions"/);
  assert.doesNotMatch(empty, /sidebar-filter-clear/, "nothing typed: nothing to clear");
  const typed = render({ value: "shader", onChange: () => { }, empty: true });
  assert.match(typed, /aria-label="Effacer le filtre"/);
  assert.match(typed, /Aucun résultat\./);
});

test("a scope the filter opens is folded apart from the tree, and opened again by a second toggle", () => {
  const snapshot = { projects: [project("p1", "Stride"), project("p2", "Docs")], sessions: [session("s1", null, "Shader chat")] } as unknown as WorkspaceSnapshot;
  const view = sidebarFilterView(snapshot, ["stride"]);
  assert.equal(filterOpens(view, "p1"), true);
  assert.equal(filterOpens(view, "p2"), false);
  assert.equal(filterOpens(sidebarFilterView(snapshot, ["shader"]), null), true, "the chats, with a chat found");
  const folded = toggleFilterFold(new Set(), "p1");
  assert.deepEqual([...folded], ["p1"]);
  assert.deepEqual([...toggleFilterFold(folded, "p1")], []);
  assert.deepEqual([...toggleFilterFold(folded, null)], ["p1", null]);
});
