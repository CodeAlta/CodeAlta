import type {
  WorkspaceClaudeCodeSession,
  WorkspaceClaudeCodeSessionConversationResponse,
  WorkspaceClaudeCodeSessionRequest,
  WorkspaceClaudeCodeSessionsRequest,
  WorkspaceClaudeCodeSessionsResponse,
  WorkspaceImportClaudeCodeSessionRequest,
  WorkspaceImportClaudeCodeSessionResponse,
  WorkspaceOpenClaudeCodeSessionResponse,
} from "#neoastra";
import type { IconName } from "../AppIcon";
import type { MessageKey } from "../localization";
import type { SessionMenuEntry } from "../SessionTabMenu";

/**
 * The calls of the host this feature makes: the listing, the import, and, for a session that is not imported, the
 * description of its conversation (its resume command) and its opening in VS Code. The host builds the command and the
 * address of VS Code from the transcript it finds in the folder of the project.
 */
export type ClaudeCodeSessionsApi = Readonly<{
  list: (request: WorkspaceClaudeCodeSessionsRequest) => Promise<WorkspaceClaudeCodeSessionsResponse>;
  import: (request: WorkspaceImportClaudeCodeSessionRequest) => Promise<WorkspaceImportClaudeCodeSessionResponse>;
  conversation: (request: WorkspaceClaudeCodeSessionRequest) => Promise<WorkspaceClaudeCodeSessionConversationResponse>;
  openInVsCode: (request: WorkspaceClaudeCodeSessionRequest) => Promise<WorkspaceOpenClaudeCodeSessionResponse>;
}>;

/**
 * The project whose folder the sessions were started in: its exact identity and folder, as the catalog has them, and the
 * space the window shows (null without spaces). The host takes the provider of that space, and an imported session
 * belongs to it.
 */
export type ClaudeCodeProject = Readonly<{ id: string; path: string; spaceId?: string | null }>;

/** How many sessions the group lists at first, and how many more each "Show more" adds. */
export const claudeCodeFirstPage = 20;
export const claudeCodeMorePage = 50;
// The most sessions one call of the host returns.
const hostPage = 50;

/** What a listing found: the sessions read so far and how many there are, nothing to list, or why it failed. */
export type ClaudeCodeListing =
  | Readonly<{ kind: "listed"; sessions: readonly WorkspaceClaudeCodeSession[]; total: number; providerId: string | null }>
  | Readonly<{ kind: "none" }>
  | Readonly<{ kind: "error"; code: string }>;

/** What an import did: the session that continues the conversation, new or already there, or why there is none. */
export type ClaudeCodeImport =
  | Readonly<{ kind: "opened"; sessionId: string; existing: boolean }>
  | Readonly<{ kind: "error"; code: string }>;

/** Whether the configuration has a Claude Code provider: the only one whose sessions VS Code and a terminal start too. */
export const hasClaudeCodeProvider = (providers: Iterable<Readonly<{ type?: string | null }>>): boolean => {
  for (const provider of providers) if (provider.type === "claude-code") return true;
  return false;
};

/** The small icon of where a session was started, before its time, and what it says. */
export const originMark = (origin: string): Readonly<{ icon: IconName; label: MessageKey }> =>
  origin === "vscode" ? { icon: "code", label: "Started in VS Code" }
    : origin === "terminal" ? { icon: "terminal", label: "Started in a terminal" }
    : { icon: "server", label: "Started in another program" };

/**
 * Whether a session is in use in another Claude process now: held by one that says it does something. A session merely
 * open in a panel of VS Code is held by an idle process: it is not marked, or nearly every session would be.
 */
export const inUseElsewhere = (session: Readonly<Pick<WorkspaceClaudeCodeSession, "held" | "holderStatus">>): boolean =>
  session.held && session.holderStatus !== "idle";

const sameProject = (reply: Readonly<{ projectId: string | null; projectPath: string | null }>, project: ClaudeCodeProject) =>
  reply.projectId === project.id && reply.projectPath === project.path;

/**
 * Lists a page of the Claude Code sessions started in VS Code or a terminal in the folder of a project, which no session
 * of CodeAlta continues. A reply about another project, another page, or of another host, is no listing.
 */
export async function listClaudeCodeSessions(api: ClaudeCodeSessionsApi, epoch: string | null | undefined, project: ClaudeCodeProject,
  offset = 0, count = claudeCodeFirstPage): Promise<ClaudeCodeListing> {
  if (!epoch) return { kind: "error", code: "stale_epoch" };
  let reply: WorkspaceClaudeCodeSessionsResponse;
  try { reply = await api.list({ expectedHostEpoch: epoch, projectId: project.id, projectPath: project.path, offset, count, spaceId: project.spaceId ?? null }); }
  catch { return { kind: "error", code: "read_failed" }; }
  if (reply.status === "no_provider") return { kind: "none" };
  if (reply.status !== "ok") return { kind: "error", code: reply.status };
  if (reply.hostEpoch !== epoch || !sameProject(reply, project) || reply.offset !== offset || !Array.isArray(reply.sessions) || reply.sessions.length > count
    || !Number.isInteger(reply.total) || reply.total < offset + reply.sessions.length) return { kind: "error", code: "invalid_reply" };
  return { kind: "listed", sessions: reply.sessions, total: reply.total, providerId: reply.providerId };
}

/**
 * Reads the first sessions of the listing, as many as `count`, a page of the host after the other: what the group shows
 * when it is read again (it keeps what "Show more" added). Stops at the end of the list.
 */
export async function readClaudeCodeSessions(api: ClaudeCodeSessionsApi, epoch: string | null | undefined, project: ClaudeCodeProject,
  count: number): Promise<ClaudeCodeListing> {
  const wanted = Math.max(1, count);
  const first = await listClaudeCodeSessions(api, epoch, project, 0, Math.min(hostPage, wanted));
  if (first.kind !== "listed") return first;
  let read = first;
  while (read.sessions.length < Math.min(wanted, read.total)) {
    const offset = read.sessions.length;
    const page = await listClaudeCodeSessions(api, epoch, project, offset, Math.min(hostPage, wanted - offset));
    if (page.kind !== "listed") return page;
    const next = withPage(read, page);
    // A page that brings nothing new ends the read: the list changed meanwhile.
    if (next.sessions.length === offset) return next;
    read = next;
  }
  return read;
}

/**
 * The sessions read so far, followed by the next page "Show more" read. A session that moved up meanwhile (written
 * again) is not listed twice; the total is the one of the newest reply.
 */
export function withPage(shown: Extract<ClaudeCodeListing, { kind: "listed" }>, page: Extract<ClaudeCodeListing, { kind: "listed" }>)
  : Extract<ClaudeCodeListing, { kind: "listed" }> {
  const seen = new Set(shown.sessions.map(session => session.cliSessionId));
  return { kind: "listed", sessions: [...shown.sessions, ...page.sessions.filter(session => !seen.has(session.cliSessionId))], total: page.total,
    providerId: page.providerId ?? shown.providerId };
}

/** How many sessions "Show more" says are not listed yet; zero when the list is whole. */
export const notShown = (listing: Readonly<{ sessions: readonly unknown[]; total: number }>): number => Math.max(0, listing.total - listing.sessions.length);

/**
 * Imports a Claude Code session started elsewhere as a session of the project, or finds the session that continues it
 * already. Only an answer about that very session, of that host, names a session to open.
 */
export async function importClaudeCodeSession(api: ClaudeCodeSessionsApi, epoch: string | null | undefined, project: ClaudeCodeProject,
  cliSessionId: string): Promise<ClaudeCodeImport> {
  if (!epoch) return { kind: "error", code: "stale_epoch" };
  let reply: WorkspaceImportClaudeCodeSessionResponse;
  try { reply = await api.import({ expectedHostEpoch: epoch, projectId: project.id, projectPath: project.path, cliSessionId, spaceId: project.spaceId ?? null }); }
  // The host may have written the session before the reply was lost.
  catch { return { kind: "error", code: "import_unconfirmed" }; }
  if (reply.status !== "ok" && reply.status !== "existing") return { kind: "error", code: reply.status };
  if (reply.hostEpoch !== epoch || !sameProject(reply, project) || reply.cliSessionId !== cliSessionId || !reply.sessionId)
    return { kind: "error", code: "import_unconfirmed" };
  return { kind: "opened", sessionId: reply.sessionId, existing: reply.status === "existing" };
}

/** What the window says of an import that failed. */
export const importErrorMessage = (code: string): MessageKey => {
  switch (code) {
    case "not_found": case "project_missing": return "This Claude Code session can no longer be imported: it changed or was removed.";
    case "no_provider": case "provider_outside_space": return "No Claude Code provider is available in the space this window shows: the session cannot be imported here.";
    case "space_missing": return "The space this window shows no longer exists. Show another space to import this Claude Code session.";
    case "busy": return "Another change of the sessions is in progress. Try again in a moment.";
    case "stale_epoch": return "The window is out of date. Reload it before importing.";
    case "import_unconfirmed": return "The import could not be confirmed. Look for the session in the list before importing it again.";
    default: return "The Claude Code session could not be imported.";
  }
};

/** What the menu of a session of the group does with it. Never a deletion: the transcript is not CodeAlta's. */
export type ClaudeCodeEntryAction = "preview" | "continue" | "open-vs-code" | "copy-resume-command" | "run-in-terminal";

/**
 * The lines of the menu of a session of the group: look at it (the preview, which writes nothing), continue it in
 * CodeAlta (the import), or open it where it was started. They are disabled while an import runs, and Run in terminal
 * without a terminal to run it in.
 */
export function entryMenuItems(t: (key: MessageKey) => string, gates: Readonly<{ busy: boolean; terminal: boolean }>,
  onAction: (action: ClaudeCodeEntryAction) => void): SessionMenuEntry[] {
  return [
    { key: "preview", label: t("Open preview"), icon: "eye", disabled: gates.busy, onSelect: () => onAction("preview") },
    { key: "continue", label: t("Continue in CodeAlta"), icon: "open", disabled: gates.busy, onSelect: () => onAction("continue") },
    { key: "open-vs-code", label: t("Open in VS Code"), icon: "openExternal", disabled: gates.busy, onSelect: () => onAction("open-vs-code") },
    { key: "copy-resume-command", label: t("Copy resume command"), icon: "copy", disabled: gates.busy, onSelect: () => onAction("copy-resume-command") },
    { key: "run-in-terminal", label: t("Run in terminal"), icon: "terminal", disabled: gates.busy || !gates.terminal, onSelect: () => onAction("run-in-terminal") },
  ];
}

/** What an action on a session of the group did, said in a message: done, or why not. */
export type ClaudeCodeEntryOutcome = Readonly<{ ok: boolean; message: MessageKey; reread?: boolean }>;

/** Why the host did not describe or open a session of the group: what the user can do about it. */
export function entryRefusal(status: string): ClaudeCodeEntryOutcome {
  switch (status) {
    case "not_found": case "project_missing": case "no_provider":
      return { ok: false, message: "This Claude Code session changed or was removed. The list was read again.", reread: true };
    case "existing":
      return { ok: false, message: "A session of CodeAlta continues this conversation now: open it from the list of sessions.", reread: true };
    case "stale_epoch": case "closed": return { ok: false, message: "The window is out of date. Reload it and try again." };
    case "open_failed": return { ok: false, message: "VS Code could not be opened. Check that it is installed." };
    default: return { ok: false, message: "The Claude Code session could not be opened outside CodeAlta." };
  }
}

const target = (epoch: string, project: ClaudeCodeProject, cliSessionId: string): WorkspaceClaudeCodeSessionRequest =>
  ({ expectedHostEpoch: epoch, projectId: project.id, projectPath: project.path, cliSessionId, spaceId: project.spaceId ?? null });

// A reply about another session, another project or of another host is no reply.
const answers = (reply: Readonly<{ hostEpoch: string | null; projectId: string | null; projectPath: string | null; cliSessionId: string | null }>,
  epoch: string, project: ClaudeCodeProject, cliSessionId: string) =>
  reply.hostEpoch === epoch && sameProject(reply, project) && reply.cliSessionId === cliSessionId;

/** Opens a session of the group in VS Code: the host opens the address it built for the transcript of the project. */
export async function openEntryInVsCode(api: ClaudeCodeSessionsApi, epoch: string | null | undefined, project: ClaudeCodeProject,
  cliSessionId: string): Promise<ClaudeCodeEntryOutcome> {
  if (!epoch) return entryRefusal("stale_epoch");
  try {
    const reply = await api.openInVsCode(target(epoch, project, cliSessionId));
    if (reply.status === "opened" && answers(reply, epoch, project, cliSessionId)) return { ok: true, message: "Opened in VS Code." };
    return entryRefusal(reply.status === "opened" ? "failed" : reply.status);
  } catch { return entryRefusal("failed"); }
}

// The command that resumes a session of the group, as the host gave it; or why there is none.
async function resumeCommand(api: ClaudeCodeSessionsApi, epoch: string, project: ClaudeCodeProject, cliSessionId: string)
  : Promise<string | ClaudeCodeEntryOutcome> {
  let reply: WorkspaceClaudeCodeSessionConversationResponse;
  try { reply = await api.conversation(target(epoch, project, cliSessionId)); }
  catch { return entryRefusal("failed"); }
  if (reply.status !== "ok") return entryRefusal(reply.status);
  return answers(reply, epoch, project, cliSessionId) && reply.resumeCommand === `claude --resume ${cliSessionId}` ? reply.resumeCommand : entryRefusal("failed");
}

/** Copies the command that resumes a session of the group in a terminal (`claude --resume <id>`). */
export async function copyEntryResumeCommand(api: ClaudeCodeSessionsApi, epoch: string | null | undefined, project: ClaudeCodeProject,
  cliSessionId: string, write: ((text: string) => Promise<void>) | undefined): Promise<ClaudeCodeEntryOutcome> {
  if (!epoch) return entryRefusal("stale_epoch");
  const command = await resumeCommand(api, epoch, project, cliSessionId);
  if (typeof command !== "string") return command;
  if (!write) return { ok: false, message: "Clipboard unavailable; nothing copied." };
  try { await write(command); }
  catch { return { ok: false, message: "Could not copy the resume command." }; }
  return { ok: true, message: "Resume command copied. Run it in a terminal opened in the folder of the project." };
}

/** The built-in terminal: one is created in the folder of the project (its identity, or null when it could not be), and typed in. */
export type ClaudeCodeEntryTerminal = Readonly<{
  create: () => Promise<string | null>;
  input: (terminalId: string, data: string) => void;
}>;

/**
 * Resumes a session of the group in the built-in terminal: the host gives the command for the transcript of the project
 * (nothing to release: CodeAlta does not hold it), a terminal is created in the folder of the project, and the command is
 * typed there with Enter.
 */
export async function runEntryInTerminal(api: ClaudeCodeSessionsApi, epoch: string | null | undefined, project: ClaudeCodeProject,
  cliSessionId: string, terminal: ClaudeCodeEntryTerminal): Promise<ClaudeCodeEntryOutcome> {
  if (!epoch) return entryRefusal("stale_epoch");
  const command = await resumeCommand(api, epoch, project, cliSessionId);
  if (typeof command !== "string") return command;
  let created: string | null;
  try { created = await terminal.create(); }
  catch { created = null; }
  if (!created) return { ok: false, message: "The terminal could not be started." };
  terminal.input(created, `${command}\r`);
  return { ok: true, message: "The Claude Code session continues in the terminal." };
}
