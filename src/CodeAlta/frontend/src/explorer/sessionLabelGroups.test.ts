import assert from "node:assert/strict";
import test from "node:test";
import type { LabelItem, WorkspaceSession } from "#neoastra";
import { readLabels } from "../labels/labels";
import { sessionHierarchy } from "../sessionHierarchy";
import { groupSessionsByLabels, sessionLabelGroupKey } from "./sessionLabelGroups";
import { sessionList, type SessionTreeEntry } from "./sessionTree";

const row = (id: string, labels: string[] = [], parentSessionId: string | null = null, day = 1): WorkspaceSession => ({
  messageCount: null, automationId: null, worktreePath: null, worktreeRoot: null, worktreeName: null, worktreeMissing: false, forkedFromSessionId: null, forkedFromTitle: null,
  forkKind: null, forkTurnCount: 0, rewindCount: 0, archived: false, archivedAt: null, spaceId: null, createdAt: null, id, title: id, fullTitle: id, fullTitleTruncated: false,
  parentSessionId, scopeKind: "project", projectId: "p1", workspacePath: "/p1", lineageIssue: null, providerKey: "fixture",
  updatedAt: `2026-01-${String(day).padStart(2, "0")}T00:00:00Z`, ...labels.length ? { labels } : {},
});
const item = (id: string, look: Partial<LabelItem> = {}): LabelItem =>
  ({ id, name: id.toUpperCase(), description: null, icon: null, color: null, order: 1, parent: null, favorite: false, space: null, path: id, projectIds: [], forSessions: true, ...look });
// In the order of their tree: audit, then pr-review.
const labels = readLabels([item("audit"), item("pr-review", { order: 2 })]);
const entries = (sessions: WorkspaceSession[]) => sessionList(sessionHierarchy(sessions, sessions, "p1"),
  { count: 50, subCount: 4, active: null, extra: () => 0, collapsed: () => false }).entries;
const lines = (list: readonly SessionTreeEntry[]) => list.map(entry => entry.kind === "label" ? `# ${entry.label?.id ?? "other"} ${entry.sessions.length}${entry.folded ? " folded" : ""}`
  : entry.kind === "session" ? `${entry.row.session.id}@${entry.row.depth}` : entry.kind);

const sessions = [row("review", ["pr-review"], null, 9), row("audit-1", ["audit"], null, 8), row("child", [], "audit-1", 7), row("both", ["pr-review", "audit"], null, 6),
  row("hands-on", [], null, 5)];

test("the sessions of a scope are listed under the first of their labels, in the order of the labels, with their sub-agents", () => {
  assert.deepEqual(lines(groupSessionsByLabels(entries(sessions), labels, "p1", () => false)),
    ["# audit 2", "audit-1@0", "child@1", "both@0", "# pr-review 1", "review@0", "# other 1", "hands-on@0"]);
});

test("a folded group hides its sessions and still counts them", () => {
  const folded = new Set([sessionLabelGroupKey("p1", "audit"), sessionLabelGroupKey("p1", null)]);
  assert.deepEqual(lines(groupSessionsByLabels(entries(sessions), labels, "p1", key => folded.has(key))),
    ["# audit 2 folded", "# pr-review 1", "review@0", "# other 1 folded"]);
});

test("a list where no session has a label of the space needs no title", () => {
  const plain = entries([row("a"), row("b", ["gone"])]);
  assert.deepEqual(lines(groupSessionsByLabels(plain, labels, "p1", () => false)), ["a@0", "b@0"]);
  assert.equal(sessionLabelGroupKey("", null), "sessions::");
});
