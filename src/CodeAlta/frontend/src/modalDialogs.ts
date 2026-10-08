const modal = 'dialog[open], [role="dialog"][aria-modal="true"]';

// What was last found in a document, and the observer that says when to look again. A gesture asks several
// times whether a dialog is open, and a page with long timelines holds tens of thousands of elements:
// searching them all each time took a millisecond per question.
const watched = new WeakMap<Document, { open: boolean; current: boolean; changes: MutationObserver }>();

/** Whether an element is in a modal dialog: what it does is then part of that dialog, not of the page under it. */
export const inModalDialog = (element: Element): boolean => !!element.closest(modal);

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
