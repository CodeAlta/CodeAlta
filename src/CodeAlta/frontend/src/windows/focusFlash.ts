// The short mark of a row or a tab that a click sent to another window, or that another window asked to bring forward: it
// says the click was heard even where nothing else changes on screen.

const flashMilliseconds = 1700;
// What ends the mark of each element that is pointed out.
const flashing = new WeakMap<Element, () => void>();

/**
 * Points an element out for a moment (`data-window-flash`, drawn by windows.css). Asked again while it is pointed out, the
 * mark starts over, for its whole time. The attribute is removed by a timer too, since a window that reduces motion plays
 * no animation.
 */
export function flashElement(element: Element | null | undefined) {
  if (!element) return;
  // The mark that is shown ends first: its timer would cut the new one short.
  flashing.get(element)?.();
  // Reading the layout restarts the animation of an element that was just marked.
  void (element as HTMLElement).offsetWidth;
  element.setAttribute("data-window-flash", "true");
  // The animation of the element itself, not one that ends inside it.
  const ended = (event: Event) => { if (event.target === element) clear(); };
  const clear = () => {
    clearTimeout(timer);
    element.removeEventListener("animationend", ended);
    element.removeAttribute("data-window-flash");
    flashing.delete(element);
  };
  const timer = setTimeout(clear, flashMilliseconds);
  element.addEventListener("animationend", ended);
  flashing.set(element, clear);
}

/** The rows of the Explorer that list a session, in every scope that shows it. */
export function rowsOfSession(root: ParentNode, sessionId: string): Element[] {
  return Array.from(root.querySelectorAll(`.session-row[data-family-id="${CSS.escape(sessionId)}"]`));
}

/** The tab of a node of the dock (`data-session-node` is drawn inside each tab). */
export function dockTab(root: ParentNode, nodeId: string): Element | null {
  return root.querySelector(`[data-session-node="${CSS.escape(nodeId)}"]`)?.closest(".flexlayout__tab_button") ?? null;
}
