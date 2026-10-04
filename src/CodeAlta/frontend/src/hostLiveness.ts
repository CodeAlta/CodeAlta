/** How often the app pings the host, and how long one ping may take. */
export const hostPingInterval = 10_000;
export const hostPingTimeout = 5_000;
const missesUntilSilent = 3;

/**
 * Notices a host that stopped answering. The RPC session can be closed on the host side without the page
 * being told; requests are then dropped and every call only times out, while the last loaded state stays on
 * screen. Several pings missed in a row are the sign of that; reloading the window opens a new session.
 */
export function createHostLiveness(ping: () => Promise<unknown>) {
  let misses = 0;
  let checking = false;
  let closed = false;
  const listeners = new Set<() => void>();
  const silent = () => closed || misses >= missesUntilSilent;
  return {
    /** True once the host missed several pings in a row; false again as soon as it answers one. */
    getSnapshot: silent,
    subscribe(listener: () => void) {
      listeners.add(listener);
      return () => { listeners.delete(listener); };
    },
    /** The connection reported that the host closed it: silent from now on, whatever a later ping says. */
    lost() {
      if (closed) return;
      const before = silent();
      closed = true;
      if (!before) for (const listener of [...listeners]) listener();
    },
    /** Pings the host once, unless the previous ping is still waiting for its answer. */
    async check() {
      if (checking || closed) return;
      checking = true;
      const before = silent();
      try { await ping(); misses = 0; }
      catch { misses++; }
      finally { checking = false; }
      if (silent() !== before) for (const listener of [...listeners]) listener();
    },
  };
}
