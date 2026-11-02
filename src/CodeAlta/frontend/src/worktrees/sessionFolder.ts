import type { WorkspaceSwitchSessionFolderRequest, WorkspaceSwitchSessionFolderResponse } from "#neoastra";
import type { MessageKey } from "../localization";
import { referenceName, sameFolder, type Worktree } from "./worktrees";

/**
 * Where a session that exists is sent to work: the folder of its project, a checkout of the repository of its project
 * (the folder of the project in it), or a new worktree that starts from a branch or the current commit.
 */
export type FolderTarget = Readonly<{ kind: "project" }> | Readonly<{ kind: "worktree"; folder: string }> | Readonly<{ kind: "new"; base: string | null }>;

export type FolderSwitchResult = Readonly<{ kind: "switched"; worktreePath: string | null; branch: string | null; warnings: readonly string[] }>
  | Readonly<{ kind: "error"; code: string; message: string | null }>;

type Invoke = (request: WorkspaceSwitchSessionFolderRequest, options: { timeoutMilliseconds: number }) => Promise<WorkspaceSwitchSessionFolderResponse>;

const epochPattern = /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/u;
const text = (value: unknown, limit: number): value is string => typeof value === "string" && value.length > 0 && value.length <= limit
  && value === value.trim() && !/[\u0000-\u001f\u007f-\u009f]/u.test(value);

/**
 * Sends a session to work in another folder of its project's repository, one change at a time. The session keeps its
 * conversation; no file moves. A new worktree can take minutes to check out: the call waits for it.
 */
export function createSessionFolderSwitch(invoke: Invoke) {
  let active = false;
  return async function switchFolder(epoch: string | null | undefined, project: Readonly<{ projectId: string; projectPath: string }>, sessionId: string,
    target: FolderTarget): Promise<FolderSwitchResult> {
    if (!epoch || !epochPattern.test(epoch)) return { kind: "error", code: "unconfigured", message: null };
    if (!text(sessionId, 256) || !text(project.projectId, 256) || !text(project.projectPath, 4096)
      || target.kind === "worktree" && !text(target.folder, 4096) || target.kind === "new" && target.base !== null && !referenceName(target.base))
      return { kind: "error", code: "invalid_scope", message: null };
    if (active) return { kind: "error", code: "busy", message: null };
    active = true;
    const request: WorkspaceSwitchSessionFolderRequest = { expectedHostEpoch: epoch, sessionId, projectId: project.projectId, projectPath: project.projectPath,
      target: target.kind, folder: target.kind === "worktree" ? target.folder : null, baseBranch: target.kind === "new" ? target.base : null };
    try {
      const response = await invoke(request, { timeoutMilliseconds: 600_000 });
      if (!response || typeof response.status !== "string" || response.hostEpoch !== epoch || response.sessionId !== sessionId)
        return { kind: "error", code: response?.status === "stale_epoch" ? "stale_epoch" : "switch_unconfirmed", message: null };
      const message = text(response.message, 2048) ? response.message : null;
      if (response.status !== "ok" && response.status !== "unchanged") return { kind: "error", code: response.status, message };
      if (response.worktreePath !== null && !text(response.worktreePath, 4096)) return { kind: "error", code: "switch_unconfirmed", message: null };
      // A session sent to the folder of its project has no worktree after the change, and one sent to a new worktree has one.
      if (target.kind === "project" ? response.worktreePath !== null : target.kind === "new" && response.worktreePath === null)
        return { kind: "error", code: "switch_unconfirmed", message: null };
      const warnings = Array.isArray(response.warnings) ? response.warnings.filter(value => typeof value === "string").slice(0, 8) : [];
      return { kind: "switched", worktreePath: response.worktreePath, branch: text(response.branch, 256) ? response.branch : null,
        warnings: message ? [...warnings, message] : warnings };
    } catch { return { kind: "error", code: "switch_unconfirmed", message: null }; }
    finally { active = false; }
  };
}

/**
 * The checkouts of the repository a session can be sent to: every one whose folder is there, but the one it works in.
 * The checkout the project lives in is the folder of the project, offered to a session that works in a worktree.
 */
export function folderTargets(worktrees: readonly Worktree[], current: string | null): readonly Worktree[] {
  return worktrees.filter(worktree => !worktree.missing && (worktree.project ? current !== null : !sameFolder(worktree.folder, current)));
}

const messages: Readonly<Record<string, MessageKey>> = {
  busy: "The session is working: its folder changes once it is idle.",
  session_in_use: "The session is working: its folder changes once it is idle.",
  background_running: "Background commands of the session run in its folder: change it once they end.",
  interaction_pending: "The session waits for an answer: give it first.",
  invalid: "That folder is not a checkout of the repository of this project.",
  not_found: "That folder is not a checkout of the repository of this project.",
  not_repository: "That folder is not a checkout of the repository of this project.",
  other_repository: "That folder is not a checkout of the repository of this project.",
  project_missing: "That folder is not a checkout of the repository of this project.",
  worktree_failed: "The worktree could not be created.",
  stale_epoch: "The host changed. Reload the window.",
};

/** What to tell the user when the folder of a session did not change. */
export function sessionFolderMessage(code: string): MessageKey {
  return messages[code] ?? "The folder of the session could not be changed.";
}
