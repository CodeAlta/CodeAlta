/**
 * The width of a conversation: how much of the space of a session its timeline and its prompt take, as a
 * percentage. They stay centered. It is one setting of the user, for every session; a session can be shown with a
 * width of its own, which an `alta appearance` command gives it.
 */
export const defaultSessionWidth = 100;
export const minimumSessionWidth = 40;
/** The step of the setting and of the keyboard; a drag follows the pointer to the percent. */
export const sessionWidthStep = 5;

export function validSessionWidth(value: number) { return Number.isInteger(value) && value >= minimumSessionWidth && value <= defaultSessionWidth; }

/** Brings any number to a width that can be kept: a whole percentage between the least and the whole space. */
export function clampSessionWidth(value: number) {
  return Number.isFinite(value) ? Math.min(defaultSessionWidth, Math.max(minimumSessionWidth, Math.round(value))) : defaultSessionWidth;
}

/**
 * The width a pointer asks for when it drags an edge of the prompt: the conversation is centered in its space,
 * so the edge under the pointer is as far from the middle as the other one.
 * @param x Where the pointer is.
 * @param left The left edge of the space of the session.
 * @param width The width of that space.
 */
export function sessionWidthAt(x: number, left: number, width: number) {
  if (!(width > 0)) return defaultSessionWidth;
  return clampSessionWidth(Math.abs(x - (left + width / 2)) * 2 / width * 100);
}

/** What the stylesheet reads: the width as a percentage of the space of a session. */
export const sessionWidthProperty = "--session-width";
export function applySessionWidth(element: HTMLElement, value: number) { element.style.setProperty(sessionWidthProperty, `${clampSessionWidth(value)}%`); }

/** The widths some sessions are shown with, as the host lists them: one for each session, the valid ones only. */
export function sessionWidthsOf(listed: ReadonlyArray<Readonly<{ sessionId: string; percent: number }>> | null | undefined): ReadonlyMap<string, number> {
  const widths = new Map<string, number>();
  for (const item of listed ?? []) if (item.sessionId && validSessionWidth(item.percent)) widths.set(item.sessionId.toLowerCase(), item.percent);
  return widths;
}

/** The widths after the host said that one session has a width of its own, or (a width of 0) follows the setting again. */
export function withSessionWidth(widths: ReadonlyMap<string, number>, sessionId: string, percent: number): ReadonlyMap<string, number> {
  const key = sessionId.toLowerCase();
  if (validSessionWidth(percent) ? widths.get(key) === percent : !widths.has(key)) return widths;
  const next = new Map(widths);
  if (validSessionWidth(percent)) next.set(key, percent); else next.delete(key);
  return next;
}
