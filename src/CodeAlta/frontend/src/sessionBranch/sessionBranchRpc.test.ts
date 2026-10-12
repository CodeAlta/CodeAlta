import assert from "node:assert/strict";
import test from "node:test";
import type { WorkspaceSnapshot } from "#neoastra";
import { createMutationCapability } from "../sessionOperations";
import { messages } from "../localization";
import { branchMessage, branchTarget, createSessionBranch, outcomeUnknown, readSessionTurns, turnsMessage, turnsOutdated } from "./sessionBranchRpc";

const epoch = "11111111-1111-4111-8111-111111111111";
const other = "22222222-2222-4222-8222-222222222222";
const project = { scope: "project" as const, projectId: "p1", projectPath: "C:\\test-owned\\project" };
const global = { scope: "global" as const, projectPath: "C:\\test-owned\\global" };
const echo = (target: typeof project | typeof global = project) => ({ hostEpoch: epoch, scope: target.scope,
  projectId: target.scope === "project" ? target.projectId : null, projectPath: target.projectPath, sessionId: "s1" });
const forked = (overrides = {}) => ({ status: "ok", ...echo(), forkSessionId: "f1", title: "Session (fork)", turnCount: 3, prompt: "fix it", promptTruncated: false, ...overrides });
const rewound = (overrides = {}) => ({ status: "ok", ...echo(), turnCount: 3, removedTurnCount: 2, prompt: "fix it", promptTruncated: false,
  removedTurnsSessionId: "k1", removedTurnsTitle: "Session (before rewind)", rewindCount: 1, ...overrides });
const refused = (status: string, target: typeof project | typeof global | null = project) => target
  ? { status, ...echo(target), forkSessionId: null, title: null, turnCount: 0, removedTurnCount: 0, prompt: null, promptTruncated: false,
    removedTurnsSessionId: null, removedTurnsTitle: null, rewindCount: 0 }
  : { status, hostEpoch: epoch, scope: null, projectId: null, projectPath: null, sessionId: "s1", forkSessionId: null, title: null, turnCount: 0,
    removedTurnCount: 0, prompt: null, promptTruncated: false, removedTurnsSessionId: null, removedTurnsTitle: null, rewindCount: 0 };
const never = async () => { throw new Error("not expected"); };

test("a fork sends the exact session, scope and point once and returns what the host created", async () => {
  const calls: unknown[] = [];
  const branch = createSessionBranch(async (request, options) => {
    calls.push(request);
    assert.equal(options.timeoutMilliseconds, 120_000);
    return request.scope === "global" ? forked({ ...echo(global), forkSessionId: "g2", prompt: null, turnCount: 5 }) : forked();
  }, never);
  const capability = createMutationCapability(epoch);
  const result = await branch.fork(epoch, project, "s1", "run-4", capability);
  assert.deepEqual(result, { kind: "forked", target: project, sourceId: "s1", id: "f1", title: "Session (fork)", turnCount: 3, prompt: "fix it", promptTruncated: false });
  assert.deepEqual(calls[0], { expectedHostEpoch: epoch, ...project, sessionId: "s1", beforeRunId: "run-4", title: null });
  // The end of the session has no turn to stop before; a chat has no project.
  const whole = await branch.fork(epoch, global, "s1", null, capability);
  assert.deepEqual(calls[1], { expectedHostEpoch: epoch, scope: "global", projectId: null, projectPath: global.projectPath, sessionId: "s1", beforeRunId: null, title: null });
  assert.deepEqual(whole, { kind: "forked", target: global, sourceId: "s1", id: "g2", title: "Session (fork)", turnCount: 5, prompt: null, promptTruncated: false });
  assert.equal(calls.length, 2);
  assert.equal(branch.busy(), false);
});

test("a rewind sends the turn and whether the removed turns are kept, and returns what was removed", async () => {
  const calls: unknown[] = [];
  const branch = createSessionBranch(never, async request => { calls.push(request); return request.keepRemovedTurns ? rewound()
    : rewound({ removedTurnsSessionId: null, removedTurnsTitle: null, rewindCount: 2 }); });
  const capability = createMutationCapability(epoch);
  assert.deepEqual(await branch.rewind(epoch, project, "s1", "run-4", true, capability), { kind: "rewound", target: project, id: "s1", turnCount: 3,
    removedTurnCount: 2, prompt: "fix it", promptTruncated: false, removedTurnsSessionId: "k1", removedTurnsTitle: "Session (before rewind)", rewindCount: 1 });
  assert.deepEqual(calls[0], { expectedHostEpoch: epoch, ...project, sessionId: "s1", beforeRunId: "run-4", keepRemovedTurns: true });
  const dropped = await branch.rewind(epoch, project, "s1", "run-4", false, capability);
  assert.equal(dropped.kind === "rewound" && dropped.removedTurnsSessionId, null);
  assert.equal(dropped.kind === "rewound" && dropped.rewindCount, 2);
  assert.deepEqual(calls[1], { expectedHostEpoch: epoch, ...project, sessionId: "s1", beforeRunId: "run-4", keepRemovedTurns: false });
});

test("nothing is sent without an owned host, a valid scope and a point; one change is made at a time", async () => {
  let calls = 0;
  let release: (() => void) | undefined;
  const branch = createSessionBranch(async () => { calls++; await new Promise<void>(resolve => { release = resolve; }); return forked(); },
    async () => { calls++; return rewound(); });
  const capability = createMutationCapability(epoch);
  assert.deepEqual(await branch.fork(undefined, project, "s1", null, capability), { kind: "error", code: "unconfigured" });
  assert.deepEqual(await branch.fork(epoch, project, "s1", null, undefined), { kind: "error", code: "unconfigured" });
  assert.deepEqual(await branch.fork(epoch, project, " s1", null, capability), { kind: "error", code: "invalid_scope" });
  assert.deepEqual(await branch.fork(epoch, { ...project, projectPath: "" }, "s1", null, capability), { kind: "error", code: "invalid_scope" });
  assert.deepEqual(await branch.fork(epoch, project, "s1", "run\n", capability), { kind: "error", code: "invalid_scope" });
  assert.deepEqual(await branch.rewind(epoch, project, "s1", "", true, capability), { kind: "error", code: "invalid_scope" });
  assert.equal(calls, 0);
  // A fork is under way: a rewind asked meanwhile is refused here, and nothing is sent for it.
  const pending = branch.fork(epoch, project, "s1", null, capability);
  assert.equal(branch.busy(), true);
  assert.deepEqual(await branch.rewind(epoch, project, "s1", "run-4", true, capability), { kind: "error", code: "busy" });
  assert.deepEqual(await branch.fork(epoch, project, "s1", null, capability), { kind: "error", code: "busy" });
  assert.equal(calls, 1);
  release!();
  assert.equal((await pending).kind, "forked");
  assert.equal(branch.busy(), false);
  assert.equal((await branch.rewind(epoch, project, "s1", "run-4", true, capability)).kind, "rewound");
});

test("every refusal of the host is returned as it is, with or without the scope of the request", async () => {
  const capability = createMutationCapability(epoch);
  for (const status of ["scope_missing", "session_missing", "session_archived", "turn_not_found", "turn_running", "session_in_use", "interaction_pending",
    "queue_pending", "background_work", "session_changed", "unsupported_format", "io_failed"]) {
    const branch = createSessionBranch(async () => refused(status), async () => refused(status));
    assert.deepEqual(await branch.fork(epoch, project, "s1", "run-4", capability), { kind: "error", code: status });
    assert.deepEqual(await branch.rewind(epoch, project, "s1", "run-4", true, capability), { kind: "error", code: status });
  }
  // What the host refuses before it reads the request names no scope.
  for (const status of ["busy", "closed", "invalid_scope", "unconfigured"]) {
    const branch = createSessionBranch(async () => refused(status, null), async () => refused(status, null));
    assert.deepEqual(await branch.fork(epoch, project, "s1", null, capability), { kind: "error", code: status });
    assert.deepEqual(await branch.rewind(epoch, project, "s1", "run-4", true, capability), { kind: "error", code: status });
  }
  assert.equal(capability.canMutate(), true);
});

test("an answer that is lost, late or about something else leaves the outcome unknown, and is not asked again", async () => {
  let calls = 0;
  const capability = createMutationCapability(epoch);
  const lost = createSessionBranch(async () => { calls++; throw new Error("response lost"); }, async () => { calls++; throw new Error("response lost"); });
  assert.deepEqual(await lost.fork(epoch, project, "s1", null, capability), { kind: "error", code: "fork_unconfirmed" });
  assert.deepEqual(await lost.rewind(epoch, project, "s1", "run-4", true, capability), { kind: "error", code: "rewind_unconfirmed" });
  assert.equal(calls, 2);
  assert.equal(lost.busy(), false);
  const wrongSession = createSessionBranch(async () => forked({ sessionId: "s2" }), async () => rewound({ projectPath: "C:\\elsewhere" }));
  assert.deepEqual(await wrongSession.fork(epoch, project, "s1", null, capability), { kind: "error", code: "fork_unconfirmed" });
  assert.deepEqual(await wrongSession.rewind(epoch, project, "s1", "run-4", true, capability), { kind: "error", code: "rewind_unconfirmed" });
  // A fork that is the session itself, a fork without an identity and a rewind that removed nothing are not what was asked.
  const odd = createSessionBranch(async () => forked({ forkSessionId: "s1" }), async () => rewound({ removedTurnCount: 0 }));
  assert.deepEqual(await odd.fork(epoch, project, "s1", null, capability), { kind: "error", code: "fork_unconfirmed" });
  assert.deepEqual(await odd.rewind(epoch, project, "s1", "run-4", true, capability), { kind: "error", code: "rewind_unconfirmed" });
  const empty = createSessionBranch(async () => forked({ forkSessionId: null }), async () => null as never);
  assert.deepEqual(await empty.fork(epoch, project, "s1", null, capability), { kind: "error", code: "fork_unconfirmed" });
  assert.deepEqual(await empty.rewind(epoch, project, "s1", "run-4", true, capability), { kind: "error", code: "rewind_unconfirmed" });
  assert.equal(outcomeUnknown("fork_unconfirmed") && outcomeUnknown("rewind_unconfirmed"), true);
  assert.equal(outcomeUnknown("session_in_use"), false);
  assert.match(branchMessage("fork_unconfirmed", "fork"), /not created again automatically/);
  assert.match(branchMessage("rewind_unconfirmed", "rewind"), /not made again automatically/);
});

test("a host that changed ends the authority of the window: nothing more is sent", async () => {
  let calls = 0;
  const capability = createMutationCapability(epoch);
  const branch = createSessionBranch(async () => { calls++; return { ...refused("stale_epoch", null), hostEpoch: other }; }, async () => { calls++; return rewound(); });
  assert.deepEqual(await branch.fork(epoch, project, "s1", null, capability), { kind: "error", code: "stale_epoch" });
  assert.equal(capability.canMutate(), false);
  assert.deepEqual(await branch.rewind(epoch, project, "s1", "run-4", true, capability), { kind: "error", code: "unconfigured" });
  assert.equal(calls, 1);
  // An answer of another host is not the answer to this request, whatever it says.
  const fresh = createMutationCapability(epoch);
  const foreign = createSessionBranch(async () => forked({ hostEpoch: other }), never);
  assert.deepEqual(await foreign.fork(epoch, project, "s1", null, fresh), { kind: "error", code: "stale_epoch" });
});

test("the turns of a session are read by page, and a page of another session or host is refused", async () => {
  const turn = { number: 7, runId: "run-7", status: "completed", startedAt: "2026-10-12T10:00:00Z", endedAt: null, promptOffset: "70", prompt: "p", promptTruncated: false,
    sourceSessionId: null, answersQuestion: false, steers: 0, assistantMessages: 1, toolCalls: 2, compactions: 0 };
  const page = (overrides = {}) => ({ status: "ok", hostEpoch: epoch, sessionId: "s1", turns: [turn], total: 7, activeRunId: null, revision: null, ...overrides });
  const requests: unknown[] = [];
  const abort = new AbortController();
  const read = await readSessionTurns(async (request, options) => { requests.push(request); assert.equal(options.signal, abort.signal); return page(); }, epoch, "s1", null, abort.signal);
  assert.deepEqual(read, { kind: "ok", turns: [turn], total: 7, activeRunId: null });
  await readSessionTurns(async request => { requests.push(request); return page({ activeRunId: "run-7" }); }, epoch, "s1", 7, abort.signal);
  assert.deepEqual(requests, [{ expectedHostEpoch: epoch, sessionId: "s1", beforeTurn: null }, { expectedHostEpoch: epoch, sessionId: "s1", beforeTurn: 7 }]);
  assert.deepEqual(await readSessionTurns(async () => page({ sessionId: "s2" }), epoch, "s1", null, abort.signal), { kind: "error", code: "read_failed" });
  assert.deepEqual(await readSessionTurns(async () => page({ hostEpoch: other }), epoch, "s1", null, abort.signal), { kind: "error", code: "stale_epoch" });
  assert.deepEqual(await readSessionTurns(async () => page({ total: 0 }), epoch, "s1", null, abort.signal), { kind: "error", code: "read_failed" });
  for (const status of ["unconfigured", "invalid_scope", "stale_epoch", "session_missing", "read_failed"])
    assert.deepEqual(await readSessionTurns(async () => page({ status, turns: [], total: 0 }), epoch, "s1", null, abort.signal), { kind: "error", code: status });
  assert.deepEqual(await readSessionTurns(async () => { throw new Error("lost"); }, epoch, "s1", null, abort.signal), { kind: "error", code: "read_failed" });
  assert.deepEqual(await readSessionTurns(never as never, undefined, "s1", null, abort.signal), { kind: "error", code: "unconfigured" });
});

test("the scope of a change is the chats or the exact project of the pane, never a guess", () => {
  const snapshot: WorkspaceSnapshot = { configured: true, projects: [{ id: "p1", name: "Project", path: project.projectPath, archived: false },
    { id: "p2", name: "Archived", path: "C:\\test-owned\\old", archived: true }], sessions: [], projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false };
  const inProject = { scopeKind: "project", projectId: "p1", workspacePath: project.projectPath };
  assert.deepEqual(branchTarget(snapshot, inProject, "p1"), project);
  assert.deepEqual(branchTarget(snapshot, { scopeKind: "global", projectId: null, workspacePath: global.projectPath }, null), global);
  assert.equal(branchTarget(snapshot, inProject, null), null);
  assert.equal(branchTarget(snapshot, inProject, "p2"), null);
  assert.equal(branchTarget(snapshot, { ...inProject, workspacePath: "C:\\moved" }, "p1"), null);
  assert.equal(branchTarget(snapshot, { scopeKind: "project", projectId: "p2", workspacePath: "C:\\test-owned\\old" }, "p2"), null);
  assert.equal(branchTarget(snapshot, { scopeKind: "global", projectId: null, workspacePath: null }, null), null);
  assert.equal(branchTarget(undefined, inProject, "p1"), null);
});

test("each status has a message of its own, in every language", () => {
  const statuses = ["busy", "closed", "stale_epoch", "invalid_scope", "scope_missing", "unconfigured", "session_missing", "session_archived", "turn_not_found",
    "turn_running", "session_in_use", "interaction_pending", "queue_pending", "background_work", "session_changed", "unsupported_format", "io_failed",
    "fork_unconfirmed", "rewind_unconfirmed"];
  for (const mode of ["fork", "rewind"] as const) for (const status of statuses) assert.ok(messages[branchMessage(status, mode)], `${mode} ${status}`);
  // Only the two statuses that name the same thing share a message.
  const distinct = new Set(statuses.filter(status => !["scope_missing", "fork_unconfirmed"].includes(status)).map(status => branchMessage(status, "rewind")));
  assert.equal(distinct.size, statuses.length - 2);
  assert.notEqual(branchMessage("unconfigured", "fork"), branchMessage("unconfigured", "rewind"));
  for (const status of ["unconfigured", "stale_epoch", "session_missing", "read_failed", "invalid_scope"]) assert.ok(messages[turnsMessage(status)]);
  assert.equal(turnsOutdated("session_changed") && turnsOutdated("turn_not_found"), true);
  assert.equal(turnsOutdated("session_in_use"), false);
});
