/** How often the window pings a host that answers. */
export const hostPingInterval = 10_000;
/** How often the window looks at the ping it waits for: the time it shows follows it. */
export const hostCheckInterval = 2_000;
/** How long a ping waits before the window says the host is busy: long enough that a loaded computer is no false alarm. */
export const hostBusyAfter = 10_000;

/**
 * What the window knows of its host: it answers, it is busy (a ping has waited `seconds` without an answer), or the
 * connection to it is lost, which only a reload recovers.
 */
export type HostLivenessState = Readonly<{ busy: boolean; seconds: number; lost: boolean }>;

const answering: HostLivenessState = Object.freeze({ busy: false, seconds: 0, lost: false });

export type HostLivenessOptions = Readonly<{
  now?: () => number;
  /** How often a host that answers is pinged; read at each check, so a closing host can be pinged more often. */
  pingInterval?: () => number;
  busyAfter?: number;
  /** Where a busy host and its recovery are written down. */
  report?: (level: "warning" | "information", message: string) => void;
}>;

/**
 * Notices a host that does not answer. A ping is never given up on a timer: the host runs on this computer and answers
 * every message while the connection lives, so a ping that waits says the host is busy, for as long as it waits. The
 * window shows it and keeps everything as it is; the answer clears it. A failed ping is an answer too. Only a closed
 * connection ({@link lost}) is final.
 */
export function createHostLiveness(ping: () => Promise<unknown>, options: HostLivenessOptions = {}) {
  const now = options.now ?? (() => Date.now());
  const pingInterval = options.pingInterval ?? (() => hostPingInterval);
  const busyAfter = options.busyAfter ?? hostBusyAfter;
  const report = (level: "warning" | "information", message: string) => { try { options.report?.(level, message); } catch { /* Never breaks a check. */ } };
  let state = answering;
  let pendingSince: number | null = null;
  let lastPing = Number.NEGATIVE_INFINITY;
  let closed = false;
  const listeners = new Set<() => void>();
  const set = (next: HostLivenessState) => {
    if (next.busy === state.busy && next.seconds === state.seconds && next.lost === state.lost) return;
    state = next;
    for (const listener of [...listeners]) { try { listener(); } catch { /* A listener never breaks the others. */ } }
  };
  return {
    /** The state of the host; the same object until it changes. */
    getSnapshot: () => state,
    subscribe(listener: () => void) {
      listeners.add(listener);
      return () => { listeners.delete(listener); };
    },
    /** The connection reported that the host closed it: lost from now on, whatever a later ping says. */
    lost() {
      if (closed) return;
      closed = true;
      set({ busy: true, seconds: state.seconds, lost: true });
    },
    /**
     * Looks at the ping that waits, or pings the host when the last answer is old enough. Settles when that ping is
     * answered: callers do not wait for it.
     */
    async check() {
      if (closed) return;
      const time = now();
      if (pendingSince !== null) {
        const waited = time - pendingSince;
        if (waited >= busyAfter) {
          if (!state.busy) report("warning", `The host has not answered for ${Math.floor(waited / 1000)} s; the window waits for it.`);
          set({ busy: true, seconds: Math.floor(waited / 1000), lost: false });
        }
        return;
      }
      if (time - lastPing < pingInterval()) return;
      lastPing = time;
      pendingSince = time;
      try { await ping(); }
      catch { /* A failed ping is an answer: the host is there. A closed connection is reported by lost(). */ }
      finally {
        const waited = now() - (pendingSince ?? time);
        pendingSince = null;
        if (!closed && state.busy) {
          report("information", `The host answered again after ${(waited / 1000).toFixed(1)} s.`);
          set(answering);
        }
      }
    },
  };
}

export type HostLiveness = ReturnType<typeof createHostLiveness>;
