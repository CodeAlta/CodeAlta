import { flushSync } from "react-dom";

/** Drives minute-long display intervals in mounted tests; short query/React timers still run normally. */
export function installRefreshClock() {
  const timers = new Map<number, { at: number; fire: () => void }>();
  let clock = 0, timerId = 0;
  const nativeSetTimeout = window.setTimeout.bind(window), nativeClearTimeout = window.clearTimeout.bind(window);
  window.setTimeout = ((handler: TimerHandler, delay = 0, ...args: unknown[]) => {
    if (delay < 60_000 || typeof handler !== "function") return nativeSetTimeout(handler, delay, ...args);
    const id = --timerId;
    timers.set(id, { at: clock + delay, fire: () => handler(...args) });
    return id;
  }) as typeof window.setTimeout;
  window.clearTimeout = ((id: number) => { timers.delete(id); nativeClearTimeout(id); }) as typeof window.clearTimeout;
  return (milliseconds: number) => {
    clock += milliseconds;
    flushSync(() => { for (const [id, timer] of timers) if (timer.at <= clock) { timers.delete(id); timer.fire(); } });
  };
}
