const modal = 'dialog[open], [role="dialog"][aria-modal="true"]';

// What was last found in a document, and the observer that says when to look again. A gesture asks several
// times whether a dialog is open, and a page with long timelines holds tens of thousands of elements:
// searching them all each time took a millisecond per question.
const watched = new WeakMap<Document, { open: boolean; current: boolean; changes: MutationObserver }>();

/** Whether an element is in a modal dialog: what it does is then part of that dialog, not of the page under it. */
export const inModalDialog = (element: Element): boolean => !!element.closest(modal);

/**
 * What a dialog that a press outside it must not dismiss carries: it asks something, or holds what is being
 * typed, and only an answer or its own buttons close it.
 */
export const keptOnOutsidePress = { "data-outside-press": "keep" } as const;

/**
 * Whether a press is outside what a modal dialog shows: on its backdrop, or, for a dialog that covers the page
 * to hold a movable window, beside that window. A press on anything the dialog shows has another target.
 */
export function outsideDialog(dialog: Element, target: EventTarget | null, x: number, y: number): boolean {
  if (target !== dialog) return false;
  const box = (dialog.querySelector(":scope > .app-window") ?? dialog).getBoundingClientRect();
  return x < box.left || x > box.right || y < box.top || y > box.bottom;
}

/**
 * Lets a press outside a modal dialog dismiss it as Escape does: the dialog gets the `cancel` event it handles
 * for that key, and is closed when it did not prevent the event. The press and its release are both outside, so
 * a selection or a window dragged out of the dialog dismisses nothing. A dialog marked with
 * `keptOnOutsidePress` stays, and so does one with an open menu or popover, which the press closes alone.
 * Returns what removes the listeners.
 */
export function dismissDialogsOnOutsidePress(root: Document): () => void {
  let pressed: Element | null = null;
  const dismissed = (event: MouseEvent) => {
    const dialog = event.target instanceof Element && event.target.matches("dialog[open]") ? event.target : null;
    return dialog && outsideDialog(dialog, event.target, event.clientX, event.clientY) && !dialog.matches('[data-outside-press="keep"]')
      ? dialog as HTMLDialogElement : null;
  };
  const press = (event: MouseEvent) => {
    const dialog = event.button === 0 ? dismissed(event) : null;
    pressed = dialog && !dialog.querySelector(".bp6-overlay-open .bp6-popover:not(.bp6-tooltip)") ? dialog : null;
  };
  const release = (event: MouseEvent) => {
    const dialog = dismissed(event);
    const same = dialog !== null && dialog === pressed;
    pressed = null;
    if (same && dialog.dispatchEvent(new Event("cancel", { cancelable: true }))) dialog.close();
  };
  root.addEventListener("pointerdown", press, true);
  root.addEventListener("click", release, true);
  return () => { root.removeEventListener("pointerdown", press, true); root.removeEventListener("click", release, true); };
}

/**
 * Whether a modal dialog is open in the page: an open `dialog`, or an element that is a dialog and says it is
 * modal. The answer is exact at the time of the call: the document is searched again whenever an element was
 * added or removed, or one of these attributes changed, since the last search.
 */
export function modalDialogOpen(): boolean {
  if (typeof MutationObserver === "undefined") return !!document.querySelector(modal);
  let state = watched.get(document);
  if (!state) {
    const found = { open: false, current: false, changes: new MutationObserver(() => { found.current = false; }) };
    found.changes.observe(document, { subtree: true, childList: true, attributes: true, attributeFilter: ["open", "role", "aria-modal"] });
    watched.set(document, state = found);
  }
  // Changes made in this very task have not reached the observer yet: they are taken here.
  if (state.changes.takeRecords().length > 0) state.current = false;
  if (!state.current) { state.open = !!document.querySelector(modal); state.current = true; }
  return state.open;
}
