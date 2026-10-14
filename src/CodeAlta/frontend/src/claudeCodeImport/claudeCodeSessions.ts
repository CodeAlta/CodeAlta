import type {
  WorkspaceClaudeCodeSession,
  WorkspaceClaudeCodeSessionsRequest,
  WorkspaceClaudeCodeSessionsResponse,
  WorkspaceImportClaudeCodeSessionRequest,
  WorkspaceImportClaudeCodeSessionResponse,
} from "#neoastra";
import type { MessageKey } from "../localization";

/** The two calls of the host this feature makes: the listing and the import. */
export type ClaudeCodeSessionsApi = Readonly<{
  list: (request: WorkspaceClaudeCodeSessionsRequest) => Promise<WorkspaceClaudeCodeSessionsResponse>;
  import: (request: WorkspaceImportClaudeCodeSessionRequest) => Promise<WorkspaceImportClaudeCodeSessionResponse>;
}>;

/** The project whose folder the sessions were started in: its exact identity and folder, as the catalog has them. */
export type ClaudeCodeProject = Readonly<{ id: string; path: string }>;

/** What a listing found: the sessions and how many there are, nothing to list, or why it failed. */
export type ClaudeCodeListing =
  | Readonly<{ kind: "listed"; sessions: readonly WorkspaceClaudeCodeSession[]; total: number }>
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

/** The words of where a session was started. */
export const originLabel = (origin: string): MessageKey =>
  origin === "vscode" ? "VS Code" : origin === "terminal" ? "terminal" : "another program";

const sameProject = (reply: Readonly<{ projectId: string | null; projectPath: string | null }>, project: ClaudeCodeProject) =>
  reply.projectId === project.id && reply.projectPath === project.path;

/**
 * Lists the Claude Code sessions started in VS Code or a terminal in the folder of a project, which no session of
 * CodeAlta continues. A reply about another project, or of another host, is no listing.
 */
export async function listClaudeCodeSessions(api: ClaudeCodeSessionsApi, epoch: string | null | undefined, project: ClaudeCodeProject): Promise<ClaudeCodeListing> {
  if (!epoch) return { kind: "error", code: "stale_epoch" };
  let reply: WorkspaceClaudeCodeSessionsResponse;
  try { reply = await api.list({ expectedHostEpoch: epoch, projectId: project.id, projectPath: project.path }); }
  catch { return { kind: "error", code: "read_failed" }; }
  if (reply.status === "no_provider") return { kind: "none" };
  if (reply.status !== "ok") return { kind: "error", code: reply.status };
  if (reply.hostEpoch !== epoch || !sameProject(reply, project) || !Array.isArray(reply.sessions) || !Number.isInteger(reply.total)
    || reply.total < reply.sessions.length) return { kind: "error", code: "invalid_reply" };
  return { kind: "listed", sessions: reply.sessions, total: reply.total };
}

/**
 * Imports a Claude Code session started elsewhere as a session of the project, or finds the session that continues it
 * already. Only an answer about that very session, of that host, names a session to open.
 */
export async function importClaudeCodeSession(api: ClaudeCodeSessionsApi, epoch: string | null | undefined, project: ClaudeCodeProject,
  cliSessionId: string): Promise<ClaudeCodeImport> {
  if (!epoch) return { kind: "error", code: "stale_epoch" };
  let reply: WorkspaceImportClaudeCodeSessionResponse;
  try { reply = await api.import({ expectedHostEpoch: epoch, projectId: project.id, projectPath: project.path, cliSessionId }); }
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
    case "not_found": case "project_missing": case "no_provider": return "This Claude Code session can no longer be imported: it changed or was removed.";
    case "busy": return "Another change of the sessions is in progress. Try again in a moment.";
    case "stale_epoch": return "The window is out of date. Reload it before importing.";
    case "import_unconfirmed": return "The import could not be confirmed. Look for the session in the list before importing it again.";
    default: return "The Claude Code session could not be imported.";
  }
};
