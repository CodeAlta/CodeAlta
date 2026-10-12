// Which session shows the window of a fork or a rewind, and on what. A command, a message of the timeline and the
// menu of a session row all ask here; the pane of the session follows its own request and shows the window. The
// window of the application does not follow this store: opening the window renders one pane.
import type { BranchMode } from "./branchPoints";

/** A request to show the window: for a session, in a mode, on the turn of a message when one was pointed at. */
export type BranchRequest = Readonly<{ sessionId: string; mode: BranchMode; promptOffset: string | null }>;

export function createBranchRequests() {
  let current: BranchRequest | null = null;
  const listeners = new Set<() => void>();
  const publish = () => { for (const listener of [...listeners]) listener(); };
  return {
    subscribe: (listener: () => void) => { listeners.add(listener); return () => { listeners.delete(listener); }; },
    /** The request of a session: the same object until it is closed or replaced, null for every other session. */
    forSession: (sessionId: string): BranchRequest | null => current?.sessionId === sessionId ? current : null,
    /** Asks for the window. A request replaces the one before: one window is shown at a time. */
    open(sessionId: string, mode: BranchMode, promptOffset: string | null = null) {
      current = { sessionId, mode, promptOffset };
      publish();
    },
    /** Closes the window of a request; a request made since stays. */
    close(request: BranchRequest | null) {
      if (!current || request !== null && request !== current) return;
      current = null;
      publish();
    },
    /**
     * Closes the window of a session once its change is made. The pane that showed it may be gone by then (a
     * rewind mounts the pane of its session again), and the pane that takes its place must not show it anew.
     */
    done(sessionId: string) {
      if (current?.sessionId !== sessionId) return;
      current = null;
      publish();
    },
  };
}

export type BranchRequests = ReturnType<typeof createBranchRequests>;

/** Where the choice of keeping the turns a rewind removes is remembered. */
export const keepRemovedTurnsKey = "codealta.desktop.rewind.keep-removed-turns.v1";

/** Whether a rewind keeps what it removes in a session of its own: yes, unless the user chose otherwise. */
export function readKeepRemovedTurns(read: (key: string) => string | null): boolean {
  try { return read(keepRemovedTurnsKey) !== "false"; } catch { return true; }
}

export function saveKeepRemovedTurns(write: (key: string, value: string) => void, keep: boolean): void {
  try { write(keepRemovedTurnsKey, keep ? "true" : "false"); } catch { /* The choice holds for this window only. */ }
}
