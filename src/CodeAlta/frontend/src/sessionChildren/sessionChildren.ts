// The sessions below a session, wherever they are, and their deletion with it.
import type { WorkspaceDeleteSessionTreeRequest, WorkspaceDeleteSessionTreeResponse, WorkspaceSession, WorkspaceSessionChild,
  WorkspaceSessionChildrenRequest, WorkspaceSessionChildrenResponse } from "#neoastra";
import type { MessageKey } from "../localization";
import type { RenameTarget } from "../sessionRename";

type ReadInvoke = (request: WorkspaceSessionChildrenRequest, options: { timeoutMilliseconds: number }) => Promise<WorkspaceSessionChildrenResponse>;
type DeleteInvoke = (request: WorkspaceDeleteSessionTreeRequest, options: { timeoutMilliseconds: number }) => Promise<WorkspaceDeleteSessionTreeResponse>;

/** A session with the sessions below it, as the host read them. */
export type SessionChildren = Readonly<{
  root: WorkspaceSession;
  rootAtWork: boolean;
  /** The tree read from the top: each child follows the session it is shown under. */
  children: readonly WorkspaceSessionChild[];
  /** Every descendant, shown or not: what a deletion of the tree confirms. */
  descendantIds: readonly string[];
  direct: number;
  total: number;
  /** Some descendants are not in `children`. */
  truncated: boolean;
}>;

export type SessionChildrenRead = Readonly<{ kind: "read"; value: SessionChildren }> | Readonly<{ kind: "error"; code: string }>;

/** Reads the sessions below a session from the host: across projects, chats and spaces. */
export async function readSessionChildren(invoke: ReadInvoke, epoch: string | null | undefined, sessionId: string): Promise<SessionChildrenRead> {
  if (!epoch) return { kind: "error", code: "unconfigured" };
  try {
    const reply = await invoke({ expectedHostEpoch: epoch, sessionId }, { timeoutMilliseconds: 30_000 });
    if (!reply || reply.hostEpoch !== epoch || reply.sessionId !== sessionId) return { kind: "error", code: "read_failed" };
    if (reply.status !== "ok" || !reply.root) return { kind: "error", code: reply.status === "ok" ? "read_failed" : reply.status };
    return { kind: "read", value: { root: reply.root, rootAtWork: reply.rootAtWork, children: reply.children, descendantIds: reply.descendantIds,
      direct: reply.directCount, total: reply.totalCount, truncated: reply.truncated } };
  } catch { return { kind: "error", code: "read_failed" }; }
}

/** The sessions of the tree that something goes on in: the session itself included. */
export function sessionsAtWork(tree: SessionChildren): readonly WorkspaceSession[] {
  return [...(tree.rootAtWork ? [tree.root] : []), ...tree.children.filter(child => child.atWork).map(child => child.session)];
}

/**
 * Whether the tree can be deleted in one go: it has descendants, the host listed them all, and nothing goes on in
 * any of them. A run is never stopped to delete its session.
 */
export function treeDeletable(tree: SessionChildren): boolean {
  return tree.total > 0 && tree.descendantIds.length === tree.total && sessionsAtWork(tree).length === 0;
}

export type SessionTreeDeletion = Readonly<{ kind: "deleted"; deleted: readonly string[] }>
  | Readonly<{ kind: "error"; code: string; deleted: readonly string[]; blocking: string | null }>;

/**
 * Deletes a session with the sessions below it, those the user saw: the host refuses when they changed, and when one
 * of them is at work. The session is named by its title as the list shows it, as Delete does.
 */
export async function deleteSessionTree(invoke: DeleteInvoke, epoch: string | null | undefined, target: RenameTarget,
  tree: SessionChildren): Promise<SessionTreeDeletion> {
  if (!epoch) return { kind: "error", code: "unconfigured", deleted: [], blocking: null };
  const request: WorkspaceDeleteSessionTreeRequest = { expectedHostEpoch: epoch, scope: target.scope,
    projectId: target.scope === "project" ? target.projectId : null, projectPath: target.projectPath,
    sessionId: tree.root.id, confirmedTitle: tree.root.title, descendants: [...tree.descendantIds] };
  try {
    const reply = await invoke(request, { timeoutMilliseconds: 120_000 });
    if (!reply || reply.hostEpoch !== epoch || reply.sessionId !== tree.root.id)
      return { kind: "error", code: "delete_unconfirmed", deleted: [], blocking: null };
    const deleted = reply.deleted.filter(id => typeof id === "string");
    if (reply.status === "ok") return { kind: "deleted", deleted };
    return { kind: "error", code: reply.status, deleted, blocking: reply.blockingSessionId };
  } catch { return { kind: "error", code: "delete_unconfirmed", deleted: [], blocking: null }; }
}

/** Why the child sessions could not be read or deleted, in the words of the window. */
export function sessionTreeMessage(code: string): MessageKey {
  switch (code) {
    case "descendants_changed": return "The child sessions changed. Check them again before deleting.";
    case "session_in_use": return "A session of this tree is at work. Wait for it to finish.";
    case "session_missing": case "scope_missing": return "The session, its title or its project changed. Check it again before deleting.";
    case "delete_partial": return "Some sessions were deleted, then one could not be. The list shows what is left.";
    case "busy": return "A session operation is in progress. Nothing was deleted.";
    case "stale_epoch": return "The host changed. Reload before deleting.";
    case "closed": case "unconfigured": return "The host is closing. Nothing was deleted.";
    case "read_failed": return "The child sessions could not be read.";
    default: return "Deletion may have completed. Check the sessions again; nothing will be retried.";
  }
}
