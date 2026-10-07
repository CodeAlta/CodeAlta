import assert from "node:assert/strict";
import test from "node:test";
import type { ReminderActiveResponse, WorkspaceSession, WorkspaceSnapshot } from "#neoastra";
import { activeReminderCounts, sameActiveReminders, scopeReminderCount } from "./activeReminders";

const reply = (patch: Partial<ReminderActiveResponse> = {}): ReminderActiveResponse => ({ status: "ok", epoch: "e1",
  sessions: [{ sessionId: "one", activeCount: 2, nextDueAt: null }, { sessionId: "global", activeCount: 1, nextDueAt: "2026-10-03T10:00:00+00:00" }], ...patch });
const session = (id: string, projectId: string | null): WorkspaceSession => ({ messageCount: null, automationId: null, worktreePath: null, worktreeRoot: null, worktreeName: null, worktreeMissing: false, createdAt: null, id, title: id, fullTitle: id,
  fullTitleTruncated: false, parentSessionId: null, scopeKind: projectId ? "project" : "global", projectId, lineageIssue: null,
  workspacePath: projectId ? "/p" : null, providerKey: "fixture", updatedAt: "2026-09-24T00:00:00Z" });
const snapshot: WorkspaceSnapshot = { configured: true, projects: [{ id: "p", name: "Project", path: "/p", archived: false }],
  sessions: [session("one", "p"), session("two", "p"), session("global", null)], projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false };

test("only an exact successful reply establishes active reminder counts", () => {
  assert.deepEqual([...activeReminderCounts(reply(), "e1")!], [["one", 2], ["global", 1]]);
  assert.equal(activeReminderCounts(reply({ sessions: [] }), "e1")!.size, 0);
  assert.equal(activeReminderCounts(reply(), "other"), null);
  assert.equal(activeReminderCounts(reply({ status: "closed" }), "e1"), null);
  assert.equal(activeReminderCounts(reply({ sessions: [{ sessionId: "one", activeCount: 0, nextDueAt: null }] }), "e1"), null);
  assert.equal(activeReminderCounts(reply({ sessions: [{ sessionId: "one", activeCount: 1, nextDueAt: null }, { sessionId: "one", activeCount: 1, nextDueAt: null }] }), "e1"), null);
});

test("unchanged counts compare equal and scopes sum the sessions they show", () => {
  const counts = activeReminderCounts(reply(), "e1")!;
  assert.equal(sameActiveReminders(counts, new Map([["global", 1], ["one", 2]])), true);
  assert.equal(sameActiveReminders(counts, new Map([["one", 2]])), false);
  assert.equal(sameActiveReminders(counts, new Map([["one", 3], ["global", 1]])), false);
  assert.equal(scopeReminderCount(counts, snapshot, "p"), 2);
  assert.equal(scopeReminderCount(counts, snapshot, null), 1);
  assert.equal(scopeReminderCount(counts, snapshot, "missing"), 0);
  assert.equal(scopeReminderCount(new Map(), snapshot, "p"), 0);
  assert.equal(scopeReminderCount(counts, undefined, "p"), 0);
});
