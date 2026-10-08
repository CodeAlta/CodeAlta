import type { MessageKey } from "../localization";
import type { PluginComposerRequest } from "../pluginUi";
import type { RunsWith } from "./runsWith";
import type { WorkItem, WorkStart } from "./workItems";
import type { WorkItemsHub } from "./workItemsHub";

/** What starting the work of an item needs from the window. */
export type WorkStartHost = Readonly<{
  hub: Pick<WorkItemsHub, "act">;
  /** Asks the composer of a session to send or queue a prompt, as a prompt the user sends. */
  composer: (kind: PluginComposerRequest["kind"], sessionId: string, text?: string | null, agentPromptId?: string | null) => PluginComposerRequest;
  /** Shows the session that now carries the item out. */
  openSession: (sessionId: string, projectId: string) => void;
  /** Says why the work did not start. */
  refuse: (message: MessageKey | null, detail: string | null) => void;
}>;

/** The session an item is started from: the one that showed its card. */
export type WorkStartSession = Readonly<{ id: string; workingDirectory: string | null }>;

/**
 * Starts the work of a task or a plan. In a new session, with or without a new worktree, the host creates the
 * session and sends it the item. In the session that shows the item, the prompt goes through the composer of
 * that session: sent when the session is idle, queued behind its current work otherwise. A new session runs
 * with what the user chose; without a choice, with what the session that shows the item runs with, then with
 * what the item was proposed with, then with the defaults.
 */
export async function startWorkItem(host: WorkStartHost, item: WorkItem, start: WorkStart, session: WorkStartSession | null, runsWith: RunsWith | null = null): Promise<boolean> {
  if (start !== "here") {
    const outcome = await host.hub.act(item, start === "worktree" ? "start_worktree" : "start_session", { sessionId: session?.id ?? null, runsWith });
    // A session may exist although its prompt was refused: it is shown, with what went wrong.
    if (outcome.sessionId) host.openSession(outcome.sessionId, item.projectId);
    if (outcome.ok) return true;
    host.refuse(startRefusal(outcome.reason), outcome.message);
    return false;
  }

  if (!session) return false;
  const prepared = await host.hub.act(item, "start_here", { sessionId: session.id, workingDirectory: session.workingDirectory });
  if (!prepared.ok || !prepared.prompt) {
    host.refuse("The work did not start.", prepared.message);
    return false;
  }
  const busy = host.composer("state", session.id).state?.busy ?? false;
  if (host.composer(busy ? "enqueue" : "send", session.id, prepared.prompt, prepared.agentPromptId).result) return true;
  // The session did not take the prompt: nobody carries the item out.
  await host.hub.act(item, "release");
  host.refuse("This session cannot take it right now. Try again in a moment, or start it in a new session.", null);
  return false;
}

/** The text the window has for a refusal the host names. */
export function startRefusal(reason: string | null): MessageKey | null {
  return reason === "worktree_not_repository" || reason === "worktree_no_commit"
    ? "This project has no git repository with a commit: start it in a new session instead."
    : reason === "worktree_git_unavailable" ? "Git was not found: start it in a new session instead."
    : reason === "not_found" ? "It is no longer there."
    : null;
}
