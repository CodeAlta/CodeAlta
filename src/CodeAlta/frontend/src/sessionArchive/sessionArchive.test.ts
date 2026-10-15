import assert from "node:assert/strict";
import test from "node:test";
import type { WorkspaceArchiveSessionRequest, WorkspaceArchiveSessionResponse, WorkspaceSession } from "#neoastra";
import { archivableDescendants, sessionArchiveMessage, setSessionArchived } from "./sessionArchive";

const epoch = "11111111-1111-4111-8111-111111111111";
const session = (id: string, parentSessionId: string | null = null, archived = false): WorkspaceSession => ({
  id, title: id, fullTitle: id, fullTitleTruncated: false, workspacePath: "/chats", providerKey: "codex", updatedAt: "2026-10-14T10:00:00Z",
  createdAt: null, parentSessionId, scopeKind: "global", projectId: null, lineageIssue: null, messageCount: null, automationId: null,
  worktreePath: null, worktreeRoot: null, worktreeName: null, worktreeMissing: false, forkedFromSessionId: null, forkedFromTitle: null,
  forkKind: null, forkTurnCount: 0, rewindCount: 0, archived, archivedAt: archived ? "2026-10-15T08:00:00Z" : null, spaceId: null,
});

test("an archive asks the host for the exact session, and says what changed or why nothing did", async () => {
  const requests: WorkspaceArchiveSessionRequest[] = [];
  const reply = (response: Partial<WorkspaceArchiveSessionResponse>) => async (request: WorkspaceArchiveSessionRequest) => {
    requests.push(request);
    return { status: "ok", hostEpoch: epoch, sessionId: request.sessionId, changed: [], blockingSessionId: null, ...response } as WorkspaceArchiveSessionResponse;
  };

  assert.deepEqual(await setSessionArchived(reply({ changed: ["root", "child"] }), epoch, "root", true), { kind: "done", changed: ["root", "child"] });
  assert.deepEqual(requests.at(-1), { expectedHostEpoch: epoch, sessionId: "root", withChildren: true });
  // The session at work below it is named.
  assert.deepEqual(await setSessionArchived(reply({ status: "session_in_use", blockingSessionId: "child" }), epoch, "root", false),
    { kind: "error", code: "session_in_use", changed: [], blocking: "child" });
  // An answer about another session or another host confirms nothing.
  assert.equal((await setSessionArchived(reply({ sessionId: "other" }), epoch, "root", false)).kind, "error");
  assert.deepEqual(await setSessionArchived(reply({ hostEpoch: "22222222-2222-4222-8222-222222222222" }), epoch, "root", false),
    { kind: "error", code: "archive_unconfirmed", changed: [], blocking: null });
  assert.deepEqual(await setSessionArchived(async () => { throw new Error("transport"); }, epoch, "root", false),
    { kind: "error", code: "archive_unconfirmed", changed: [], blocking: null });
  const sent = requests.length;
  assert.deepEqual(await setSessionArchived(reply({}), null, "root", false), { kind: "error", code: "unconfigured", changed: [], blocking: null });
  assert.equal(requests.length, sent, "Without a host nothing is sent.");
});

test("the sessions an archive takes with it are those below the session that are not archived yet", () => {
  const sessions = [session("root"), session("child", "root"), session("grandchild", "child"), session("archived", "root", true),
    session("below-archived", "archived"), session("other"), session("loop-a", "loop-b"), session("loop-b", "loop-a")];
  assert.deepEqual(new Set(archivableDescendants(sessions, "root").map(value => value.id)), new Set(["child", "grandchild", "below-archived"]),
    "An archived session is not counted, the sessions below it are.");
  assert.deepEqual(archivableDescendants(sessions, "other"), []);
  assert.deepEqual(archivableDescendants(sessions, "loop-a").map(value => value.id), ["loop-b"], "A loop of parents ends.");
});

test("why an archive was refused is said in the words of the window", () => {
  assert.match(sessionArchiveMessage("session_in_use"), /at work/);
  assert.match(sessionArchiveMessage("queue_pending"), /queued prompts/);
  assert.match(sessionArchiveMessage("interaction_pending"), /waits for an answer/);
  assert.match(sessionArchiveMessage("anything else"), /could not be confirmed/);
});
