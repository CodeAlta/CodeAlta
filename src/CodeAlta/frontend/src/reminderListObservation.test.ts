import assert from "node:assert/strict";
import test from "node:test";
import type { ReminderListResponse, WorkspaceSession, WorkspaceSnapshot } from "#neoastra";
import { validReminderList, verifiedReminderCountTarget } from "./reminderListObservation";

const session: WorkspaceSession = { createdAt: null, id: "one", title: "One", fullTitle: "One", fullTitleTruncated: false,
  parentSessionId: null, scopeKind: "project", projectId: "p", lineageIssue: null,
  workspacePath: "/p", providerKey: "fixture", updatedAt: "2026-09-24T00:00:00Z" };
const snapshot: WorkspaceSnapshot = { configured: true, projects: [{ id: "p", name: "Project", path: "/p", archived: false }],
  sessions: [session], projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false };

test("reminder count target requires one exact selected persisted project/global scope", () => {
  assert.equal(verifiedReminderCountTarget(snapshot, session, "p"), true);
  assert.equal(verifiedReminderCountTarget(snapshot, session, null), false, "unmatched project rows in the global rail are not global owners");
  assert.equal(verifiedReminderCountTarget(snapshot, session, "other"), false);
  assert.equal(verifiedReminderCountTarget({ ...snapshot, projects: [{ ...snapshot.projects[0], archived: true }] }, session, "p"), false);
  assert.equal(verifiedReminderCountTarget({ ...snapshot, projects: [...snapshot.projects, { ...snapshot.projects[0] }] }, session, "p"), false);
  assert.equal(verifiedReminderCountTarget({ ...snapshot, sessions: [session, { ...session }] }, session, "p"), false);
  assert.equal(verifiedReminderCountTarget({ ...snapshot, projectsTruncated: true }, session, "p"), false);
  assert.equal(verifiedReminderCountTarget({ ...snapshot, sessionsTruncated: true }, session, "p"), false);
  const global = { ...session, scopeKind: "global", projectId: null };
  assert.equal(verifiedReminderCountTarget({ ...snapshot, sessions: [global] }, global, null), true);
  assert.equal(verifiedReminderCountTarget({ ...snapshot, sessions: [global] }, global, "p"), false);
});

test("only complete, exact, active-only host lists can certify a count, including zero", () => {
  const row = { id: "a", state: "active", preview: "prompt", delaySeconds: 300, repeatCount: 1,
    firedCount: 0, dueAt: null, lastExitCode: null, lastError: null };
  const target = { epoch: "e1", sessionId: "one" };
  const page: ReminderListResponse = { status: "ok", epoch: "e1", sessionId: "one", reminders: [row],
    activeCount: 1, completedCount: 0 };
  assert.equal(validReminderList(target, page), true);
  assert.equal(validReminderList(target, { ...page, reminders: [], activeCount: 0 }), true);
  assert.equal(validReminderList(target, { ...page, reminders: [{ ...row, state: "completed" }], activeCount: 0, completedCount: 1 }), true);
  for (const invalid of [
    { ...page, status: "read_failed", reminders: [], activeCount: 0 },
    { ...page, epoch: "other" }, { ...page, sessionId: "other" },
    { ...page, activeCount: 0 }, { ...page, reminders: undefined },
    { ...page, reminders: [row, row], activeCount: 2 },
    { ...page, reminders: Array(33).fill(row), activeCount: 33 },
  ] as ReminderListResponse[]) assert.equal(validReminderList(target, invalid), false);
});
