// Which session shows one message of its timeline, named by the offset its journal holds it at. The manager of the
// bookmarks and the rail of a timeline ask here; the pane of the session follows its own request, reads older history
// until its window holds the message, and shows it. The window of the application does not follow this store.

/** A request to show a message: each one is its own object, so that asking for the same message again shows it again. */
export type RevealRequest = Readonly<{ sessionId: string; offset: string }>;
/** How a request ended: the message is shown, the session no longer holds it, or its history could not be read. */
export type RevealOutcome = "shown" | "missing" | "failed";

/** How long a request waits for the pane of its session: one that no pane took is dropped, and a session opened later starts where it was. */
export const revealWaitMilliseconds = 20_000;

type Timers = Readonly<{ set: (run: () => void, milliseconds: number) => unknown; clear: (timer: unknown) => void }>;

export function createRevealRequests(timers: Timers = { set: (run, milliseconds) => setTimeout(run, milliseconds), clear: timer => clearTimeout(timer as number) }) {
  let current: RevealRequest | null = null;
  // Whether a pane took the request: it then lasts as long as the pane needs to read the history.
  let taken = false;
  let waiting: unknown = null;
  const listeners = new Set<() => void>();
  const publish = () => { for (const listener of [...listeners]) listener(); };
  const forget = () => { if (waiting !== null) timers.clear(waiting); waiting = null; };
  return {
    subscribe: (listener: () => void) => { listeners.add(listener); return () => { listeners.delete(listener); }; },
    /** The request of a session: the same object until it is done or replaced, null for every other session. Asking for it takes it. */
    forSession(sessionId: string): RevealRequest | null {
      if (current?.sessionId !== sessionId) return null;
      taken = true;
      return current;
    },
    /** Asks for a message. A request replaces the one before: one message is gone to at a time. */
    open(sessionId: string, offset: string): RevealRequest {
      forget();
      const request = current = { sessionId, offset };
      taken = false;
      waiting = timers.set(() => { waiting = null; if (current === request && !taken) { current = null; publish(); } }, revealWaitMilliseconds);
      publish();
      return request;
    },
    /** Ends a request; a request made since stays. */
    done(request: RevealRequest) {
      if (current !== request) return;
      forget();
      current = null;
      publish();
    },
  };
}

export type RevealRequests = ReturnType<typeof createRevealRequests>;

// Offsets are canonical decimal strings: a longer one, or a greater one of equal length, is higher.
const offsetAfter = (left: string, right: string) => left.length > right.length || left.length === right.length && left > right;

/**
 * What a window of history does about a message it is asked to show: it holds it (`shown`); it has older history to
 * read first (`older`); it slid away from the end of the journal and the message is after it, so the newest history
 * is read first (`newest`); or the offset names nothing the timeline can have (`missing`).
 */
export function revealStep(entries: readonly Readonly<{ offset: string }>[], hasOlder: boolean, newerOmitted: boolean, offset: string): "shown" | "older" | "newest" | "missing" {
  if (entries.length === 0) return hasOlder ? "older" : "missing";
  if (offsetAfter(entries[0].offset, offset)) return hasOlder ? "older" : "missing";
  if (newerOmitted && offsetAfter(offset, entries[entries.length - 1].offset)) return "newest";
  return entries.some(entry => entry.offset === offset) ? "shown" : "missing";
}
