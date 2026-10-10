/**
 * A read that runs one at a time. Asked for while it runs, or while something blocks it (a move that publishes its
 * own list), it is not dropped: it runs once more when the read ends, or when {@link resume} says the block is over.
 */
export function createCoalescedRead(blocked: () => boolean, read: () => Promise<void>) {
  let running = false;
  let deferred = false;
  const run = (): void => {
    if (running || blocked()) { deferred = true; return; }
    running = true;
    deferred = false;
    void read().catch(() => {}).finally(() => {
      running = false;
      if (deferred && !blocked()) run();
    });
  };
  return {
    run,
    /** What a read found it could not use, because a block began while it ran: it is read again after. */
    defer: () => { deferred = true; },
    /** The block is over: a read asked for meanwhile runs now. */
    resume: (force = false) => { if (force || deferred) run(); },
  };
}
