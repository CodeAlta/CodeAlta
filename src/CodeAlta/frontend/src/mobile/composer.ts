import type { PromptMode } from "./contract";

/** What the composer of a session shows. */
export type ComposerView = Readonly<{
  /** The primary button: Send while idle; Steer (or Queue, when the phone queues first) while a turn runs; null when hidden. */
  primary: PromptMode | null;
  /** The second button while a turn runs: Queue, or Steer when the phone queues first; null when hidden. */
  secondary: "queue" | "steer" | null;
  stop: boolean;
  /** What the composer says of its state; the box shows it while it is empty. */
  hint: string;
  /** What submitting the form does (the Enter of a keyboard, the primary button). */
  submit: PromptMode;
}>;

/**
 * The composer, after the desktop's. Idle: Send. While a turn runs: only Stop until new text is typed, then Queue
 * (after this turn), and Steer (into it) when the server says the session can be steered. Steer is the primary action
 * unless the phone was set to queue first.
 */
export function composerView(busy: boolean, typed: boolean, canSteer: boolean, queueFirst = false): ComposerView {
  if (!busy) return { primary: "send", secondary: null, stop: false, hint: "", submit: "send" };
  const submit: PromptMode = canSteer && !queueFirst ? "steer" : "queue";
  if (!typed) return { primary: null, secondary: null, stop: true, hint: canSteer ? "Working… type to steer or queue" : "Working… type to queue", submit };
  if (!canSteer) return { primary: null, secondary: "queue", stop: true, hint: "Queue: runs after this turn", submit };
  return queueFirst
    ? { primary: "queue", secondary: "steer", stop: true, hint: "Queue: runs after this turn · Steer: into this turn", submit }
    : { primary: "steer", secondary: "queue", stop: true, hint: "Steer: into this turn · Queue: runs after this turn", submit };
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
