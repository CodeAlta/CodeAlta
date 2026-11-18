// The dock hides the pane of a tab that is not in front with `display: none`, and showing it again builds the style and
// the layout of everything in it: with the transcript of a long session (20,000 elements), 150 ms or more before the
// first frame, and a view that jumps because the heights of its rows are estimated again. The panes of the sessions
// looked at last are kept laid out instead, out of sight: the stylesheet moves a kept pane out of the dock while the
// dock hides it, which changes the style of one element. Showing it again is then a few milliseconds, where it was.
//
// A pane out of sight must cost nothing and must not be reachable:
// - its size is held, so a change of the size of the dock lays nothing out in it;
// - its animations are paused and it is hidden from assistive technology, a moment after it is hidden so that going
//   back and forth between two tabs pays for neither;
// - what has the keyboard in it gives it up, as does the text selected in it, and the keyboard cannot go into it
//   (`leaveHiddenPane`).
// Only a few panes are kept (`keptPaneLimit`): the others are hidden as the dock hides them, and cost nothing at all.

/** The hidden panes kept laid out, beside the ones shown: the sessions gone back to most often are the last ones left. */
export const keptPaneLimit = 3;

/** The time a pane stays as it was once hidden, before what makes it cost nothing is done. */
const settleDelay = 1000;

const paneSelector = ".flexlayout__tab";

function paneOf(element: Element): HTMLElement | null {
  return element.closest<HTMLElement>(paneSelector);
}

/**
 * Whether an element is in the pane of a tab the dock hides. A kept pane is laid out while hidden, so what looks for the
 * pane on screen cannot tell by the layout alone (`offsetParent`, `getClientRects`).
 */
export function inHiddenPane(element: Element): boolean {
  return paneOf(element)?.style.display === "none";
}

type Kept = { id: string; content: HTMLElement; pane: HTMLElement; settled: boolean; paused: Animation[] };

function thaw(entry: Kept) {
  entry.content.style.width = entry.content.style.height = "";
  if (!entry.settled) return;
  entry.settled = false;
  entry.pane.removeAttribute("aria-hidden");
  for (const animation of entry.paused) {
    // An animation whose element is gone, or that ended meanwhile, has nothing to resume.
    try { if (animation.playState === "paused") animation.play(); } catch { /* It was cancelled. */ }
  }
  entry.paused = [];
}

/** Keeps the panes of the sessions hidden last laid out, and makes them cost nothing while they are hidden. */
export function createKeptPanes(limit = keptPaneLimit, delay = settleDelay) {
  // The hidden panes that are kept, the one hidden last first.
  let hidden: Kept[] = [];
  let timer: ReturnType<typeof setTimeout> | undefined;

  function settle() {
    timer = undefined;
    for (const entry of hidden.splice(limit)) {
      thaw(entry);
      // Hidden as the dock hides it from now on.
      delete entry.pane.dataset.keptPane;
    }
    for (const entry of hidden) {
      if (entry.settled || !entry.pane.isConnected) continue;
      entry.settled = true;
      entry.pane.setAttribute("aria-hidden", "true");
      entry.paused = typeof entry.pane.getAnimations === "function"
        ? entry.pane.getAnimations({ subtree: true }).filter(animation => animation.playState === "running") : [];
      for (const animation of entry.paused) animation.pause();
    }
  }

  function forget(id: string): Kept | undefined {
    const entry = hidden.find(value => value.id === id);
    if (entry) hidden = hidden.filter(value => value !== entry);
    return entry;
  }

  return {
    /** The content of a session tab is shown: its pane is kept laid out when the dock hides it. */
    shown(id: string, content: HTMLElement) {
      const entry = forget(id);
      if (entry) thaw(entry);
      const pane = paneOf(content);
      if (pane) pane.dataset.keptPane = "";
    },
    /** The dock hid the content of a session tab. A pane that was never shown is not kept: it holds nothing yet. */
    hidden(id: string, content: HTMLElement) {
      const pane = paneOf(content);
      if (!pane || pane.dataset.keptPane === undefined || hidden.some(entry => entry.id === id)) return;
      // What is typed must not go to a prompt nobody sees.
      const focused = content.ownerDocument.activeElement as HTMLElement | null;
      if (focused && pane.contains(focused)) focused.blur?.();
      // Nor does a copy take the text that was selected in it.
      const selection = content.ownerDocument.getSelection?.();
      if (selection?.anchorNode && pane.contains(selection.anchorNode)) selection.removeAllRanges();
      const width = content.offsetWidth, height = content.offsetHeight;
      if (width > 0 && height > 0) { content.style.width = `${width}px`; content.style.height = `${height}px`; }
      hidden.unshift({ id, content, pane, settled: false, paused: [] });
      clearTimeout(timer);
      timer = setTimeout(settle, delay);
    },
    /** The tab is closed, or moved to another window. */
    gone(id: string) { forget(id); },
    /** Nothing is kept any more: the dock is going away. */
    dispose() { clearTimeout(timer); timer = undefined; hidden = []; },
    /** For tests: what makes the hidden panes cost nothing, without waiting for it. */
    settle() { clearTimeout(timer); settle(); },
  };
}

export type KeptPanes = ReturnType<typeof createKeptPanes>;

const tabbable = 'a[href], button:not(:disabled), input:not(:disabled):not([type="hidden"]), select:not(:disabled), textarea:not(:disabled), summary, iframe, [contenteditable]:not([contenteditable="false"]), [tabindex]';

function reachable(element: HTMLElement): boolean {
  return element.tabIndex >= 0 && !inHiddenPane(element) && element.getClientRects().length > 0 && !element.closest("[inert]");
}

/**
 * The keyboard went into a pane that is hidden, where nothing can be seen: it goes on to what follows the pane when the
 * Tab key took it there (`tabbing`, with `backward` for Shift+Tab), as it would past a pane that is not laid out, and
 * back to what had it otherwise (code that gives the keyboard to a control of its pane without knowing the pane is hidden).
 * Returns whether the keyboard was in a hidden pane.
 */
export function leaveHiddenPane(target: Element, previous: Element | null, tabbing: boolean, backward = false): boolean {
  const pane = paneOf(target);
  if (!pane || pane.style.display !== "none" || !(target instanceof HTMLElement)) return false;
  if (!tabbing) {
    if (previous instanceof HTMLElement && previous.isConnected && !inHiddenPane(previous)) previous.focus({ preventScroll: true });
    else target.blur();
    return true;
  }
  const candidates = Array.from(target.ownerDocument.querySelectorAll<HTMLElement>(tabbable)).filter(reachable);
  const before = candidates.filter(element => !!(pane.compareDocumentPosition(element) & Node.DOCUMENT_POSITION_PRECEDING));
  const next = backward ? before[before.length - 1]
    : candidates.find(element => !!(pane.compareDocumentPosition(element) & Node.DOCUMENT_POSITION_FOLLOWING));
  // At the end of the page the keyboard starts again from its beginning, as the Tab key does.
  const wrapped = next ?? (backward ? candidates[candidates.length - 1] : candidates[0]);
  if (wrapped) wrapped.focus();
  else target.blur();
  return true;
}
