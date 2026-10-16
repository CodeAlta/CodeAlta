import type { SessionClaudeCodeConversationResult, SessionClaudeCodeRequest, SessionOpenInVsCodeResult } from "#neoastra";
import type { MessageKey } from "../localization";

/**
 * The calls of the host that open a Claude Code session elsewhere. The page names a session only: the command and the
 * address of VS Code are built by the host from the conversation the session recorded.
 */
export type ClaudeCodeOpenApi = Readonly<{
  conversation: (request: SessionClaudeCodeRequest) => Promise<SessionClaudeCodeConversationResult>;
  release: (request: SessionClaudeCodeRequest) => Promise<SessionClaudeCodeConversationResult>;
  openInVsCode: (request: SessionClaudeCodeRequest) => Promise<SessionOpenInVsCodeResult>;
}>;

/** What a session of Claude Code can be opened in, besides CodeAlta. */
export type OpenElsewhereAction = "open-vs-code" | "copy-resume-command" | "run-in-terminal";

/** Whether an action of the menu of a session is one that opens it elsewhere. */
export function isOpenElsewhereAction(action: string): action is OpenElsewhereAction {
  return action === "open-vs-code" || action === "copy-resume-command" || action === "run-in-terminal";
}

/** The action each command of the search window does for the selected session. */
export const openElsewhereCommandAction = Object.freeze({
  openInVsCode: "open-vs-code",
  copyResumeCommand: "copy-resume-command",
  runInTerminal: "run-in-terminal",
} as const satisfies Record<string, OpenElsewhereAction>);

/** What an action did, said in a message: done, or why not; `detail` says what is left to do after it was done. */
export type OpenElsewhereOutcome = Readonly<{ ok: boolean; message: MessageKey; detail?: MessageKey }>;

/**
 * Whether a session can be opened in VS Code or a terminal: a session of a Claude Code provider whose conversation of
 * the CLI exists (its first turn was answered). The type of the provider is the one of its configuration.
 */
export function opensElsewhere(session: Readonly<{ providerConversation?: boolean | null }>, providerType: string | null | undefined): boolean {
  return providerType === "claude-code" && session.providerConversation === true;
}

/** Why the host did not give the conversation, or did not release it: what the user can do about it. */
export function openElsewhereRefusal(status: string): MessageKey {
  switch (status) {
    case "running": return "The session is working. Wait until it is idle, or stop it, then try again.";
    case "background_tasks": return "Claude Code runs tasks in the background for this session, which end with its process. Stop them first.";
    case "remote_control": return "Remote Control is on for this session. Turn it off first.";
    case "interaction_pending": return "The session waits for an answer. Answer it first.";
    case "busy": return "Another change of the session is in progress. Try again in a moment.";
    case "none": return "This session has no Claude Code conversation yet. Send a prompt first.";
    case "unsupported": return "Only a Claude Code session can be opened in VS Code or a terminal.";
    case "stale_epoch": case "closed": return "The window is out of date. Reload it and try again.";
    case "release_unconfirmed": return "CodeAlta could not confirm that it closed its Claude Code process for this session. Try again.";
    case "open_failed": return "VS Code could not be opened. Check that it is installed.";
    default: return "The session could not be opened outside CodeAlta.";
  }
}

const request = (epoch: string, sessionId: string): SessionClaudeCodeRequest => ({ expectedEpoch: epoch, sessionId });

// A reply about another session, or of another host, is no reply.
const answers = (reply: Readonly<{ epoch: string | null; sessionId: string }>, epoch: string, sessionId: string) =>
  reply.epoch === epoch && reply.sessionId === sessionId;

/**
 * Reads the conversation of a session, for the Session Info window: its identifier and whether another program holds
 * it. Null when there is none to show (another provider, no conversation yet, or a failed read).
 */
export async function readClaudeCodeConversation(api: ClaudeCodeOpenApi, epoch: string | null | undefined, sessionId: string)
  : Promise<SessionClaudeCodeConversationResult | null> {
  if (!epoch) return null;
  try {
    const reply = await api.conversation(request(epoch, sessionId));
    return reply.status === "ok" && answers(reply, epoch, sessionId) && !!reply.cliSessionId ? reply : null;
  } catch { return null; }
}

/** Opens the conversation of a session in VS Code: the host releases it, then opens the address it built. */
export async function openSessionInVsCode(api: ClaudeCodeOpenApi, epoch: string | null | undefined, sessionId: string): Promise<OpenElsewhereOutcome> {
  if (!epoch) return { ok: false, message: openElsewhereRefusal("stale_epoch") };
  try {
    const reply = await api.openInVsCode(request(epoch, sessionId));
    if (reply.status === "opened" && answers(reply, epoch, sessionId))
      return { ok: true, message: "Opened in VS Code. A prompt sent here continues the conversation as VS Code leaves it." };
    return { ok: false, message: openElsewhereRefusal(reply.status) };
  } catch { return { ok: false, message: openElsewhereRefusal("failed") }; }
}

// Why a release was refused while the conversation is there to copy all the same.
const releaseRefusals: readonly string[] = ["running", "background_tasks", "remote_control", "interaction_pending", "busy", "release_unconfirmed"];

/**
 * Copies the command that resumes the conversation of a session in a terminal (`claude --resume <id>`). The host
 * releases the conversation first, as for Run in terminal, so that the command pasted later does not run beside the
 * process CodeAlta keeps for the session. When the release is refused, the command is copied all the same, and the
 * outcome says why CodeAlta still has the conversation open.
 */
export async function copyResumeCommand(api: ClaudeCodeOpenApi, epoch: string | null | undefined, sessionId: string,
  write: ((text: string) => Promise<void>) | undefined): Promise<OpenElsewhereOutcome> {
  if (!epoch) return { ok: false, message: openElsewhereRefusal("stale_epoch") };
  if (!write) return { ok: false, message: "Clipboard unavailable; nothing copied." };
  let reply: SessionClaudeCodeConversationResult;
  let refusal: string | null = null;
  try {
    reply = await api.release(request(epoch, sessionId));
    if (releaseRefusals.includes(reply.status) && answers(reply, epoch, sessionId)) {
      refusal = reply.status;
      reply = await api.conversation(request(epoch, sessionId));
    }
  } catch { return { ok: false, message: openElsewhereRefusal("failed") }; }
  if (reply.status !== "ok" || !answers(reply, epoch, sessionId) || !reply.resumeCommand) return { ok: false, message: openElsewhereRefusal(reply.status) };
  try { await write(reply.resumeCommand); }
  catch { return { ok: false, message: "Could not copy the resume command." }; }
  return refusal === null
    ? { ok: true, message: "Resume command copied. Run it in a terminal opened in the folder of the session." }
    : { ok: true, message: "Resume command copied, but CodeAlta still has the conversation open: both would write it.", detail: openElsewhereRefusal(refusal) };
}

/** The built-in terminal: one is created in the folder of the session (its identity, or null when it could not be), and typed in. */
export type ClaudeCodeTerminal = Readonly<{
  create: (sessionId: string) => Promise<string | null>;
  input: (terminalId: string, data: string) => void;
}>;

/**
 * Resumes the conversation of a session in the built-in terminal: the host releases it, a terminal is created in the
 * folder of the session, and the command the host gave is typed there with Enter.
 */
export async function runSessionInTerminal(api: ClaudeCodeOpenApi, epoch: string | null | undefined, sessionId: string,
  terminal: ClaudeCodeTerminal): Promise<OpenElsewhereOutcome> {
  if (!epoch) return { ok: false, message: openElsewhereRefusal("stale_epoch") };
  let reply: SessionClaudeCodeConversationResult;
  try { reply = await api.release(request(epoch, sessionId)); }
  catch { return { ok: false, message: openElsewhereRefusal("failed") }; }
  if (reply.status !== "ok" || !answers(reply, epoch, sessionId) || !reply.resumeCommand) return { ok: false, message: openElsewhereRefusal(reply.status) };
  let created: string | null;
  try { created = await terminal.create(sessionId); }
  catch { created = null; }
  if (!created) return { ok: false, message: "The terminal could not be started." };
  terminal.input(created, `${reply.resumeCommand}\r`);
  return { ok: true, message: "The conversation continues in the terminal. A prompt sent here continues it as the terminal leaves it." };
}
