// Gives a session another space through the host, and says what came of it.
import type { WorkspaceMoveToSpaceRequest, WorkspaceMoveToSpaceResponse } from "#neoastra";
import type { MessageKey } from "../localization";
import { sessionMoveMessage } from "../sessionMove";

type Invoke = (request: WorkspaceMoveToSpaceRequest, options: { timeoutMilliseconds: number }) => Promise<WorkspaceMoveToSpaceResponse>;

/** What a move to a space did: the sessions whose space changed, or why none did. */
export type SpaceMoveResult = Readonly<{ kind: "moved"; moved: readonly string[]; warnings: readonly string[] }>
  | Readonly<{ kind: "error"; code: string }>;

/** Asks the host to give a session, with the sessions it started, another space. */
export async function moveSessionToSpace(invoke: Invoke, epoch: string | null | undefined, sessionId: string, spaceId: string): Promise<SpaceMoveResult> {
  if (!epoch) return { kind: "error", code: "unconfigured" };
  try {
    const reply = await invoke({ expectedHostEpoch: epoch, sessionId, spaceId }, { timeoutMilliseconds: 60_000 });
    if (!reply || reply.hostEpoch !== epoch || reply.sessionId !== sessionId || reply.spaceId !== spaceId) return { kind: "error", code: "move_unconfirmed" };
    if (reply.status !== "ok") return { kind: "error", code: reply.status };
    return { kind: "moved", moved: reply.moved.filter(id => typeof id === "string"), warnings: reply.warnings.filter(text => typeof text === "string") };
  } catch { return { kind: "error", code: "move_unconfirmed" }; }
}

/** Why a session did not change space, in the words of the window. */
export function spaceMoveMessage(code: string): MessageKey {
  return code === "space_missing" ? "The space is gone. Choose another one." : sessionMoveMessage(code);
}
