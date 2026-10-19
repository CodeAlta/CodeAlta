/** A host that closes, as `boot.status` says it: the step of its close it waits for, and whether the page may end it. */
export type HostClosing = Readonly<{ step: string | null; seconds: number; canForceExit: boolean }>;

/** How often the page asks the host while it closes: the step and the time it shows follow it. */
export const closingPingInterval = 2_000;

/**
 * Reads the `closing` fact of a `boot.status` reply. Anything malformed reads as a host that is not closing: a reply
 * never breaks the window, and the window never offers to end a process on a value it could not read.
 */
export function readHostClosing(status: unknown): HostClosing | null {
  if (!status || typeof status !== "object") return null;
  const closing = (status as { closing?: unknown }).closing;
  if (!closing || typeof closing !== "object") return null;
  const { step, seconds, canForceExit } = closing as { step?: unknown; seconds?: unknown; canForceExit?: unknown };
  if (typeof seconds !== "number" || !Number.isFinite(seconds) || seconds < 0 || typeof canForceExit !== "boolean") return null;
  const shownStep = typeof step === "string" && step.length > 0 && step.length <= 64 ? step : null;
  return { step: shownStep, seconds: Math.floor(seconds), canForceExit };
}
