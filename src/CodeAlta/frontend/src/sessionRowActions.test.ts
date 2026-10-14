import assert from "node:assert/strict";
import test from "node:test";
import type { WorkspaceProject, WorkspaceSession } from "#neoastra";
import { isSessionContextKey, isSessionDeleteKey, menuFocusIndex, restoreSessionMenuFocus, sessionActionAccess, type SessionMenuTarget } from "./sessionRowActions";

const project: WorkspaceProject = { id: "p", name: "Project", path: "/p", archived: false };
const row: WorkspaceSession = { messageCount: null, automationId: null, worktreePath: null, worktreeRoot: null, worktreeName: null, worktreeMissing: false, forkedFromSessionId: null, forkedFromTitle: null, forkKind: null, forkTurnCount: 0, rewindCount: 0, archived: false, archivedAt: null, spaceId: null, createdAt: null, id: "s", title: "Title", fullTitle: "Title", fullTitleTruncated: false,
  parentSessionId: null, scopeKind: "project", projectId: "p", lineageIssue: null, workspacePath: "/p",
  providerKey: null, updatedAt: "2026-01-01T00:00:00Z" };
const target: SessionMenuTarget = { id: "s", projectId: "p", hostEpoch: "epoch" };
const access = (session = row, menu = target, selected: string | null = "s", scope: string | null = "p",
  selectedProject: WorkspaceProject | undefined = project, epoch: string | null = "epoch", owned = true,
  canMutate = true, busy = false, uncertain = false) =>
  sessionActionAccess(session, menu, selected, scope, selectedProject, epoch, owned, canMutate, busy, uncertain);

test("unselected row must establish exact selection and project scope before context mutation", () => {
  assert.deepEqual(access(row, target, "other"), { open: false, rename: false, fork: false, delete: false, archive: false, "remote-control": false, move: false, space: false,
    "open-vs-code": false, "copy-resume-command": false, "run-in-terminal": false });
  assert.deepEqual(access(row, target, "s"), { open: true, rename: true, fork: true, delete: true, archive: true, "remote-control": true, move: true, space: true,
    "open-vs-code": true, "copy-resume-command": true, "run-in-terminal": true });
  assert.equal(access(row, target, "s", "other").open, false);
  assert.equal(access({ ...row, id: "other" }, target).open, false);
  assert.equal(access(row, { ...target, projectId: "other" }).open, false);
  assert.equal(access(row, target, "s", "p", project, "next").open, false);
});

test("project/global headers are exact; unmatched, archived, stale, read-only and locked rows cannot mutate", () => {
  const global = { ...row, scopeKind: "global", projectId: null };
  const globalTarget = { ...target, projectId: null };
  assert.deepEqual(access(global, globalTarget, "s", null, undefined), { open: true, rename: true, fork: true, delete: true, archive: true, "remote-control": true, move: false, space: true,
    "open-vs-code": true, "copy-resume-command": true, "run-in-terminal": true });
  // A global session belongs to no project: it has none to move from. A fork creates a session in the scope of the row: it takes the authority of a change, and none on an archived project or a read-only host.
  assert.equal(access(row, target, "s", "p", { ...project, archived: true }).fork, false);
  assert.equal(access(row, target, "s", "p", project, "epoch", false).fork, false);
  assert.equal(access({ ...row, workspacePath: "/other" }).fork, false);
  // Remote Control acts on the provider of the session: it takes the authority of a change of the session.
  assert.equal(access(row, target, "s", "p", project, "epoch", false)["remote-control"], false);
  assert.equal(access(row, target, "s", "p", project, "epoch", true, true, true)["remote-control"], false);
  assert.equal(access({ ...row, projectId: "other" }).rename, false);
  assert.equal(access({ ...row, scopeKind: null }).delete, false);
  assert.equal(access({ ...row, workspacePath: "/other" }).rename, false);
  assert.equal(access(row, target, "s", "p", { ...project, archived: true }).delete, false);
  assert.equal(access(row, target, "s", "p", project, "epoch", false).rename, false);
  assert.equal(access(row, target, "s", "p", project, "epoch", true, false).delete, false);
  assert.equal(access(row, target, "s", "p", project, "epoch", true, true, true).rename, false);
  assert.equal(access(row, target, "s", "p", project, "epoch", true, true, false, true).delete, false);
  assert.equal(access(global, globalTarget, "s", null, undefined, null).rename, false);
});

test("a Claude Code session opens elsewhere with the authority of a change; its resume command is copied with the session's", () => {
  // Opening it in VS Code or a terminal closes the process CodeAlta keeps for it.
  for (const action of ["open-vs-code", "run-in-terminal"] as const) {
    assert.equal(access(row, target, "s", "p", project, "epoch", false)[action], false, `${action}: read-only window`);
    assert.equal(access(row, target, "s", "p", project, "epoch", true, false)[action], false, `${action}: cannot mutate`);
    assert.equal(access(row, target, "s", "p", project, "epoch", true, true, true)[action], false, `${action}: busy`);
    assert.equal(access(row, target, "s", "p", project, "epoch", true, true, false, true)[action], false, `${action}: uncertain`);
    assert.equal(access(row, target, "s", "p", { ...project, archived: true })[action], false, `${action}: archived project`);
  }
  // Copying changes nothing: it is offered while the window is busy with another change, not on a read-only, stale or unmatched one.
  assert.equal(access(row, target, "s", "p", project, "epoch", true, false, true, true)["copy-resume-command"], true);
  assert.equal(access(row, target, "s", "p", project, "epoch", false)["copy-resume-command"], false);
  assert.equal(access(row, target, "s", "p", project, null)["copy-resume-command"], false);
  assert.equal(access({ ...row, workspacePath: "/other" })["copy-resume-command"], false);
  assert.equal(access(row, target, "other")["copy-resume-command"], false);
});

test("context keys do not steal composer typing/IME; menu arrows and Escape restore focus safely", () => {
  assert.equal(isSessionContextKey("ContextMenu", false, false, false), true);
  assert.equal(isSessionContextKey("F10", true, false, false), true);
  assert.equal(isSessionContextKey("F10", false, false, false), false);
  assert.equal(isSessionContextKey("ContextMenu", false, true, false), false);
  assert.equal(isSessionContextKey("F10", true, false, true), false);
  assert.equal(menuFocusIndex("ArrowDown", 2, 3), 0);
  assert.equal(menuFocusIndex("ArrowUp", 0, 3), 2);
  assert.equal(menuFocusIndex("ArrowUp", -1, 3), 2);
  assert.equal(menuFocusIndex("ArrowDown", -1, 3), 0);
  assert.equal(menuFocusIndex("Home", 2, 3), 0);
  assert.equal(menuFocusIndex("End", 0, 3), 2);
  assert.equal(menuFocusIndex("Escape", 0, 3), null);
  assert.equal(menuFocusIndex("ArrowUp", 0, 0), null);
  let focused = 0;
  restoreSessionMenuFocus({ isConnected: false, focus: () => focused++ });
  restoreSessionMenuFocus({ isConnected: true, focus: () => focused++ });
  assert.equal(focused, 1);
});

test("Delete alone, pressed once, asks to delete the session of a row", () => {
  const key = { key: "Delete", altKey: false, ctrlKey: false, metaKey: false, shiftKey: false, repeat: false };
  assert.equal(isSessionDeleteKey(key, false), true);
  assert.equal(isSessionDeleteKey(key, true), false, "not while a text is being composed");
  assert.equal(isSessionDeleteKey({ ...key, repeat: true }, false), false, "a key held down deletes one session, not the list");
  for (const modifier of ["altKey", "ctrlKey", "metaKey", "shiftKey"] as const)
    assert.equal(isSessionDeleteKey({ ...key, [modifier]: true }, false), false, modifier);
  for (const other of ["Backspace", "d", "Enter"]) assert.equal(isSessionDeleteKey({ ...key, key: other }, false), false, other);
});
