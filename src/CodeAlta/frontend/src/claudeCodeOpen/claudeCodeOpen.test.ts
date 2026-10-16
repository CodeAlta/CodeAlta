import assert from "node:assert/strict";
import test from "node:test";
import type { SessionClaudeCodeConversationResult, SessionClaudeCodeRequest, SessionOpenInVsCodeResult } from "#neoastra";
import { commandDefinitions, searchCommands } from "../commandRegistry";
import { messages } from "../localization";
import {
  copyResumeCommand, isOpenElsewhereAction, openElsewhereCommandAction, openElsewhereRefusal, openSessionInVsCode, opensElsewhere,
  readClaudeCodeConversation, runSessionInTerminal, type ClaudeCodeOpenApi,
} from "./claudeCodeOpen";
import { openElsewhereEntries } from "./openElsewhereEntries";

const catalog: Readonly<Record<string, readonly string[] | undefined>> = messages;
const conversation = "5b0c8b0e-3f7c-4c1e-9a55-6d1f0b2c3e01";
const ok = (sessionId = "s", epoch = "epoch"): SessionClaudeCodeConversationResult => ({ status: "ok", epoch, sessionId, cliSessionId: conversation,
  resumeCommand: `claude --resume ${conversation}`, workingDirectory: "C:/work", held: null, configDir: null, windows: true, home: "C:\\Users\\me" });
const refused = (status: string): SessionClaudeCodeConversationResult => ({ status, epoch: "epoch", sessionId: "s", cliSessionId: null,
  resumeCommand: null, workingDirectory: null, held: null, configDir: null, windows: true, home: null });

/** A host that answers as told, and records what it was asked. */
function host(answers: { conversation?: SessionClaudeCodeConversationResult; release?: SessionClaudeCodeConversationResult; open?: SessionOpenInVsCodeResult } = {}) {
  const asked: string[] = [];
  const requests: SessionClaudeCodeRequest[] = [];
  const api: ClaudeCodeOpenApi = {
    conversation: async request => { asked.push("conversation"); requests.push(request); return answers.conversation ?? ok(); },
    release: async request => { asked.push("release"); requests.push(request); return answers.release ?? ok(); },
    openInVsCode: async request => { asked.push("open"); requests.push(request); return answers.open ?? { status: "opened", epoch: "epoch", sessionId: "s" }; },
  };
  return { api, asked, requests };
}

test("only a session of a Claude Code provider whose conversation of the CLI exists opens elsewhere", () => {
  assert.equal(opensElsewhere({ providerConversation: true }, "claude-code"), true);
  assert.equal(opensElsewhere({ providerConversation: true }, "openai-responses"), false, "another provider keeps no conversation VS Code opens");
  assert.equal(opensElsewhere({ providerConversation: true }, undefined), false, "a provider the configuration does not know");
  assert.equal(opensElsewhere({}, "claude-code"), false, "before its first turn");
  assert.equal(opensElsewhere({ providerConversation: null }, "claude-code"), false);
  for (const action of ["open-vs-code", "copy-resume-command", "run-in-terminal"]) assert.equal(isOpenElsewhereAction(action), true);
  for (const action of ["open", "remote-control", "move"]) assert.equal(isOpenElsewhereAction(action), false);
});

test("the menu lines are those of a Claude Code session with a conversation, disabled when the window does not allow them", () => {
  const chosen: string[] = [];
  const t = (key: string) => `[${key}]`;
  const lines = openElsewhereEntries(t, { providerConversation: true }, "claude-code", { "open-vs-code": true, "copy-resume-command": true }, action => chosen.push(action));
  assert.deepEqual(lines.map(line => "divider" in line ? null : [line.key, line.label, line.icon, !!line.disabled]), [
    ["open-vs-code", "[Open in VS Code]", "openExternal", false],
    ["run-in-terminal", "[Run in terminal]", "terminal", true],
    ["copy-resume-command", "[Copy resume command]", "copy", false],
  ]);
  for (const line of lines) if (!("divider" in line)) line.onSelect();
  assert.deepEqual(chosen, ["open-vs-code", "run-in-terminal", "copy-resume-command"]);
  assert.deepEqual(openElsewhereEntries(t, { providerConversation: true }, "codex", { "open-vs-code": true }, () => { }), []);
  assert.deepEqual(openElsewhereEntries(t, { providerConversation: false }, "claude-code", { "open-vs-code": true }, () => { }), []);
});

test("the search window has the three commands for the selected session, translated in every language", () => {
  const commands = commandDefinitions.filter(command => command.id in openElsewhereCommandAction);
  assert.deepEqual(commands.map(command => [command.id, command.category, openElsewhereCommandAction[command.id as keyof typeof openElsewhereCommandAction]]), [
    ["openInVsCode", "Session", "open-vs-code"],
    ["runInTerminal", "Session", "run-in-terminal"],
    ["copyResumeCommand", "Session", "copy-resume-command"],
  ]);
  const label = (command: (typeof commandDefinitions)[number]) => command.label;
  const description = (command: (typeof commandDefinitions)[number]) => command.description;
  // Another command (an external editor) may match "vscode" first: this one is among them.
  assert.ok(searchCommands("vscode", label, description).some(command => command.id === "openInVsCode"));
  assert.ok(searchCommands("resume", label, description).some(command => command.id === "copyResumeCommand"));
  for (const command of commands) for (const key of [command.label, command.description]) {
    const translations = catalog[key];
    assert.equal(translations?.length, 5, key);
    assert.ok(translations.every(value => value.trim().length > 0), key);
  }
});

test("Open in VS Code names the session only: the host releases the conversation and opens the address it built", async () => {
  const { api, asked, requests } = host();
  assert.deepEqual(await openSessionInVsCode(api, "epoch", "s"), { ok: true, message: "Opened in VS Code. A prompt sent here continues the conversation as VS Code leaves it." });
  assert.deepEqual(asked, ["open"]);
  assert.deepEqual(requests, [{ expectedEpoch: "epoch", sessionId: "s" }], "no address, no command: only the session");

  for (const status of ["running", "background_tasks", "remote_control", "interaction_pending", "none", "unsupported", "open_failed", "stale_epoch"]) {
    const outcome = await openSessionInVsCode(host({ open: { status, epoch: "epoch", sessionId: "s" } }).api, "epoch", "s");
    assert.deepEqual(outcome, { ok: false, message: openElsewhereRefusal(status) }, status);
  }
  // A reply about another session, or of another host, did not open this one.
  assert.equal((await openSessionInVsCode(host({ open: { status: "opened", epoch: "other", sessionId: "s" } }).api, "epoch", "s")).ok, false);
  assert.equal((await openSessionInVsCode(host({ open: { status: "opened", epoch: "epoch", sessionId: "t" } }).api, "epoch", "s")).ok, false);
  const none = host();
  assert.equal((await openSessionInVsCode(none.api, null, "s")).ok, false);
  assert.deepEqual(none.asked, [], "a window without a host asks nothing");
});

test("every refusal says what the user can do", () => {
  const told = ["running", "background_tasks", "remote_control", "interaction_pending", "busy", "none", "unsupported", "stale_epoch", "release_unconfirmed", "open_failed", "failed"]
    .map(openElsewhereRefusal);
  assert.equal(new Set(told.slice(0, 8)).size, 8, "the reasons are told apart");
  for (const message of told) assert.equal(catalog[message]?.length, 5, message);
});

test("Copy resume command releases the conversation first, as Run in terminal does, then copies the command of the host", async () => {
  const { api, asked } = host();
  const copied: string[] = [];
  const outcome = await copyResumeCommand(api, "epoch", "s", async text => { copied.push(text); });
  assert.deepEqual(outcome, { ok: true, message: "Resume command copied. Run it in a terminal opened in the folder of the session." });
  assert.deepEqual(copied, [`claude --resume ${conversation}`]);
  assert.deepEqual(asked, ["release"], "CodeAlta closes its idle Claude Code process: the command pasted later finds the conversation free");

  // A release refused: the command is copied all the same, and the message says why CodeAlta still has it open.
  for (const status of ["running", "background_tasks", "remote_control", "interaction_pending", "busy", "release_unconfirmed"]) {
    const refusal = host({ release: refused(status) });
    const pasted: string[] = [];
    assert.deepEqual(await copyResumeCommand(refusal.api, "epoch", "s", async text => { pasted.push(text); }),
      { ok: true, message: "Resume command copied, but CodeAlta still has the conversation open: both would write it.", detail: openElsewhereRefusal(status) }, status);
    assert.deepEqual(pasted, [`claude --resume ${conversation}`], status);
    assert.deepEqual(refusal.asked, ["release", "conversation"], status);
  }

  // No conversation: nothing to copy, nothing asked after the release.
  for (const status of ["none", "unsupported", "stale_epoch"]) {
    const nothing = host({ release: refused(status) });
    assert.deepEqual(await copyResumeCommand(nothing.api, "epoch", "s", async () => assert.fail("nothing to copy")), { ok: false, message: openElsewhereRefusal(status) }, status);
    assert.deepEqual(nothing.asked, ["release"], status);
  }
  // A reply about another session copies nothing.
  assert.equal((await copyResumeCommand(host({ release: ok("t") }).api, "epoch", "s", async () => assert.fail("not this session"))).ok, false);
  // Without a clipboard nothing is released either.
  const noClipboard = host();
  assert.deepEqual(await copyResumeCommand(noClipboard.api, "epoch", "s", undefined), { ok: false, message: "Clipboard unavailable; nothing copied." });
  assert.deepEqual(noClipboard.asked, []);
  assert.deepEqual(await copyResumeCommand(api, "epoch", "s", async () => { throw new Error("denied"); }), { ok: false, message: "Could not copy the resume command." });
  assert.equal(catalog["Resume command copied, but CodeAlta still has the conversation open: both would write it."]?.length, 5);
});

test("Run in terminal releases the conversation first, then types the command of the host in a terminal of the session", async () => {
  const { api, asked } = host();
  const steps: string[] = [];
  const terminal = {
    create: async (sessionId: string) => { steps.push(`create:${sessionId}`); return { id: "t1", profile: "pwsh" }; },
    input: (id: string, data: string) => { steps.push(`input:${id}:${JSON.stringify(data)}`); },
  };
  assert.equal((await runSessionInTerminal(api, "epoch", "s", terminal)).ok, true);
  assert.deepEqual(asked, ["release"]);
  assert.deepEqual(steps, ["create:s", `input:t1:${JSON.stringify(`claude --resume ${conversation}\r`)}`], "the command, then Enter");

  // Refused by the host: no terminal is opened.
  steps.length = 0;
  for (const status of ["running", "background_tasks", "remote_control"]) {
    assert.deepEqual(await runSessionInTerminal(host({ release: refused(status) }).api, "epoch", "s", terminal), { ok: false, message: openElsewhereRefusal(status) });
  }
  assert.deepEqual(steps, []);
  // A terminal that cannot be started.
  assert.deepEqual(await runSessionInTerminal(api, "epoch", "s", { create: async () => null, input: () => assert.fail("no terminal") }),
    { ok: false, message: "The terminal could not be started." });
  // A reply about another session releases nothing here.
  assert.equal((await runSessionInTerminal(host({ release: ok("t") }).api, "epoch", "s", terminal)).ok, false);
  assert.deepEqual(steps, []);
});

test("a provider with an account folder: the commands set CLAUDE_CONFIG_DIR to it, and VS Code is refused with what to do", async () => {
  const account = { ...ok(), configDir: "C:\\Users\\me\\.claude-work" };
  const prefixed = `$env:CLAUDE_CONFIG_DIR="$HOME\\.claude-work"; claude --resume ${conversation}`;
  const copied: string[] = [];
  assert.equal((await copyResumeCommand(host({ release: account }).api, "epoch", "s", async text => { copied.push(text); })).ok, true);
  assert.equal((await copyResumeCommand(host({ release: refused("running"), conversation: account }).api, "epoch", "s", async text => { copied.push(text); })).ok, true);
  assert.deepEqual(copied, [prefixed, prefixed], "the conversation is in the account folder of the provider, not in ~/.claude");

  const steps: string[] = [];
  const terminal = (profile: string) => ({
    create: async () => { steps.push(`create:${profile}`); return { id: "t1", profile }; },
    input: (id: string, data: string) => { steps.push(`input:${data}`); },
  });
  assert.equal((await runSessionInTerminal(host({ release: account }).api, "epoch", "s", terminal("pwsh"))).ok, true);
  assert.deepEqual(steps, ["create:pwsh", `input:${prefixed}\r`]);
  // A shell that does not take the form of the system is not typed in: the command would fail there, or resume nothing.
  steps.length = 0;
  assert.deepEqual(await runSessionInTerminal(host({ release: account }).api, "epoch", "s", terminal("cmd")),
    { ok: false, message: "The shell of this terminal cannot be given the account folder of the provider. Choose PowerShell or a POSIX shell for terminals in Settings, or use Copy resume command." });
  assert.deepEqual(steps, ["create:cmd"]);
  // Without an account folder, any shell runs the command.
  steps.length = 0;
  assert.equal((await runSessionInTerminal(host().api, "epoch", "s", terminal("cmd"))).ok, true);
  assert.deepEqual(steps, ["create:cmd", `input:claude --resume ${conversation}\r`]);

  assert.deepEqual(await openSessionInVsCode(host({ open: { status: "account_folder", epoch: "epoch", sessionId: "s" } }).api, "epoch", "s"),
    { ok: false, message: "VS Code opens Claude Code conversations from its own account folder, not from the account folder of this provider. Use Run in terminal or Copy resume command." });
  for (const message of [openElsewhereRefusal("account_folder"), "The shell of this terminal cannot be given the account folder of the provider. Choose PowerShell or a POSIX shell for terminals in Settings, or use Copy resume command."])
    assert.equal(catalog[message]?.length, 5, message);
});

test("Session Info reads the conversation of a Claude Code session, and nothing for one that has none", async () => {
  assert.deepEqual(await readClaudeCodeConversation(host().api, "epoch", "s"), ok());
  for (const status of ["unsupported", "none", "unavailable"])
    assert.equal(await readClaudeCodeConversation(host({ conversation: refused(status) }).api, "epoch", "s"), null, status);
  assert.equal(await readClaudeCodeConversation(host({ conversation: ok("other") }).api, "epoch", "s"), null);
  assert.equal(await readClaudeCodeConversation({ ...host().api, conversation: async () => { throw new Error("gone"); } }, "epoch", "s"), null);
  assert.equal(await readClaudeCodeConversation(host().api, null, "s"), null);
});
