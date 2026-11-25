// The short mark of a row or a tab that a click sent to another window, or that another window asked to bring forward: it
// says the click was heard even where nothing else changes on screen.

const flashMilliseconds = 1700;

/**
 * Points an element out for a moment (`data-window-flash`, drawn by windows.css). Asked again while it is pointed out, the
 * mark starts over. The attribute is removed by a timer too, since a window that reduces motion plays no animation.
 */
export function flashElement(element: Element | null | undefined) {
  if (!element) return;
  element.removeAttribute("data-window-flash");
  // Reading the layout restarts the animation of an element that was just marked.
  void (element as HTMLElement).offsetWidth;
  element.setAttribute("data-window-flash", "true");
  const clear = () => element.removeAttribute("data-window-flash");
  element.addEventListener("animationend", clear, { once: true });
  setTimeout(clear, flashMilliseconds);
}

/** The rows of the Explorer that list a session, in every scope that shows it. */
export function rowsOfSession(root: ParentNode, sessionId: string): Element[] {
  return Array.from(root.querySelectorAll(`.session-row[data-family-id="${CSS.escape(sessionId)}"]`));
}

/** The tab of a node of the dock (`data-session-node` is drawn inside each tab). */
export function dockTab(root: ParentNode, nodeId: string): Element | null {
  return root.querySelector(`[data-session-node="${CSS.escape(nodeId)}"]`)?.closest(".flexlayout__tab_button") ?? null;
}
