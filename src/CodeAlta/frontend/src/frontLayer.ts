/**
 * The window of the application in front, when one is open (Settings, the session browser, any dialog of the
 * shell): a modal dialog in the browser's top layer, above the rest of the page and whatever is added to it.
 * A question asked from outside such a window is shown inside it, or it would be behind and out of reach.
 */
export function frontLayer(): HTMLElement | undefined {
  // The keyboard is in the window in front; the others are inert while it is open.
  const focused = document.activeElement?.closest<HTMLElement>("dialog[open]");
  if (focused) return focused;
  const open = document.querySelectorAll<HTMLElement>("dialog[open]");
  return open.length > 0 ? open[open.length - 1] : undefined;
}
