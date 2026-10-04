/** The connection close reason NeoAstra reports when the host closed the RPC session of a document that keeps running. */
export const rpcSessionClosed = "rpc_session_closed";
export const sessionRecoveryKey = "codealta.desktop.sessionRecovery.v1";
/** A second loss this soon after an automatic reload is left to the user: reloading again would only loop. */
export const sessionRecoveryInterval = 60_000;

/**
 * Whether the window reloads by itself after its connection closed. Only a session the host closed is
 * recovered that way (a reload opens a new one and prompt drafts are restored); never while a file holds
 * unsaved edits, which a reload would drop, and not twice within `sessionRecoveryInterval`.
 */
export function reloadsAfterClose(reason: string | undefined, unsavedEdits: boolean, now: number, lastReload: number | null): boolean {
  return reason === rpcSessionClosed && !unsavedEdits
    && (lastReload === null || !Number.isFinite(lastReload) || now - lastReload >= sessionRecoveryInterval || now < lastReload);
}
