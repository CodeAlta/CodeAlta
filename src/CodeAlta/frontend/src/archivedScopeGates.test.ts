import assert from "node:assert/strict";
import test from "node:test";
import type { WorkspaceSnapshot } from "#neoastra";
import { archivedProjectScope, archivedRecoveryTarget } from "./ArchivedScopeGates";

const session = { messageCount: null, createdAt: null, id: "one", title: "One", fullTitle: "One", fullTitleTruncated: false, parentSessionId: null,
  scopeKind: "project" as const, projectId: "project", workspacePath: "/original", providerKey: "fixture",
  updatedAt: "2026-09-24T00:00:00Z", lineageIssue: null };
const snapshot: WorkspaceSnapshot = { configured: true, projects: [{ id: "project", name: "P", path: "/original", archived: true }],
  sessions: [session], projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false };

test("archived recovery demands one exact project path, scoped session and unique ID", () => {
  assert.equal(archivedProjectScope(snapshot, "project"), true);
  assert.equal(archivedRecoveryTarget(snapshot, "project", session, "epoch"), true);
  assert.equal(archivedRecoveryTarget(snapshot, "other", session, "epoch"), false);
  assert.equal(archivedRecoveryTarget(snapshot, "project", session, null), false);
  assert.equal(archivedRecoveryTarget(snapshot, "project", { ...session, workspacePath: "/other" }, "epoch"), false);
  assert.equal(archivedRecoveryTarget(snapshot, "project", { ...session, projectId: "other" }, "epoch"), false);
  assert.equal(archivedRecoveryTarget(snapshot, "project", { ...session, scopeKind: "global" }, "epoch"), false);
  assert.equal(archivedRecoveryTarget({ ...snapshot, sessions: [...snapshot.sessions, session] }, "project", session, "epoch"), false);
  assert.equal(archivedRecoveryTarget({ ...snapshot, sessions: [{ ...session, projectId: "other" }] }, "project", session, "epoch"), false);
  assert.equal(archivedRecoveryTarget({ ...snapshot, projects: [...snapshot.projects, { ...snapshot.projects[0], path: "/other" }] }, "project", session, "epoch"), false);
  assert.equal(archivedRecoveryTarget({ ...snapshot, projects: [{ ...snapshot.projects[0], archived: false }] }, "project", session, "epoch"), false);
});
