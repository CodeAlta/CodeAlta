/** The clock and the timers of the calm order: the window's, or a test's. */
export type CalmClock = Readonly<{
  now(): number;
  setTimer(callback: () => void, ms: number): unknown;
  clearTimer(timer: unknown): void;
}>;

/** How long the list waits: after the last change of its order, after the last touch or scroll, and at most since the first change. */
export type CalmTiming = Readonly<{ quietMs: number; maxWaitMs: number }>;

/** The list moves its cards 5 s after its order last changed, 5 s after the last touch or scroll, and at most 15 s after it first changed. */
export const calmTiming: CalmTiming = { quietMs: 5000, maxWaitMs: 15_000 };

/**
 * When the list of sessions takes its new order. While the user looks at the list, cards change in place: an order that
 * changed (another session became the latest active) waits until the list has been quiet, `quietMs` after the last change
 * and after the last touch or scroll, never while a finger is down; at most `maxWaitMs` after it first changed, so that
 * sessions that keep taking turns cannot hold it forever, but still never within `quietMs` of a touch. `settle` applies it
 * at once: when the user comes back to the list or refreshes it.
 *
 * The order shown is held as the rank of each session (`held`); null means the list follows the latest order as it is.
 */
export function createCalmOrder(options: Readonly<{ clock: CalmClock; timing?: CalmTiming; onSettle: () => void }>) {
  const { clock, onSettle } = options;
  const timing = options.timing ?? calmTiming;
  let held: Map<string, number> | null = null;
  // Since when the order shown is not the latest one, and when it last changed; null while it is the latest.
  let staleSince: number | null = null, changedAt = 0;
  let touchedAt = -Infinity, down = false;
  let latestKey = "";
  let timer: unknown = null;

  const due = () => Math.max(Math.min(changedAt + timing.quietMs, staleSince! + timing.maxWaitMs), touchedAt + timing.quietMs);
  const stop = () => { if (timer !== null) clock.clearTimer(timer); timer = null; };
  const schedule = () => {
    stop();
    if (staleSince === null || down) return;
    timer = clock.setTimer(fire, Math.max(0, due() - clock.now()));
  };
  function fire() {
    timer = null;
    if (staleSince === null || down) return;
    if (clock.now() < due()) { schedule(); return; }
    release();
    onSettle();
  }
  const release = () => { stop(); held = null; staleSince = null; };

  return {
    /** The ranks of the sessions as last shown, which the next drawing keeps; null to draw the latest order. */
    get held(): ReadonlyMap<string, number> | null { return held; },
    /** Whether a new order waits. */
    get waiting() { return staleSince !== null; },
    /** The list was drawn in this order, top to bottom, while the latest order is `latest`. */
    shown(ids: readonly string[], latest: readonly string[]) {
      held = new Map(ids.map((id, index) => [id, index]));
      const key = latest.join("\n"), changed = key !== latestKey;
      latestKey = key;
      if (ids.length === latest.length && ids.every((id, index) => id === latest[index])) { stop(); staleSince = null; return; }
      // A drawing for a live update that leaves the latest order as it was does not push the reorder back.
      if (!changed && staleSince !== null) return;
      const now = clock.now();
      if (staleSince === null) staleSince = now;
      changedAt = now;
      schedule();
    },
    /** A finger went down on the list: nothing moves until it is lifted and the list was quiet again. */
    down() { down = true; touchedAt = clock.now(); stop(); },
    /** The finger was lifted. */
    up() { down = false; touchedAt = clock.now(); schedule(); },
    /** The list scrolled. */
    scrolled() { touchedAt = clock.now(); if (!down) schedule(); },
    /** Takes the latest order now (the user came back to the list, or refreshed it): the caller draws it. */
    settle() { release(); down = false; },
    /** Stops the timer. */
    dispose: stop,
  };
}

/** The calm order of a list. */
export type CalmOrder = ReturnType<typeof createCalmOrder>;
