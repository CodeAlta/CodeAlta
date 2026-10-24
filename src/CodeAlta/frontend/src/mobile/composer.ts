import type { PromptMode } from "./contract";

/** What the composer of a session shows. */
export type ComposerView = Readonly<{
  /** The primary button: Send while idle, Steer while a turn runs; null when hidden. */
  primary: "send" | "steer" | null;
  queue: boolean;
  stop: boolean;
  hint: string;
  /** What submitting the form does (the Enter of a keyboard, the primary button). */
  submit: PromptMode;
}>;

/**
 * The composer, after the desktop's. Idle: Send. While a turn runs: only Stop until new text is typed, then Queue
 * (after this turn), and Steer (into it) when the server says the session can be steered.
 */
export function composerView(busy: boolean, typed: boolean, canSteer: boolean): ComposerView {
  if (!busy) return { primary: "send", queue: false, stop: false, hint: "", submit: "send" };
  const steer = canSteer && typed;
  return {
    primary: steer ? "steer" : null,
    queue: typed,
    stop: true,
    hint: typed ? (canSteer ? "Steer: into this turn · Queue: runs after this turn" : "Queue: runs after this turn")
      : (canSteer ? "Working… type to steer or queue" : "Working… type to queue"),
    submit: canSteer ? "steer" : "queue",
  };
}

/**
 * Runs a task one at a time: a call while it runs asks for one more run after it, whatever the number of calls, and
 * resolves when that run is done.
 */
export function coalesce(task: () => Promise<void>): () => Promise<void> {
  let running: Promise<void> | null = null, again = false;
  return () => {
    if (running) { again = true; return running; }
    running = (async () => {
      try { do { again = false; await task(); } while (again); }
      finally { running = null; }
    })();
    return running;
  };
}
