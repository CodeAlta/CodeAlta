import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { WorkspaceProject, WorkspaceSession } from "#neoastra";
import { ShellLanguageContext } from "../shellLanguage";
import { sessionMoveAccess } from "../sessionRowActions";
import { ExplorerSessions } from "./ExplorerSessions";
import { ProjectRailRows } from "./ProjectRailRows";
import { projectRowAt, sessionDropAllowed, type SessionDrag } from "./sessionDrag";

const never = () => assert.fail("rendering must not act");
const project = (id: string, archived = false): WorkspaceProject => ({ id, name: id, path: `/${id}`, archived });
const session = (id: string, projectId: string): WorkspaceSession => ({ messageCount: null, automationId: null, worktreePath: null, worktreeRoot: null,
  worktreeName: null, worktreeMissing: false, forkedFromSessionId: null, forkedFromTitle: null, forkKind: null, forkTurnCount: 0, rewindCount: 0, archived: false, archivedAt: null, spaceId: null,
  createdAt: null, id, title: id, fullTitle: id, fullTitleTruncated: false, parentSessionId: null,
  scopeKind: "project", projectId, lineageIssue: null, workspacePath: `/${projectId}`, providerKey: "codex", updatedAt: "2026-01-01T00:00:00Z" });
const drag: SessionDrag = { id: "s1", title: "s1", from: { projectId: "stride", projectPath: "/stride" } };
const render = (element: unknown, props: object) => renderToStaticMarkup(createElement(ShellLanguageContext.Provider,
  { value: { locale: "en", choice: "en", setLanguage: never } }, createElement(element as never, props as never)));

test("a dragged session is dropped only on another project that is not archived", () => {
  assert.equal(sessionDropAllowed(drag, project("stride2")), true);
  assert.equal(sessionDropAllowed(drag, project("stride")), false, "Its own project is where it already is.");
  assert.equal(sessionDropAllowed(drag, project("old", true)), false);
  assert.equal(sessionDropAllowed(drag, undefined), false, "A point that is on no project row takes nothing.");
  assert.equal(sessionDropAllowed(null, project("stride2")), false);
});

test("any row of the exact scope of a project can be dragged, without being the selected one", () => {
  const stride = project("stride");
  const row = session("s1", "stride");
  const epoch = "11111111-1111-4111-8111-111111111111";
  assert.equal(sessionMoveAccess(row, stride, epoch, true, true, false, false), true);
  assert.equal(sessionMoveAccess(row, project("stride", true), epoch, true, true, false, false), false);
  assert.equal(sessionMoveAccess({ ...row, workspacePath: "/elsewhere" }, stride, epoch, true, true, false, false), false);
  assert.equal(sessionMoveAccess(row, project("other"), epoch, true, true, false, false), false);
  assert.equal(sessionMoveAccess(row, stride, null, true, true, false, false), false);
  assert.equal(sessionMoveAccess(row, stride, epoch, false, true, false, false), false);
  assert.equal(sessionMoveAccess(row, stride, epoch, true, true, true, false), false);
  assert.equal(sessionMoveAccess(row, stride, epoch, true, true, false, true), false);
});

test("a project row shows the session over it, and a movable session row says so", () => {
  const rail = render(ProjectRailRows, { projects: [project("stride"), project("stride2")], selectedId: null, onSelect: never,
    canRename: true, renameBusy: false, onRename: never, drop: (value: WorkspaceProject) => value.id === "stride2" ? { "data-over": "true" } : undefined });
  const rows = rail.split('<li class="project-action-row"').slice(1);
  assert.doesNotMatch(rows.find(value => value.includes("/stride\n")) ?? rows[0], /data-over/);
  assert.match(rows.find(value => value.includes('data-scope="stride2"')) ?? "", /^ data-over="true"/);
  const sessions = render(ExplorerSessions, { entries: [{ kind: "row", row: { session: session("s1", "stride"), depth: 0, diagnostic: null, tooltip: "s1", subAgents: 0 } }],
    global: false, more: 0, extended: false, access: () => ({ rename: true, delete: true, move: true }), marks: () => null, onAction: never,
    onMore: never, onFewer: never, tree: { toggle: never, more: never, fewer: never }, drag: () => ({ "data-movable": "true" }) });
  assert.match(sessions, /<div class="session-row" data-movable="true"/);
});

test("the project under the pointer is the one whose row it is on, whatever part of the row", () => {
  const row = { closest: (selector: string) => selector === ".project-action-row"
    ? { querySelector: () => ({ dataset: { scope: "stride2" } }) } : null } as unknown as Element;
  assert.equal(projectRowAt(row), "stride2");
  const chats = { closest: () => ({ querySelector: () => ({ dataset: { scope: "" } }) }) } as unknown as Element;
  assert.equal(projectRowAt(chats), null, "The row of the chats is no project.");
  assert.equal(projectRowAt({ closest: () => null } as unknown as Element), null);
  assert.equal(projectRowAt(null), null);
});