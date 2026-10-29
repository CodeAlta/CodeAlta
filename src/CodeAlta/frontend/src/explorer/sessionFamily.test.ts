import assert from "node:assert/strict";
import test from "node:test";
import type { WorkspaceSession } from "#neoastra";
import { sessionFamilyOf } from "./sessionFamily";

const row = (id: string, parentSessionId: string | null, projectId: string | null): WorkspaceSession => ({
  messageCount: null, automationId: null, worktreePath: null, worktreeRoot: null, worktreeName: null, worktreeMissing: false, forkedFromSessionId: null, forkedFromTitle: null, forkKind: null, forkTurnCount: 0, rewindCount: 0, archived: false, archivedAt: null, spaceId: null, createdAt: null, id, title: id, fullTitle: id, fullTitleTruncated: false, parentSessionId,
  scopeKind: projectId === null ? "global" : "project", projectId, workspacePath: projectId ? `/${projectId}` : "/global", lineageIssue: null, providerKey: "fixture", updatedAt: "2026-01-01T00:00:00Z",
});

test("the family of a session is its parent and its children, with itself, and the scope each is listed in", () => {
  const sessions = [row("root", null, "god"), row("child", "root", "web"), row("chat", "root", null), row("grandchild", "child", "keycloak"),
    { ...row("archived", "root", "web"), archived: true }, row("alone", null, "god")];
  const family = (id: string) => { const value = sessionFamilyOf(sessions, id); return [[...value.ids].sort(), [...value.scopes.entries()].sort()]; };
  assert.deepEqual(family("root"), [["chat", "child", "root"], [["chat", ""], ["child", "web"], ["root", "god"]]]);
  assert.deepEqual(family("child"), [["child", "grandchild", "root"], [["child", "web"], ["grandchild", "keycloak"], ["root", "god"]]]);
  // A session with no family marks nothing, not even itself; an unknown one neither.
  assert.deepEqual(family("alone"), [[], []]);
  assert.deepEqual(family("absent"), [[], []]);
});
