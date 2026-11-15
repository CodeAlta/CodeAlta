import type { WorkspaceSessionStorageRequest, WorkspaceSessionStorageResponse } from "#neoastra";

type Invoke = (request: WorkspaceSessionStorageRequest, options: { timeoutMilliseconds: number }) => Promise<WorkspaceSessionStorageResponse>;

/** Where a session is stored on this computer, and whether the system has a file manager to show it in. */
export type SessionStorage = Readonly<{ folder: string; canReveal: boolean }>;

/** What the Session Info window asks the host about the folder of the session it shows. */
export type SessionStorageHost = Readonly<{
  /** The folder of the session; null when the host does not tell it (no journal, a host that changed). */
  read: () => Promise<SessionStorage | null>;
  /** Opens the folder in the file manager of the system, the journal of the session selected; false when it did not. */
  reveal: () => Promise<boolean>;
}>;

/**
 * The folder of one session, as the host of this window tells it. The host is given the session and the day it was
 * created, which is where its journal is filed; a failed or mismatched answer tells nothing.
 */
export function createSessionStorageHost(read: Invoke, reveal: Invoke, epoch: string | null | undefined, sessionId: string,
  createdAt: string | null | undefined): SessionStorageHost | null {
  if (!epoch || !sessionId || !createdAt) return null;
  const request: WorkspaceSessionStorageRequest = { expectedHostEpoch: epoch, sessionId, createdAt };
  const answer = async (invoke: Invoke) => {
    try {
      const response = await invoke(request, { timeoutMilliseconds: 8_000 });
      return response?.status === "ok" && response.hostEpoch === epoch && response.sessionId === sessionId && response.folder ? response : null;
    } catch { return null; }
  };
  return {
    read: async () => { const response = await answer(read); return response && { folder: response.folder!, canReveal: response.canReveal === true }; },
    reveal: async () => await answer(reveal) !== null,
  };
}
