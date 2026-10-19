import assert from "node:assert/strict";
import test from "node:test";
import type {
  WorkspaceClaudeCodeSession,
  WorkspaceClaudeCodeSessionConversationResponse,
  WorkspaceClaudeCodeSessionsRequest,
  WorkspaceClaudeCodeSessionsResponse,
  WorkspaceImportClaudeCodeSessionResponse,
  WorkspaceOpenClaudeCodeSessionResponse,
} from "#neoastra";
import { translate, type MessageKey } from "../localization";
import {
  copyEntryResumeCommand,
  entryMenuItems,
  entryTag,
  entryRefusal,
  hasClaudeCodeProvider,
  importClaudeCodeSession,
  importErrorMessage,
  holderMark,
  inUseElsewhere,
  listClaudeCodeSessions,
  notShown,
  openEntryInVsCode,
  originMark,
  readClaudeCodeSessions,
  runEntryInTerminal,
  withHolders,
  withPage,
  type ClaudeCodeSessionsApi,
} from "./claudeCodeSessions";

const epoch = "11111111-1111-4111-8111-111111111111";
const project = { id: "p1", path: "C:\\dev\\p1" };
const cli = "0f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e11";
const session = { cliSessionId: cli, title: "Fix the parser", lastChangedAt: "2026-10-14T10:00:00Z", origin: "vscode", held: true, holderStatus: "busy", tag: null, startDirectory: null,
  holderOrigin: "vscode" };
const numbered = (index: number): WorkspaceClaudeCodeSession =>
  ({ ...session, cliSessionId: `00000000-0000-4000-8000-${String(index).padStart(12, "0")}`, title: `Session ${index}`, held: false, holderStatus: null, holderOrigin: null });
const listed = (overrides: Partial<WorkspaceClaudeCodeSessionsResponse> = {}): WorkspaceClaudeCodeSessionsResponse =>
  ({ status: "ok", hostEpoch: epoch, projectId: project.id, projectPath: project.path, providerId: "claude-code", offset: 0, sessions: [session], total: 3, ...overrides });
const imported = (overrides: Partial<WorkspaceImportClaudeCodeSessionResponse> = {}): WorkspaceImportClaudeCodeSessionResponse =>
  ({ status: "ok", hostEpoch: epoch, projectId: project.id, projectPath: project.path, cliSessionId: cli, sessionId: "s-new", ...overrides });
const described = (overrides: Partial<WorkspaceClaudeCodeSessionConversationResponse> = {}): WorkspaceClaudeCodeSessionConversationResponse =>
  ({ status: "ok", hostEpoch: epoch, projectId: project.id, projectPath: project.path, cliSessionId: cli, resumeCommand: `claude --resume ${cli}`,
    workingDirectory: project.path, held: null, configDir: null, windows: true, home: null, ...overrides });
const opened = (overrides: Partial<WorkspaceOpenClaudeCodeSessionResponse> = {}): WorkspaceOpenClaudeCodeSessionResponse =>
  ({ status: "opened", hostEpoch: epoch, projectId: project.id, projectPath: project.path, cliSessionId: cli, ...overrides });
const api = (list: (request: WorkspaceClaudeCodeSessionsRequest) => Promise<WorkspaceClaudeCodeSessionsResponse>,
  more: Partial<{ importing: () => Promise<WorkspaceImportClaudeCodeSessionResponse>; conversation: () => Promise<WorkspaceClaudeCodeSessionConversationResponse>;
    open: () => Promise<WorkspaceOpenClaudeCodeSessionResponse> }> = {}) => {
  const requests: unknown[] = [];
  const value: ClaudeCodeSessionsApi = {
    list: async request => { requests.push(request); return list(request); },
    import: async request => { requests.push(request); return (more.importing ?? (async () => imported()))(); },
    conversation: async request => { requests.push(request); return (more.conversation ?? (async () => described()))(); },
    openInVsCode: async request => { requests.push(request); return (more.open ?? (async () => opened()))(); },
  };
  return { value, requests };
};
const page = (from: number, count: number) => Array.from({ length: count }, (_, index) => numbered(from + index));
// A host that holds `total` sessions and answers each page as it is asked.
const host = (total: number) => api(async request => listed({ offset: request.offset, total,
  sessions: page(request.offset, Math.max(0, Math.min(request.count, total - request.offset))) }));

test("only a Claude Code provider has sessions started elsewhere", () => {
  assert.equal(hasClaudeCodeProvider([{ type: "openai" }, { type: "claude-code" }]), true);
  assert.equal(hasClaudeCodeProvider([{ type: "anthropic" }, { type: null }, {}]), false);
  assert.equal(hasClaudeCodeProvider(new Map([["a", { type: "claude-code" }]]).values()), true);
});

test("a listing asks for a page of the exact project and keeps only a reply about it", async () => {
  const ok = api(async () => listed());
  assert.deepEqual(await listClaudeCodeSessions(ok.value, epoch, project), { kind: "listed", sessions: [session], total: 3, providerId: "claude-code" });
  assert.deepEqual(ok.requests, [{ expectedHostEpoch: epoch, projectId: "p1", projectPath: "C:\\dev\\p1", offset: 0, count: 20, spaceId: null }], "the first 20 at first");
  assert.deepEqual(await listClaudeCodeSessions(api(async () => listed({ status: "no_provider", sessions: [], total: 0 })).value, epoch, project), { kind: "none" });
  for (const reply of [{ projectPath: "C:\\dev\\other" }, { hostEpoch: "other" }, { total: 0 }, { offset: 20 }, { sessions: page(0, 21), total: 40 }])
    assert.deepEqual(await listClaudeCodeSessions(api(async () => listed(reply)).value, epoch, project), { kind: "error", code: "invalid_reply" }, JSON.stringify(reply).slice(0, 40));
  assert.deepEqual(await listClaudeCodeSessions(api(async () => listed({ status: "stale_epoch" })).value, epoch, project), { kind: "error", code: "stale_epoch" });
  assert.deepEqual(await listClaudeCodeSessions(api(async () => { throw new Error("lost"); }).value, epoch, project), { kind: "error", code: "read_failed" });
  // A page further on: a reply of that page, whose total counts what comes before it.
  const later = api(async () => listed({ offset: 20, sessions: page(20, 2), total: 22 }));
  assert.equal((await listClaudeCodeSessions(later.value, epoch, project, 20, 50)).kind, "listed");
  assert.deepEqual(await listClaudeCodeSessions(api(async () => listed({ offset: 20, sessions: page(20, 2), total: 21 })).value, epoch, project, 20, 50),
    { kind: "error", code: "invalid_reply" });
  // Without a host, nothing is asked.
  const none = api(async () => listed());
  assert.deepEqual(await listClaudeCodeSessions(none.value, null, project), { kind: "error", code: "stale_epoch" });
  assert.equal(none.requests.length, 0);
});

test("the sessions of no project are asked for without a project, and a reply about a project is not theirs", async () => {
  const unplaced = api(async () => listed({ projectId: null, projectPath: null, sessions: [{ ...session, tag: "C:\\work\\elsewhere", startDirectory: "C:\\work\\elsewhere" }], total: 1 }));
  const found = await listClaudeCodeSessions(unplaced.value, epoch, null);
  assert.equal(found.kind, "listed");
  assert.deepEqual(unplaced.requests, [{ expectedHostEpoch: epoch, projectId: null, projectPath: null, offset: 0, count: 20, spaceId: null }]);
  assert.equal(found.kind === "listed" ? entryTag(found.sessions[0]!) : null, "C:\\work\\elsewhere");
  assert.deepEqual(await listClaudeCodeSessions(api(async () => listed()).value, epoch, null), { kind: "error", code: "invalid_reply" });
  assert.equal(entryTag({ tag: "  " }), null, "a blank tag says nothing");
  assert.equal(entryTag({ tag: null }), null);
});

test("the group shows 20 at first, then 50 more at a time until the end, and reads again as many as it shows", async () => {
  const sixtyFour = host(64);
  const first = await readClaudeCodeSessions(sixtyFour.value, epoch, project, 20);
  assert.ok(first.kind === "listed");
  assert.deepEqual([first.sessions.length, first.total, notShown(first)], [20, 64, 44]);
  const more = await listClaudeCodeSessions(sixtyFour.value, epoch, project, first.sessions.length, 50);
  assert.ok(more.kind === "listed");
  const all = withPage(first, more);
  assert.deepEqual([all.sessions.length, notShown(all)], [64, 0], "every session can be reached");
  assert.deepEqual(all.sessions.map(value => value.title), page(0, 64).map(value => value.title));
  // Read again, the 64 shown come in pages of at most 50.
  const again = host(64);
  const read = await readClaudeCodeSessions(again.value, epoch, project, 64);
  assert.ok(read.kind === "listed");
  assert.equal(read.sessions.length, 64);
  assert.deepEqual(again.requests.map(request => [(request as WorkspaceClaudeCodeSessionsRequest).offset, (request as WorkspaceClaudeCodeSessionsRequest).count]), [[0, 50], [50, 14]]);
  // Fewer than asked for: the read ends at the end of the list.
  const shorter = host(30);
  const short = await readClaudeCodeSessions(shorter.value, epoch, project, 120);
  assert.ok(short.kind === "listed");
  assert.deepEqual([short.sessions.length, short.total, shorter.requests.length], [30, 30, 1]);
  // A session written again moved to the top meanwhile: it is not listed twice, and a page that brings nothing new ends the read.
  const moved = withPage({ kind: "listed", sessions: page(0, 2), total: 4, providerId: "claude-code" },
    { kind: "listed", sessions: [numbered(1), numbered(2)], total: 4, providerId: null });
  assert.deepEqual(moved.sessions.map(value => value.title), ["Session 0", "Session 1", "Session 2"]);
  assert.equal(moved.providerId, "claude-code");
  const stuck = api(async request => listed({ offset: request.offset, total: 60, sessions: request.offset === 0 ? page(0, 50) : page(40, 10) }));
  const stopped = await readClaudeCodeSessions(stuck.value, epoch, project, 60);
  assert.ok(stopped.kind === "listed");
  assert.equal(stopped.sessions.length, 50);
  assert.equal(stuck.requests.length, 2);
});

test("a row says where a session was started, and marks only one another process works on", () => {
  assert.deepEqual(["vscode", "terminal", "other", "unknown"].map(origin => originMark(origin)), [
    { icon: "code", label: "Started in VS Code" }, { icon: "terminal", label: "Started in a terminal" },
    { icon: "server", label: "Started in another program" }, { icon: "server", label: "Started in another program" }]);
  assert.equal(inUseElsewhere({ held: true, holderStatus: "busy" }), true);
  assert.equal(inUseElsewhere({ held: true, holderStatus: "waiting" }), true);
  assert.equal(inUseElsewhere({ held: true, holderStatus: null }), true, "a holder that does not say what it does may work on it");
  assert.equal(inUseElsewhere({ held: true, holderStatus: "idle" }), false, "a session merely open in a VS Code panel");
  assert.equal(inUseElsewhere({ held: false, holderStatus: null }), false);
});

test("a row says whether another Claude process works on a session now or merely has it open, and where that process runs", () => {
  assert.deepEqual(holderMark({ held: true, holderStatus: "busy", holderOrigin: "vscode" }), { working: true, label: "In use in VS Code now" });
  assert.deepEqual(holderMark({ held: true, holderStatus: "idle", holderOrigin: "vscode" }), { working: false, label: "Open in VS Code now" });
  assert.deepEqual(holderMark({ held: true, holderStatus: "waiting", holderOrigin: "terminal" }), { working: true, label: "In use in a terminal now" });
  assert.deepEqual(holderMark({ held: true, holderStatus: "idle", holderOrigin: "terminal" }), { working: false, label: "Open in a terminal now" });
  assert.deepEqual(holderMark({ held: true, holderStatus: "idle", holderOrigin: null }), { working: false, label: "Open in another Claude process now" });
  assert.deepEqual(holderMark({ held: true, holderStatus: null, holderOrigin: "background" }), { working: true, label: "In use in another Claude process now" });
  assert.equal(holderMark({ held: false, holderStatus: null, holderOrigin: null }), null);
});

test("the menu of a row opens its preview first, continues the session in CodeAlta or opens it where it was started, never deletes it", () => {
  const chosen: string[] = [];
  const items = entryMenuItems(key => key, { busy: false, terminal: true }, action => chosen.push(action));
  assert.deepEqual(items.map(item => "divider" in item ? null : [item.key, item.label, item.disabled]), [
    ["preview", "Open preview", false], ["continue", "Import", false], ["open-vs-code", "Open in VS Code", false],
    ["copy-resume-command", "Copy resume command", false], ["run-in-terminal", "Run in terminal", false]]);
  for (const item of items) if (!("divider" in item)) item.onSelect();
  assert.deepEqual(chosen, ["preview", "continue", "open-vs-code", "copy-resume-command", "run-in-terminal"]);
  assert.ok(items.every(item => !("divider" in item) && !item.danger), "no deletion");
  assert.deepEqual(entryMenuItems(key => key, { busy: true, terminal: true }, () => { }).map(item => "divider" in item ? null : item.disabled), [true, true, true, true, true],
    "nothing while an import or another action runs");
  assert.deepEqual(entryMenuItems(key => key, { busy: false, terminal: false }, () => { }).map(item => "divider" in item ? null : item.disabled), [false, false, false, false, true],
    "no terminal: no Run in terminal");
  // A project can be chosen to continue it in; a session of no project is continued only in a project chosen.
  assert.deepEqual(entryMenuItems(key => key, { busy: false, terminal: true, choose: true }, () => { }).map(item => "divider" in item ? null : item.key),
    ["preview", "continue", "continue-in-project", "open-vs-code", "copy-resume-command", "run-in-terminal"]);
  const unplaced: string[] = [];
  const items2 = entryMenuItems(key => key, { busy: false, terminal: false, listed: false, choose: true }, action => unplaced.push(action));
  assert.deepEqual(items2.map(item => "divider" in item ? null : [item.key, item.label]), [["preview", "Open preview"], ["continue-in-project", "Import into project…"],
    ["open-vs-code", "Open in VS Code"], ["copy-resume-command", "Copy resume command"], ["run-in-terminal", "Run in terminal"]]);
  for (const item of items2) if (!("divider" in item)) item.onSelect();
  assert.deepEqual(unplaced, ["preview", "continue-in-project", "open-vs-code", "copy-resume-command", "run-in-terminal"]);
});

test("an import opens the session that continues the conversation, new or already there", async () => {
  const ok = api(async () => listed());
  assert.deepEqual(await importClaudeCodeSession(ok.value, epoch, project, cli), { kind: "opened", sessionId: "s-new", existing: false });
  assert.deepEqual(ok.requests, [{ expectedHostEpoch: epoch, projectId: "p1", projectPath: "C:\\dev\\p1", cliSessionId: cli, spaceId: null }]);
  assert.deepEqual(await importClaudeCodeSession(api(async () => listed(), { importing: async () => imported({ status: "existing", sessionId: "s-old" }) }).value, epoch, project, cli),
    { kind: "opened", sessionId: "s-old", existing: true });
  assert.deepEqual(await importClaudeCodeSession(api(async () => listed(), { importing: async () => imported({ status: "not_found", sessionId: null }) }).value, epoch, project, cli),
    { kind: "error", code: "not_found" });
  // A reply about another conversation, or without a session, or lost, says nothing sure: the session may exist.
  for (const reply of [async () => imported({ cliSessionId: "another" }), async () => imported({ sessionId: null }), async () => { throw new Error("lost"); }])
    assert.deepEqual(await importClaudeCodeSession(api(async () => listed(), { importing: reply }).value, epoch, project, cli), { kind: "error", code: "import_unconfirmed" });
});

test("every call names the space the window shows: the host takes its provider, and an imported session belongs to it", async () => {
  const spaced = { ...project, spaceId: "business" };
  const calls = api(async () => listed());
  await listClaudeCodeSessions(calls.value, epoch, spaced);
  await importClaudeCodeSession(calls.value, epoch, spaced, cli);
  await openEntryInVsCode(calls.value, epoch, spaced, cli);
  await copyEntryResumeCommand(calls.value, epoch, spaced, cli, async () => { });
  assert.deepEqual(calls.requests.map(request => (request as { spaceId: string | null }).spaceId), ["business", "business", "business", "business"]);
  // Refused for the space: the window says so, not that the session is gone.
  for (const code of ["no_provider", "provider_outside_space"])
    assert.deepEqual(await importClaudeCodeSession(api(async () => listed(), { importing: async () => imported({ status: code, sessionId: null }) }).value, epoch, spaced, cli),
      { kind: "error", code });
  assert.equal(importErrorMessage("provider_outside_space"), "No Claude Code provider is available in the space this window shows: the session cannot be imported here.");
  assert.equal(importErrorMessage("space_missing"), "The space this window shows no longer exists. Show another space to import this Claude Code session.");
});

test("a session of the group opens in VS Code, or its command is copied or run, as the host gives it for the project", async () => {
  const vsCode = api(async () => listed());
  assert.deepEqual(await openEntryInVsCode(vsCode.value, epoch, project, cli), { ok: true, message: "Opened in VS Code." });
  assert.deepEqual(vsCode.requests, [{ expectedHostEpoch: epoch, projectId: "p1", projectPath: "C:\\dev\\p1", cliSessionId: cli, spaceId: null }]);
  assert.deepEqual(await openEntryInVsCode(api(async () => listed(), { open: async () => opened({ status: "open_failed" }) }).value, epoch, project, cli),
    { ok: false, message: "VS Code could not be opened. Check that it is installed." });
  // An answer about another session is not taken for an opening.
  assert.equal((await openEntryInVsCode(api(async () => listed(), { open: async () => opened({ cliSessionId: "another" }) }).value, epoch, project, cli)).ok, false);

  const written: string[] = [];
  const copy = api(async () => listed());
  assert.deepEqual(await copyEntryResumeCommand(copy.value, epoch, project, cli, async text => { written.push(text); }),
    { ok: true, message: "Resume command copied. Run it in a terminal opened in the folder of the project." });
  assert.deepEqual(written, [`claude --resume ${cli}`]);
  assert.equal((await copyEntryResumeCommand(api(async () => listed(), { conversation: async () => described({ resumeCommand: "claude --resume x && calc" }) }).value,
    epoch, project, cli, async text => { written.push(text); })).ok, false, "only the command of that session");
  assert.equal(written.length, 1);
  assert.deepEqual(await copyEntryResumeCommand(copy.value, epoch, project, cli, undefined), { ok: false, message: "Clipboard unavailable; nothing copied." });

  const typed: string[] = [];
  const terminal = { create: async () => ({ id: "t1", profile: "cmd" }), input: (id: string, data: string) => typed.push(`${id}:${data}`) };
  assert.deepEqual(await runEntryInTerminal(api(async () => listed()).value, epoch, project, cli, terminal),
    { ok: true, message: "The Claude Code session continues in the terminal." });
  assert.deepEqual(typed, [`t1:claude --resume ${cli}\r`]);
  assert.deepEqual(await runEntryInTerminal(api(async () => listed()).value, epoch, project, cli, { create: async () => null, input: () => typed.push("no") }),
    { ok: false, message: "The terminal could not be started." });
  // Refused: what the user can do, and whether the list is read again.
  assert.deepEqual(await runEntryInTerminal(api(async () => listed(), { conversation: async () => described({ status: "not_found" }) }).value, epoch, project, cli, terminal),
    { ok: false, message: "This Claude Code session changed or was removed. The list was read again.", reread: true });
  assert.equal(typed.length, 1, "nothing is typed for a refused session");
  assert.deepEqual(entryRefusal("existing"), { ok: false, message: "A session of CodeAlta continues this conversation now: open it from the list of sessions.", reread: true });
  assert.deepEqual(await openEntryInVsCode(vsCode.value, null, project, cli), { ok: false, message: "The window is out of date. Reload it and try again." });
});

test("a session of the group of a provider with an account folder is resumed there, and VS Code is refused with what to do", async () => {
  const account = api(async () => listed(), { conversation: async () => described({ configDir: "~/.claude-work", home: "C:\\Users\\me" }) });
  const prefixed = `$env:CLAUDE_CONFIG_DIR="$HOME\\.claude-work"; claude --resume ${cli}`;
  const written: string[] = [];
  assert.equal((await copyEntryResumeCommand(account.value, epoch, project, cli, async text => { written.push(text); })).ok, true);
  assert.deepEqual(written, [prefixed]);
  const typed: string[] = [];
  const terminal = (profile: string) => ({ create: async () => ({ id: "t1", profile }), input: (id: string, data: string) => typed.push(data) });
  assert.equal((await runEntryInTerminal(account.value, epoch, project, cli, terminal("powershell"))).ok, true);
  assert.deepEqual(typed, [`${prefixed}\r`]);
  assert.deepEqual(await runEntryInTerminal(account.value, epoch, project, cli, terminal("git-bash")),
    { ok: false, message: "The shell of this terminal cannot be given the account folder of the provider. Choose PowerShell or a POSIX shell for terminals in Settings, or use Copy resume command." });
  assert.equal(typed.length, 1, "nothing is typed in a shell that does not take it");
  assert.deepEqual(await openEntryInVsCode(api(async () => listed(), { open: async () => opened({ status: "account_folder" }) }).value, epoch, project, cli),
    { ok: false, message: "VS Code opens Claude Code conversations from its own account folder, not from the account folder of this provider. Use Run in terminal or Copy resume command." });
});

test("where a session was started, what it does elsewhere, and why an action failed are said in every language", () => {
  const keys: MessageKey[] = [
    ...["not_found", "busy", "stale_epoch", "import_unconfirmed", "closed", "provider_outside_space", "space_missing"].map(importErrorMessage),
    ...["not_found", "existing", "stale_epoch", "open_failed", "failed"].map(code => entryRefusal(code).message),
    ...["vscode", "terminal", "other"].map(origin => originMark(origin).label),
    "In use in another Claude process now", "In use in VS Code now", "In use in a terminal now", "Open in VS Code now", "Open in a terminal now",
    "Open in another Claude process now", "Import", "Continue in CodeAlta", "Actions for the Claude Code session {title}", "Opened in VS Code.",
    "Resume command copied. Run it in a terminal opened in the folder of the project.", "The Claude Code session continues in the terminal.",
  ];
  for (const key of keys)
    for (const locale of ["es", "fr", "de", "ja", "zh-CN"] as const) assert.notEqual(translate(locale, key), key, `${key} in ${locale}`);
  assert.equal(translate("fr", "From Claude Code ({count})", { count: 4 }), "Depuis Claude Code (4)");
});

test("a session the filter found says it is held when the rows read of its group say so", () => {
  const found = [{ ...session, held: false, holderStatus: null }, numbered(7)];
  const shown = withHolders(found, [session, { ...numbered(8), held: true, holderStatus: "busy" }]);
  assert.deepEqual(shown.map(value => [value.cliSessionId, value.held, value.holderStatus]), [[cli, true, "busy"], [numbered(7).cliSessionId, false, null]]);
  assert.equal(inUseElsewhere(shown[0]!), true);
});
