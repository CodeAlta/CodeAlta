// How long after a tap outside the menu its click is swallowed: the tap closes the menu, it does not act too.
const swallowWindow = 600;

/**
 * The menu of "⋯" in the top bar: it opens under its button, and a tap outside it, Escape or Back closes it (it has an
 * entry of its own in the history, as the viewers do). An item that changes the page (a route, a session archived) acts
 * once the menu has closed: its entry is gone first, so that the new page is not the one Back leaves.
 */
export function createBarMenu(options: Readonly<{ window: Window & typeof globalThis; button: HTMLButtonElement; menu: HTMLElement; historyKey?: string; onOpen?: () => void }>) {
  const { window, button, menu } = options, document = window.document, historyKey = options.historyKey ?? "barMenu";
  let open = false, after: (() => void) | null = null, swallowTimer = 0;
  const ownEntry = () => !!(window.history.state as Record<string, unknown> | null)?.[historyKey];

  const swallow = (event: Event) => { event.preventDefault(); event.stopPropagation(); stopSwallowing(); };
  function stopSwallowing() { window.clearTimeout(swallowTimer); document.removeEventListener("click", swallow, true); }
  const outside = (event: PointerEvent) => {
    const target = event.target as Node | null;
    if (target && (menu.contains(target) || button.contains(target))) return;
    stopSwallowing();
    document.addEventListener("click", swallow, true);
    swallowTimer = window.setTimeout(stopSwallowing, swallowWindow);
    close();
  };
  const keys = (event: KeyboardEvent) => {
    if (event.key !== "Escape") return;
    event.preventDefault();
    event.stopPropagation();
    close();
    button.focus();
  };
  const popped = () => { if (!ownEntry()) end(); };

  function show() {
    if (open) return;
    open = true;
    options.onOpen?.();
    menu.hidden = false;
    button.setAttribute("aria-expanded", "true");
    window.history.pushState({ ...(window.history.state as object | null ?? {}), [historyKey]: true }, "");
    window.addEventListener("popstate", popped);
    document.addEventListener("pointerdown", outside, true);
    document.addEventListener("keydown", keys, true);
  }
  // Closes the menu at once, and does what waited for it.
  function end() {
    if (!open) return;
    open = false;
    menu.hidden = true;
    button.setAttribute("aria-expanded", "false");
    window.removeEventListener("popstate", popped);
    document.removeEventListener("pointerdown", outside, true);
    document.removeEventListener("keydown", keys, true);
    const then = after;
    after = null;
    then?.();
  }
  /** Closes the menu by its entry in the history, then does `then`. */
  function close(then?: () => void) {
    if (!open) { then?.(); return; }
    after = then ?? null;
    if (ownEntry()) window.history.back(); else end();
  }

  button.addEventListener("click", () => { if (open) close(); else show(); });

  return {
    open: show,
    close,
    /** Closes the menu at once, without its entry in the history: the page changed under it. */
    dismiss: () => { after = null; end(); },
    isOpen: (): boolean => open,
  };
}

/** The menu of "⋯". */
export type BarMenu = ReturnType<typeof createBarMenu>;
