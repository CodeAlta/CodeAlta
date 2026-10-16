import type { HistoryRequest, WorkspaceClaudeCodeSessionPreviewRequest, WorkspaceClaudeCodeSessionPreviewResponse } from "#neoastra";
import type { TimelinePage } from "../history";
import type { MessageKey } from "../localization";
import type { ClaudeCodeProject } from "./claudeCodeSessions";

/** The call of the host that reads a page of a conversation to show it: nothing is written. */
export type ClaudeCodePreviewApi = (request: WorkspaceClaudeCodeSessionPreviewRequest,
  options?: Readonly<{ signal?: AbortSignal; timeoutMilliseconds?: number }>) => Promise<WorkspaceClaudeCodeSessionPreviewResponse>;

/** The identity the timeline of a preview reads its pages under, as the host names its records: they belong to no session. */
export const previewSessionId = (cliSessionId: string) => `preview:${cliSessionId}`;

/** What a page of the preview said about the conversation: shown, continued by a session already, or why it is not shown. */
export type ClaudeCodePreviewStatus =
  | Readonly<{ kind: "shown"; title: string | null }>
  | Readonly<{ kind: "existing"; sessionId: string }>
  | Readonly<{ kind: "unavailable"; code: string }>;

/**
 * Reads the pages of the preview of a conversation as the timeline of a session reads those of its journal: the newest
 * first, then the older ones from the cursor of the page after them. Each reply is reported with `report`: a conversation
 * a session continues already is not shown, that session is. A reply about another conversation, another project or of
 * another host is no page.
 */
export function previewReader(api: ClaudeCodePreviewApi, epoch: string, project: ClaudeCodeProject, cliSessionId: string,
  report: (status: ClaudeCodePreviewStatus) => void) {
  return async (request: HistoryRequest, options?: Readonly<{ signal?: AbortSignal; timeoutMilliseconds?: number }>): Promise<TimelinePage> => {
    const failed = (code: string): TimelinePage => ({ status: code, entries: [], next: null, tailOmitted: false, revision: null, sources: [] });
    if (request.sessionId !== previewSessionId(cliSessionId)) return failed("invalid_cursor");
    let reply: WorkspaceClaudeCodeSessionPreviewResponse;
    try { reply = await api({ expectedHostEpoch: epoch, projectId: project.id, projectPath: project.path, cliSessionId, cursor: request.cursor, spaceId: project.spaceId ?? null }, options); }
    catch (error) { if (options?.signal?.aborted) throw error; return failed("read_failed"); }
    if (reply.hostEpoch !== epoch || reply.projectId !== project.id || reply.projectPath !== project.path || reply.cliSessionId !== cliSessionId)
      return failed("read_failed");
    if (reply.status === "existing" && reply.sessionId) { report({ kind: "existing", sessionId: reply.sessionId }); return failed("existing"); }
    if (reply.status !== "ok") {
      // A page that could be read again (the transcript grew meanwhile) is the timeline's to retry: the preview stays.
      if (reply.status !== "history_changed" && reply.status !== "read_failed") report({ kind: "unavailable", code: reply.status });
      return failed(reply.status);
    }
    report({ kind: "shown", title: reply.title });
    return { ...reply.page, revision: null, sources: [] };
  };
}

/** What the preview says of a conversation that cannot be shown. */
export const previewUnavailableMessage = (code: string): MessageKey => {
  switch (code) {
    case "too_large": return "This conversation is too large to preview. Continue it in CodeAlta or open it in VS Code to read it.";
    case "not_found": case "project_missing": case "no_provider": return "This Claude Code session can no longer be shown: it changed or was removed.";
    case "stale_epoch": case "closed": return "The window is out of date. Reload it and try again.";
    default: return "The Claude Code session could not be shown.";
  }
};

/** Says where a conversation was started, above its preview. */
export const previewBannerText = (origin: string): MessageKey =>
  origin === "vscode" ? "Claude Code conversation started in VS Code, not in CodeAlta yet. Your first message continues it here."
    : origin === "terminal" ? "Claude Code conversation started in a terminal, not in CodeAlta yet. Your first message continues it here."
    : "Claude Code conversation started in another program, not in CodeAlta yet. Your first message continues it here.";

/** What the prompt under the preview says before anything is typed. */
export const previewPlaceholder = (origin: string): MessageKey =>
  origin === "vscode" ? "Continue this VS Code conversation in CodeAlta…"
    : origin === "terminal" ? "Continue this terminal conversation in CodeAlta…"
    : "Continue this Claude Code conversation in CodeAlta…";

/**
 * What the banner says of another Claude process that works in the conversation now (one merely open in a panel of VS
 * Code is idle, and says nothing): the first message will ask whether to wait for it. Null when nothing works in it.
 */
export function previewHeldText(held: Readonly<{ origin: string; status: string | null }> | null): MessageKey | null {
  if (!held || held.status === "idle") return null;
  return held.origin === "vsCode" ? "In use in VS Code now: your first message will ask whether to wait until it is free."
    : held.origin === "terminal" ? "In use in a terminal now: your first message will ask whether to wait until it is free."
    : "In use in another Claude process now: your first message will ask whether to wait until it is free.";
}
