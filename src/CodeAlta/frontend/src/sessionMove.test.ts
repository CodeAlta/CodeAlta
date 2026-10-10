import assert from "node:assert/strict";
import test from "node:test";
import type { WorkspaceMoveSessionRequest, WorkspaceSession, WorkspaceSnapshot } from "#neoastra";
import { moveTargets } from "./MoveSessionDialog";
import { createMutationCapability } from "./sessionOperations";
import { createSessionMove, movedSessionsVisible, retargetSessionTabs, sessionMoveMessage, splitPendingTabMoves } from "./sessionMove";
import { reconcileSessionTabs, tabKey } from "./sessionTabs";

const epoch = "11111111-1111-4111-8111-111111111111";
const other = "22222222-2222-4222-8222-222222222222";
const from = { projectId: "p1", projectPath: "C:\\code\\stride" };
const target = { projectId: "p2", projectPath: "C:\\code\\stride2" };
const moved = (sessionId: string, worktreePath: string | null = null) => ({ sessionId, projectId: target.projectId, projectPath: target.projectPath, worktreePath });
const reply = (request: WorkspaceMoveSessionRequest, status = "ok", overrides = {}) => ({ status, hostEpoch: epoch, sessionId: request.sessionId,
  projectId: request.projectId, projectPath: request.projectPath, targetProjectId: request.targetProjectId, targetProjectPath: request.targetProjectPath,
  moved: status === "ok" ? [moved("child"), moved(request.sessionId)] : [], warnings: [], ...overrides });
const session = (id: string, projectId: string, workspacePath: string): WorkspaceSession => ({ messageCount: null, automationId: null, worktreePath: null,
  worktreeRoot: null, worktreeName: null, worktreeMissing: false, forkedFromSessionId: null, forkedFromTitle: null, forkKind: null, forkTurnCount: 0, rewindCount: 0,
  createdAt: null, id, title: id, fullTitle: id, fullTitleTruncated: false, parentSessionId: null,
  scopeKind: "project", projectId, lineageIssue: null, workspacePath, providerKey: "fixture", updatedAt: "2026-01-01T00:00:00Z" });
const snapshot = (sessions: WorkspaceSession[]): WorkspaceSnapshot => ({ configured: true, sessions,
  projects: [{ id: "p1", name: "stride", path: from.projectPath, archived: false }, { id: "p2", name: "stride2", path: target.projectPath, archived: false },
    { id: "p3", name: "old", path: "C:\\code\\old", archived: true }],
  projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false });

test("a move sends both exact scopes once and returns what the host moved", async () => {
  const calls: WorkspaceMoveSessionRequest[] = [];
  const move = createSessionMove(async (request, options) => {
    calls.push(request);
    assert.equal(options.timeoutMilliseconds, 60_000);
    return reply(request, "ok", { warnings: ["kept the transcript"] });
  });
  const result = await move(epoch, from, target, "s1", createMutationCapability(epoch));
  assert.deepEqual(calls, [{ expectedHostEpoch: epoch, sessionId: "s1", projectId: "p1", projectPath: from.projectPath,
    targetProjectId: "p2", targetProjectPath: target.projectPath }]);
  assert.deepEqual(result, { kind: "moved", id: "s1", target, moved: [moved("child"), moved("s1")], warnings: ["kept the transcript"] });
});

test("refusals, a stale host and replies that do not match the request are never taken for a move", async () => {
  let calls = 0;
  const answer = (overrides: object, status = "ok") => createSessionMove(async request => { calls++; return reply(request, status, overrides); });
  const capability = () => createMutationCapability(epoch);
  assert.deepEqual(await answer({})(epoch, from, from, "s1", capability()), { kind: "error", code: "same_project", moved: [] });
  assert.deepEqual(await answer({})(epoch, from, target, " s1", capability()), { kind: "error", code: "invalid_scope", moved: [] });
  assert.deepEqual(await answer({})(undefined, from, target, "s1", capability()), { kind: "error", code: "unconfigured", moved: [] });
  assert.equal(calls, 0, "What is refused before the request sends nothing.");
  assert.equal((await answer({}, "session_in_use")(epoch, from, target, "s1", capability())).kind, "error");
  assert.deepEqual(await answer({ hostEpoch: other })(epoch, from, target, "s1", capability()), { kind: "error", code: "stale_epoch", moved: [] });
  assert.deepEqual(await answer({ targetProjectPath: from.projectPath })(epoch, from, target, "s1", capability()),
    { kind: "error", code: "move_unconfirmed", moved: [] });
  assert.deepEqual(await answer({ moved: [{ ...moved("s1"), projectId: "p3" }] })(epoch, from, target, "s1", capability()),
    { kind: "error", code: "move_unconfirmed", moved: [] }, "A session said to move elsewhere than asked is not a move.");
  assert.deepEqual(await answer({ moved: [moved("child")] })(epoch, from, target, "s1", capability()),
    { kind: "error", code: "move_unconfirmed", moved: [moved("child")] }, "A move that does not include the session is not its move.");
  assert.deepEqual(await answer({ moved: [moved("child")] }, "move_partial")(epoch, from, target, "s1", capability()),
    { kind: "error", code: "move_partial", moved: [moved("child")] });
  const failing = createSessionMove(async () => { throw new Error("lost"); });
  assert.deepEqual(await failing(epoch, from, target, "s1", capability()), { kind: "error", code: "move_unconfirmed", moved: [] });
  for (const code of ["session_in_use", "move_partial", "anything"]) assert.ok(sessionMoveMessage(code).length > 0);
});

test("the tabs of moved sessions follow them, keep their place, and survive the next reconciliation", () => {
  const stride = (id: string) => ({ sessionId: id, projectId: "p1", path: from.projectPath });
  const state = { open: [stride("a"), stride("s1"), stride("child")], active: stride("s1"), closed: [stride("old")] };
  const after = retargetSessionTabs(state, [moved("child"), moved("s1")]);
  assert.deepEqual(after.open.map(tab => tab.sessionId), ["a", "s1", "child"]);
  assert.deepEqual(after.active, { sessionId: "s1", projectId: "p2", path: target.projectPath });
  assert.equal(after.open[0], state.open[0], "A tab of a session that did not move is the same tab.");
  assert.equal(retargetSessionTabs(after, [moved("s1")]), after, "Nothing changes for tabs already in place.");
  const fresh = snapshot([session("a", "p1", from.projectPath), session("s1", "p2", target.projectPath), session("child", "p2", target.projectPath),
    session("old", "p1", from.projectPath)]);
  assert.deepEqual(reconcileSessionTabs(after, fresh).open.map(tabKey), after.open.map(tabKey));
  assert.deepEqual(reconcileSessionTabs(state, fresh).open.map(tab => tab.sessionId), ["a"], "Without the retarget, the moved tabs are dropped.");
  assert.equal(movedSessionsVisible(fresh, [moved("s1"), moved("child")]), true);
  assert.equal(movedSessionsVisible(snapshot([session("s1", "p1", from.projectPath)]), [moved("s1")]), false);
});

test("the tabs of a move whose fresh list could not be read follow it with the first list that shows it", () => {
  const pending = [moved("child"), moved("s1")];
  // A list read before the move, or of a space without the session, shows neither: both wait.
  const before = splitPendingTabMoves(snapshot([session("s1", "p1", from.projectPath)]), pending);
  assert.deepEqual(before, { shown: [], waiting: pending });
  // The next list shows one of them in its project, the other not yet: the one shown follows now.
  const half = splitPendingTabMoves(snapshot([session("s1", "p2", target.projectPath)]), pending);
  assert.deepEqual(half, { shown: [moved("s1")], waiting: [moved("child")] });
  assert.deepEqual(splitPendingTabMoves({ ...snapshot([]), configured: false }, pending), { shown: [], waiting: pending });
});

test("the move window offers the other projects that are not archived, filtered by name or folder", () => {
  const projects = snapshot([]).projects;
  assert.deepEqual(moveTargets(projects, "p1", "").map(project => project.id), ["p2"]);
  assert.deepEqual(moveTargets(projects, "p2", "STRIDE").map(project => project.id), ["p1"]);
  assert.deepEqual(moveTargets(projects, "p1", "old"), []);
});
