// Gives a session another space through the host, and says what came of it, and of the sessions a project takes
// along when it changes spaces.
import type { WorkspaceMoveToSpaceRequest, WorkspaceMoveToSpaceResponse } from "#neoastra";
import type { MessageKey } from "../localization";
import type { SettingsNotice } from "../settingsEditing";
import { sessionMoveMessage } from "../sessionMove";
import { defaultSpaceId } from "./spaces";
import type { SessionsMove, SessionsOutcome } from "./spacesHub";

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

/**
 * What the page says of the sessions that a change of the spaces of a project moved: the ones at work that stayed
 * (they move by Move to space… once they are idle), why none could move, or how many went. Null when none were to move.
 */
export function sessionsNotice(sessions: SessionsOutcome | undefined, t: (key: MessageKey) => string): SettingsNotice | null {
  if (!sessions) return null;
  if (sessions.left.length > 0) {
    const count = sessions.left.length;
    return { intent: "warning", values: { count }, detail: sessions.left.map(left => left.title || t("Untitled session")).join(", "),
      key: count === 1 ? "1 session is at work and stayed in its space. Move it with Move to space… once it is idle."
        : "{count} sessions are at work and stayed in their space. Move them with Move to space… once they are idle." };
  }
  if (sessions.status !== "ok")
    return { intent: "warning", key: "The project changed space, but its sessions did not: move them with Move to space…", detail: t(spaceMoveMessage(sessions.status)) };
  if (sessions.moved === 0) return null;
  return { intent: "success", values: { count: sessions.moved },
    key: sessions.moved === 1 ? "1 session moved with the project. Its next turn uses the providers, tools and identity of its new space."
      : "{count} sessions moved with the project. Their next turn uses the providers, tools and identity of their new space." };
}

/**
 * The sessions that go with a project when it joins a space (those of the default space) or leaves it (those of that
 * space, back to the default one): what a drag from the list, the × of a card and the checkbox of a space page ask.
 */
export function joinedSessions(spaceId: string, joins: boolean): SessionsMove {
  return joins ? { from: defaultSpaceId, to: spaceId } : { from: spaceId, to: defaultSpaceId };
}
