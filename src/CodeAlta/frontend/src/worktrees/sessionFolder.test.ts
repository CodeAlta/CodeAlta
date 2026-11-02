import assert from "node:assert/strict";
import test from "node:test";
import type { WorkspaceSwitchSessionFolderRequest, WorkspaceSwitchSessionFolderResponse } from "#neoastra";
import { createSessionFolderSwitch, folderTargets, sessionFolderMessage } from "./sessionFolder";
import type { Worktree } from "./worktrees";

const epoch = "11111111-1111-4111-8111-111111111111";
const project = { projectId: "p1", projectPath: "C:\\code\\stride" };
const reply = (request: WorkspaceSwitchSessionFolderRequest, overrides: Partial<WorkspaceSwitchSessionFolderResponse> = {}): WorkspaceSwitchSessionFolderResponse => ({
  status: "ok", hostEpoch: epoch, sessionId: request.sessionId,
  worktreePath: request.target === "project" ? null : request.folder ?? "C:\\worktrees\\stride\\new-tree", branch: request.target === "new" ? "alta/new-tree" : null,
  warnings: [], message: null, reason: null, ...overrides });
const worktree = (path: string, overrides: Partial<Worktree> = {}): Worktree => ({ path, name: path.split("\\").pop()!, branch: "main", head: "abc1234", main: false,
  project: false, locked: false, missing: false, folder: path, busy: false, ...overrides });

test("a change of folder sends the exact session, project and target once, and waits as long as a checkout takes", async () => {
  const calls: WorkspaceSwitchSessionFolderRequest[] = [];
  const switchFolder = createSessionFolderSwitch(async (request, options) => {
    calls.push(request);
    assert.equal(options.timeoutMilliseconds, 600_000);
    return reply(request);
  });

  const existing = await switchFolder(epoch, project, "s1", { kind: "worktree", folder: "C:\\code\\stride4" });
  assert.deepEqual(existing, { kind: "switched", worktreePath: "C:\\code\\stride4", branch: null, warnings: [] });
  const created = await switchFolder(epoch, project, "s1", { kind: "new", base: "main" });
  assert.deepEqual(created, { kind: "switched", worktreePath: "C:\\worktrees\\stride\\new-tree", branch: "alta/new-tree", warnings: [] });
  const home = await switchFolder(epoch, project, "s1", { kind: "project" });
  assert.deepEqual(home, { kind: "switched", worktreePath: null, branch: null, warnings: [] });
  assert.deepEqual(calls.map(call => [call.target, call.folder, call.baseBranch]), [["worktree", "C:\\code\\stride4", null], ["new", null, "main"], ["project", null, null]]);
  assert.ok(calls.every(call => call.expectedHostEpoch === epoch && call.sessionId === "s1" && call.projectId === "p1" && call.projectPath === project.projectPath));
});

test("what cannot be sent is refused before the host is asked", async () => {
  let calls = 0;
  const switchFolder = createSessionFolderSwitch(async request => { calls++; return reply(request); });
  assert.deepEqual(await switchFolder(null, project, "s1", { kind: "project" }), { kind: "error", code: "unconfigured", message: null });
  assert.deepEqual(await switchFolder(epoch, project, "", { kind: "project" }), { kind: "error", code: "invalid_scope", message: null });
  assert.deepEqual(await switchFolder(epoch, project, "s1", { kind: "worktree", folder: " C:\\x" }), { kind: "error", code: "invalid_scope", message: null });
  assert.deepEqual(await switchFolder(epoch, project, "s1", { kind: "new", base: "--force" }), { kind: "error", code: "invalid_scope", message: null });
  assert.equal(calls, 0);
});

test("one change at a time: a second one while the first waits is busy", async () => {
  let release!: () => void;
  const switchFolder = createSessionFolderSwitch(request => new Promise(resolve => { release = () => resolve(reply(request)); }));
  const first = switchFolder(epoch, project, "s1", { kind: "project" });
  assert.deepEqual(await switchFolder(epoch, project, "s1", { kind: "project" }), { kind: "error", code: "busy", message: null });
  release();
  assert.equal((await first).kind, "switched");
});

test("a refusal says why, and an answer that does not match the request is unconfirmed", async () => {
  const refuse = (overrides: Partial<WorkspaceSwitchSessionFolderResponse>) => createSessionFolderSwitch(async request => reply(request, overrides));
  assert.deepEqual(await refuse({ status: "busy", worktreePath: null })(epoch, project, "s1", { kind: "project" }), { kind: "error", code: "busy", message: null });
  assert.deepEqual(await refuse({ status: "worktree_failed", message: "fatal: invalid reference" })(epoch, project, "s1", { kind: "new", base: null }),
    { kind: "error", code: "worktree_failed", message: "fatal: invalid reference" });
  assert.deepEqual(await refuse({ sessionId: "other" })(epoch, project, "s1", { kind: "project" }), { kind: "error", code: "switch_unconfirmed", message: null });
  assert.deepEqual(await refuse({ hostEpoch: "22222222-2222-4222-8222-222222222222", status: "stale_epoch" })(epoch, project, "s1", { kind: "project" }),
    { kind: "error", code: "stale_epoch", message: null });
  assert.deepEqual(await refuse({ worktreePath: "C:\\code\\stride4" })(epoch, project, "s1", { kind: "project" }), { kind: "error", code: "switch_unconfirmed", message: null },
    "A session sent to the folder of its project has no worktree after the change.");
  assert.deepEqual(await createSessionFolderSwitch(async () => { throw new Error("closed"); })(epoch, project, "s1", { kind: "project" }),
    { kind: "error", code: "switch_unconfirmed", message: null });
  assert.equal(sessionFolderMessage("background_running"), "Background commands of the session run in its folder: change it once they end.");
  assert.equal(sessionFolderMessage("other_repository"), "That folder is not a checkout of the repository of this project.");
  assert.equal(sessionFolderMessage("anything"), "The folder of the session could not be changed.");
});

test("the checkouts offered are those that are there, but the one the session works in", () => {
  const main = worktree("C:\\code\\stride", { main: true, project: true });
  const four = worktree("C:\\code\\stride4", { branch: "feature/fleet" });
  const gone = worktree("C:\\worktrees\\gone", { missing: true });
  const tree = worktree("C:\\worktrees\\stride\\bold-fox");
  assert.deepEqual(folderTargets([main, four, gone, tree], null), [four, tree], "A session in the folder of the project is offered the worktrees.");
  assert.deepEqual(folderTargets([main, four, gone, tree], "c:/code/stride4/"), [main, tree], "A session in a worktree is offered the folder of the project and the others.");
});
