/**
 * A bottom sheet as a modal dialog: the rest of the page is inert while it is open, focus moves into it and comes
 * back where it was, Escape and a tap outside its panel close it.
 */
export function createSheet(document: Document, sheet: HTMLElement, onClose?: () => void) {
  let before: HTMLElement | null = null;
  const outside = () => Array.from(document.body.children).filter((child): child is HTMLElement => child instanceof HTMLElement && child !== sheet && !child.classList.contains("sheet") && child.id !== "toast");

  function close() {
    if (sheet.hidden) return;
    sheet.hidden = true;
    for (const element of outside()) element.inert = false;
    document.removeEventListener("keydown", escape, true);
    before?.focus?.();
    before = null;
    onClose?.();
  }
  function escape(event: KeyboardEvent) {
    if (event.key !== "Escape" || sheet.hidden) return;
    event.preventDefault();
    event.stopPropagation();
    close();
  }
  sheet.addEventListener("click", event => { if (event.target === sheet) close(); });

  return {
    /** Opens the sheet and focuses what it names, else its first control. */
    open(focus?: HTMLElement | null) {
      if (!sheet.hidden) return;
      before = document.activeElement as HTMLElement | null;
      sheet.hidden = false;
      for (const element of outside()) element.inert = true;
      document.addEventListener("keydown", escape, true);
      const first = focus ?? sheet.querySelector<HTMLElement>("button.on, button:not([disabled]), input, select, [tabindex='0']");
      first?.focus();
    },
    close,
    /** Whether the sheet is open. */
    get shown() { return !sheet.hidden; },
  };
}

/** A sheet of the page. */
export type Sheet = ReturnType<typeof createSheet>;
