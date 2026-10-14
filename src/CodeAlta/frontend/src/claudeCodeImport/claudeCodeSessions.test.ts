import assert from "node:assert/strict";
import test from "node:test";
import type { WorkspaceClaudeCodeSessionsResponse, WorkspaceImportClaudeCodeSessionResponse } from "#neoastra";
import { translate } from "../localization";
import { hasClaudeCodeProvider, importClaudeCodeSession, importErrorMessage, listClaudeCodeSessions, originLabel, type ClaudeCodeSessionsApi } from "./claudeCodeSessions";

const epoch = "11111111-1111-4111-8111-111111111111";
const project = { id: "p1", path: "C:\\dev\\p1" };
const cli = "0f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e11";
const session = { cliSessionId: cli, title: "Fix the parser", lastChangedAt: "2026-10-14T10:00:00Z", origin: "vscode", held: true };
const listed = (overrides: Partial<WorkspaceClaudeCodeSessionsResponse> = {}): WorkspaceClaudeCodeSessionsResponse =>
  ({ status: "ok", hostEpoch: epoch, projectId: project.id, projectPath: project.path, providerId: "claude-code", sessions: [session], total: 3, ...overrides });
const imported = (overrides: Partial<WorkspaceImportClaudeCodeSessionResponse> = {}): WorkspaceImportClaudeCodeSessionResponse =>
  ({ status: "ok", hostEpoch: epoch, projectId: project.id, projectPath: project.path, cliSessionId: cli, sessionId: "s-new", ...overrides });
const api = (list: () => Promise<WorkspaceClaudeCodeSessionsResponse>, importing: () => Promise<WorkspaceImportClaudeCodeSessionResponse> = async () => imported()) => {
  const requests: unknown[] = [];
  const value: ClaudeCodeSessionsApi = {
    list: async request => { requests.push(request); return list(); },
    import: async request => { requests.push(request); return importing(); },
  };
  return { value, requests };
};

test("only a Claude Code provider has sessions started elsewhere", () => {
  assert.equal(hasClaudeCodeProvider([{ type: "openai" }, { type: "claude-code" }]), true);
  assert.equal(hasClaudeCodeProvider([{ type: "anthropic" }, { type: null }, {}]), false);
  assert.equal(hasClaudeCodeProvider(new Map([["a", { type: "claude-code" }]]).values()), true);
});

test("a listing asks about the exact project and keeps only a reply about it", async () => {
  const ok = api(async () => listed());
  assert.deepEqual(await listClaudeCodeSessions(ok.value, epoch, project), { kind: "listed", sessions: [session], total: 3 });
  assert.deepEqual(ok.requests, [{ expectedHostEpoch: epoch, projectId: "p1", projectPath: "C:\\dev\\p1" }]);
  assert.deepEqual(await listClaudeCodeSessions(api(async () => listed({ status: "no_provider", sessions: [], total: 0 })).value, epoch, project), { kind: "none" });
  assert.deepEqual(await listClaudeCodeSessions(api(async () => listed({ projectPath: "C:\\dev\\other" })).value, epoch, project), { kind: "error", code: "invalid_reply" });
  assert.deepEqual(await listClaudeCodeSessions(api(async () => listed({ hostEpoch: "other" })).value, epoch, project), { kind: "error", code: "invalid_reply" });
  assert.deepEqual(await listClaudeCodeSessions(api(async () => listed({ total: 0 })).value, epoch, project), { kind: "error", code: "invalid_reply" });
  assert.deepEqual(await listClaudeCodeSessions(api(async () => listed({ status: "stale_epoch" })).value, epoch, project), { kind: "error", code: "stale_epoch" });
  assert.deepEqual(await listClaudeCodeSessions(api(async () => { throw new Error("lost"); }).value, epoch, project), { kind: "error", code: "read_failed" });
  // Without a host, nothing is asked.
  const none = api(async () => listed());
  assert.deepEqual(await listClaudeCodeSessions(none.value, null, project), { kind: "error", code: "stale_epoch" });
  assert.equal(none.requests.length, 0);
});

test("an import opens the session that continues the conversation, new or already there", async () => {
  const ok = api(async () => listed());
  assert.deepEqual(await importClaudeCodeSession(ok.value, epoch, project, cli), { kind: "opened", sessionId: "s-new", existing: false });
  assert.deepEqual(ok.requests, [{ expectedHostEpoch: epoch, projectId: "p1", projectPath: "C:\\dev\\p1", cliSessionId: cli }]);
  assert.deepEqual(await importClaudeCodeSession(api(async () => listed(), async () => imported({ status: "existing", sessionId: "s-old" })).value, epoch, project, cli),
    { kind: "opened", sessionId: "s-old", existing: true });
  assert.deepEqual(await importClaudeCodeSession(api(async () => listed(), async () => imported({ status: "not_found", sessionId: null })).value, epoch, project, cli),
    { kind: "error", code: "not_found" });
  // A reply about another conversation, or without a session, or lost, says nothing sure: the session may exist.
  for (const reply of [async () => imported({ cliSessionId: "another" }), async () => imported({ sessionId: null }), async () => { throw new Error("lost"); }])
    assert.deepEqual(await importClaudeCodeSession(api(async () => listed(), reply).value, epoch, project, cli), { kind: "error", code: "import_unconfirmed" });
});

test("where a session was started and why an import failed are said in every language", () => {
  assert.deepEqual(["vscode", "terminal", "other", "unknown"].map(originLabel), ["VS Code", "terminal", "another program", "another program"]);
  for (const code of ["not_found", "busy", "stale_epoch", "import_unconfirmed", "closed"]) {
    const key = importErrorMessage(code);
    for (const locale of ["es", "fr", "de", "ja", "zh-CN"] as const) assert.notEqual(translate(locale, key), key, `${code} in ${locale}`);
  }
  assert.equal(translate("fr", "From Claude Code ({count})", { count: 4 }), "Depuis Claude Code (4)");
});
