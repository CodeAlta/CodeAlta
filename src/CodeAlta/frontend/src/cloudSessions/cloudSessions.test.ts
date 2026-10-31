import assert from "node:assert/strict";
import test from "node:test";
import type { CloudReadinessResponse, CloudSessionDetailsResponse, CloudStartRequest, CloudStartResponse } from "#neoastra";
import { translate } from "../localization";
import { cloudPlace, persistWorkPlaces, restoreWorkPlaces, withWorkPlace } from "../worktrees/worktrees";
import { bringBackCommand, claudeAiLink, cloudMessages, cloudOf, continueFailure, inCloud, pastedCloudSession, providerHasCloudSessions, readinessLines,
  readPendingCloudStart, runCloudStart, sendFailure, startFailure, startStepKey } from "./cloudSessions";

const en = (key: Parameters<typeof translate>[1], parameters?: Readonly<Record<string, string | number>>) => translate("en", key, parameters);
const id = "session_01QK5u8xRwDmhdvgs5BDTzPp";
const readiness = (change: Partial<CloudReadinessResponse> = {}): CloudReadinessResponse => ({ status: "ok", epoch: "e", repository: true, branch: "main", unpushed: 0,
  changed: 0, gitHub: true, account: "subscription", windows: true, folderBusy: false, ...change });
const started = (change: Partial<CloudStartResponse> = {}): CloudStartResponse => ({ status: "created", epoch: "e", requestId: "r", sessionId: "s", cloudSessionId: id,
  url: `https://claude.ai/code/${id}`, message: null, terminalId: null, step: null, ...change });

test("only Claude Code runs sessions in the cloud", () => {
  assert.equal(providerHasCloudSessions("claude-code"), true);
  for (const other of ["codex", "anthropic", "openai", "", null, undefined]) assert.equal(providerHasCloudSessions(other), false, String(other));
});

test("the cloud session of a row is kept only well formed, and its link only when it is one of claude.ai", () => {
  assert.equal(cloudOf({ cloud: null }), null);
  assert.equal(cloudOf({ cloud: { id: "not an id", url: null, title: null, continued: false, continuing: false } }), null);
  const view = cloudOf({ cloud: { id, url: `https://claude.ai/code/${id}`, title: "  Fix it  ", continued: false, continuing: true } });
  assert.deepEqual(view, { id, url: `https://claude.ai/code/${id}`, title: "Fix it", continued: false, continuing: true });
  assert.equal(cloudOf({ cloud: { id, url: "javascript:alert(1)", title: null, continued: false, continuing: false } })?.url, null);
  assert.equal(claudeAiLink("https://claude.ai.evil.example/x"), null, "a host that only starts like claude.ai is another one");
  assert.equal(inCloud({ cloud: { id, url: null, title: null, continued: false, continuing: false } }), true);
  assert.equal(inCloud({ cloud: { id, url: null, title: null, continued: true, continuing: false } }), false, "brought back: it works here now");
  assert.equal(inCloud({ cloud: null }), false);
});

test("a pasted link of claude.ai/code or an identifier names a cloud session; anything else does not", () => {
  assert.equal(pastedCloudSession(`https://claude.ai/code/${id}?from=cli&m=0`), id);
  assert.equal(pastedCloudSession(`  ${id}  `), id);
  assert.equal(pastedCloudSession("cse_01ABCDEFGHJKLMN"), "cse_01ABCDEFGHJKLMN");
  for (const other of ["", "hello", `http://claude.ai/code/${id}`, `https://example.com/code/${id}`, "https://claude.ai/code/", "session_short"])
    assert.equal(pastedCloudSession(other), null, other);
});

test("the readiness says the account, the branch, what is not on GitHub and what stays here, and always the plan", () => {
  const lines = readinessLines(readiness({ unpushed: 2, changed: 3 }), "Claude Code (Max)").map(line => [line.tone, en(line.key, line.parameters)]);
  assert.deepEqual(lines, [
    ["ok", "Claude Code (Max) is signed in with a Claude plan"],
    ["ok", "Starts from the branch main"],
    ["warning", "2 commits are not on GitHub: the cloud session starts without them."],
    ["warning", "3 files have changes that are not committed: they stay here, unless Claude Code uploads the folder with them."],
    ["info", "A cloud session counts against your Claude plan. CodeAlta's statistics do not see it."],
  ]);
  assert.deepEqual(readinessLines(readiness({ unpushed: 1, changed: 1 }), "x").map(line => line.key).slice(2, 4),
    ["1 commit is not on GitHub: the cloud session starts without it.", "1 file has changes that are not committed: they stay here, unless Claude Code uploads the folder with them."]);
  assert.equal(readinessLines(readiness({ unpushed: null }), "x")[2].key, "The branch {branch} is not on GitHub: the cloud session may start from another branch.");
  assert.equal(readinessLines(readiness({ gitHub: false, unpushed: 4 }), "x")[2].key, "The repository is not on GitHub: Claude Code uploads it, and the changes made in the cloud come back as the conversation only, not as a branch.",
    "without GitHub the folder is uploaded: no commit is left behind");
  const none = readinessLines(readiness({ repository: false, branch: null, account: "unknown" }), "x").map(line => line.key);
  assert.deepEqual(none, ["Claude Code checks the sign-in of {provider} when the session starts.",
    "The folder is not in a git repository: Claude Code may refuse to start a cloud session from it.", "A cloud session counts against your Claude plan. CodeAlta's statistics do not see it."]);
  assert.equal(readinessLines(readiness({ account: "not_subscription" }), "x")[0].tone, "warning");
});

test("each outcome has a sentence of ours, and keeps what Claude Code said", () => {
  assert.equal(startStepKey("asking"), "Claude Code asks a question: answer it in the terminal tab.");
  assert.equal(startStepKey("starting"), "Starting Claude Code…");
  assert.deepEqual(startFailure("needs_claude_ai", " Error: sign in "), { key: "Cloud sessions need Claude Code signed in with a claude.ai account (Pro, Max, Team or Enterprise).", said: "Error: sign in" });
  assert.equal(startFailure("weird", null).key, "Claude Code could not start the cloud session.");
  assert.equal(sendFailure("archived", null).key, "The cloud session is archived on claude.ai: it takes no more messages. You can still continue it here.");
  assert.equal(sendFailure("failed", "boom").said, "boom");
  const dirty = continueFailure({ status: "uncommitted_changes", message: null, changedFiles: 2 });
  assert.equal(en(dirty.key, dirty.parameters), "2 files of the folder have changes that are not committed: Claude Code would switch its branch. Commit them, or continue in a new worktree.");
  assert.equal(continueFailure({ status: "failed", message: "Branch not found", changedFiles: 0 }).said, "Branch not found");
});

test("a start reports its steps while it runs, and a lost answer is read again by its request", async () => {
  const request: CloudStartRequest = { expectedEpoch: "e", requestId: "r", projectId: "p", providerKey: "claude", spaceId: null, title: null, task: "Fix it" };
  const steps: string[] = [];
  let finish!: (value: CloudStartResponse) => void;
  let reads = 0;
  const api = {
    start: () => new Promise<CloudStartResponse>(resolve => { finish = resolve; }),
    startState: async () => { reads++; return started({ status: "running", step: reads > 1 ? "asking" : "starting" }); },
  };
  let ticks = 0;
  const wait = async () => { ticks++; await new Promise(resolve => setImmediate(resolve)); if (ticks === 3) finish(started()); };
  const outcome = await runCloudStart(api as never, request, step => steps.push(step), wait);
  assert.equal(outcome.status, "created");
  assert.deepEqual([...new Set(steps)], ["starting", "asking"]);

  // The answer is lost: the host still has the outcome.
  let polled = 0;
  const lost = {
    start: async () => { throw new Error("timeout"); },
    startState: async () => ++polled < 3 ? started({ status: "running", step: "asking" }) : started({ status: "failed", message: "no" }),
  };
  const read = await runCloudStart(lost as never, request, () => { }, async () => { await new Promise(resolve => setImmediate(resolve)); });
  assert.deepEqual([read.status, read.message], ["failed", "no"]);

  // The answer never comes: the outcome the host keeps ends the wait.
  const silent = { start: () => new Promise<CloudStartResponse>(() => { }), startState: async () => started() };
  assert.equal((await runCloudStart(silent as never, request, () => { }, async () => { await new Promise(resolve => setImmediate(resolve)); })).status, "created");

  // A reading of the state that hangs does not hold an answer that came.
  const stuck = { start: async () => started(), startState: () => new Promise<CloudStartResponse>(() => { }) };
  assert.equal((await runCloudStart(stuck as never, request, () => { }, async () => { await new Promise(resolve => setImmediate(resolve)); })).status, "created");

  // After a reload there is no answer to wait for: a request the host does not know ends as lost.
  let asked = false;
  const reloaded = { start: async () => { asked = true; return started(); }, startState: async () => started({ status: "unknown" }) };
  assert.equal((await runCloudStart(reloaded as never, request, () => { }, async () => { await new Promise(resolve => setImmediate(resolve)); }, true)).status, "lost");
  assert.equal(asked, false, "Nothing is started again.");

  // A host that no longer knows the request once the answer was lost: the start is lost, and the window is not held.
  const forgotten = { start: async () => { throw new Error("closed"); }, startState: async () => started({ status: "unknown" }) };
  assert.equal((await runCloudStart(forgotten as never, request, () => { }, async () => { await new Promise(resolve => setImmediate(resolve)); })).status, "lost");
});

test("the card lists the task first, then what was sent", () => {
  const details: CloudSessionDetailsResponse = { status: "ok", id, url: null, title: "Fix", task: "Fix the login test", branch: "main", startedAt: "2026-10-31T21:11:00Z",
    note: null, sent: [{ text: "Also add a test", at: "2026-10-31T21:40:00Z" }, { text: "  ", at: "2026-10-31T21:41:00Z" }], continuing: false, continuedAt: null, inTerminal: false };
  assert.deepEqual(cloudMessages(details), [
    { text: "Fix the login test", at: "2026-10-31T21:11:00Z", task: true },
    { text: "Also add a test", at: "2026-10-31T21:40:00Z", task: false },
  ]);
  assert.deepEqual(cloudMessages({ ...details, status: "not_cloud" }), []);
  assert.deepEqual(cloudMessages(null), []);
});

test("a return in a terminal runs the executable of the provider, quoted for the shell", () => {
  const command = "claude --teleport session_01QK5u8xRwDmhdvgs5BDTzPp --session-id 0f0e0d0c-0b0a-4908-8706-050403020100";
  assert.equal(bringBackCommand(command, "C:\\Users\\me\\.alta\\cache\\claude.exe", { windows: true, home: "C:\\Users\\me" }),
    "& \"$HOME\\.alta\\cache\\claude.exe\" --teleport session_01QK5u8xRwDmhdvgs5BDTzPp --session-id 0f0e0d0c-0b0a-4908-8706-050403020100");
  assert.equal(bringBackCommand(command, "/opt/claude bin/claude", { windows: false, home: "/home/me" }).startsWith("\"/opt/claude bin/claude\" --teleport "), true);
  assert.equal(bringBackCommand(command, null, { windows: true, home: null }), command, "Without an executable: claude, as the host wrote it.");
});

test("a start a reload finds is read back whole, and anything else is none", () => {
  const pending = { requestId: "r", projectId: "p", epoch: "e" };
  assert.deepEqual(readPendingCloudStart(() => JSON.stringify({ ...pending, task: "Fix it" })), pending);
  assert.deepEqual(readPendingCloudStart(() => JSON.stringify({ ...pending, task: "x".repeat(16_000) })), pending, "with the longest task");
  for (const raw of [null, "", "{", "[]", JSON.stringify({ requestId: "r", projectId: "p" }), JSON.stringify({ ...pending, epoch: 3 }), "x".repeat(300_000)])
    assert.equal(readPendingCloudStart(() => raw), null, String(raw).slice(0, 20));
});

test("the cloud place of a project is kept, and read back as the cloud", () => {
  let stored: string | null = null;
  const places = withWorkPlace(withWorkPlace(new Map(), "a", cloudPlace), "b", { worktree: true, base: null });
  assert.equal(persistWorkPlaces(value => { stored = value; }, places), true);
  const restored = restoreWorkPlaces(() => stored);
  assert.deepEqual(restored.get("a"), { worktree: false, base: null, cloud: true });
  assert.deepEqual(restored.get("b"), { worktree: true, base: null });
  assert.equal(withWorkPlace(restored, "a", { worktree: false, base: null }).has("a"), false, "back to the folder: nothing is kept");
});
