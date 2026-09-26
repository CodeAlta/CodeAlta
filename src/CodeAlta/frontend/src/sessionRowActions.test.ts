import assert from "node:assert/strict";
import test from "node:test";
import type { WorkspaceProject, WorkspaceSession } from "#neoastra";
import { isSessionContextKey, menuFocusIndex, restoreSessionMenuFocus, sessionActionAccess, type SessionMenuTarget } from "./sessionRowActions";

const project: WorkspaceProject = { id: "p", name: "Project", path: "/p", archived: false };
const row: WorkspaceSession = { createdAt: null, id: "s", title: "Title", fullTitle: "Title", fullTitleTruncated: false,
  parentSessionId: null, scopeKind: "project", projectId: "p", lineageIssue: null, workspacePath: "/p",
  providerKey: null, updatedAt: "2026-01-01T00:00:00Z" };
const target: SessionMenuTarget = { id: "s", projectId: "p", hostEpoch: "epoch" };
const access = (session = row, menu = target, selected: string | null = "s", scope: string | null = "p",
  selectedProject: WorkspaceProject | undefined = project, epoch: string | null = "epoch", owned = true,
  canMutate = true, busy = false, uncertain = false) =>
  sessionActionAccess(session, menu, selected, scope, selectedProject, epoch, owned, canMutate, busy, uncertain);

test("unselected row must establish exact selection and project scope before context mutation", () => {
  assert.deepEqual(access(row, target, "other"), { open: false, rename: false, delete: false });
  assert.deepEqual(access(row, target, "s"), { open: true, rename: true, delete: true });
  assert.equal(access(row, target, "s", "other").open, false);
  assert.equal(access({ ...row, id: "other" }, target).open, false);
  assert.equal(access(row, { ...target, projectId: "other" }).open, false);
  assert.equal(access(row, target, "s", "p", project, "next").open, false);
});

test("project/global headers are exact; unmatched, archived, stale, read-only and locked rows cannot mutate", () => {
  const global = { ...row, scopeKind: "global", projectId: null };
  const globalTarget = { ...target, projectId: null };
  assert.deepEqual(access(global, globalTarget, "s", null, undefined), { open: true, rename: true, delete: true });
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
