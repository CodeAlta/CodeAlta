type Listening = Pick<Document, "addEventListener" | "removeEventListener">;

/** How long after its key went up the request for a context menu is still the one of that key. */
export const keyContextMenuDelay = 300;

/**
 * Takes the request for a context menu that follows a key which already opened a menu of the page. The page reads
 * the menu key and Shift+F10 itself, on every system; Windows and Linux also make that request of the same key press
 * (Windows a moment after the key went up), and macOS never does. Left alone, the request opens the menu a second
 * time, or the menu of the browser over it: it is taken wherever it lands, since the focus may be in the menu by then.
 * @returns What stops waiting for the request.
 */
export function takeKeyContextMenu(target: Listening = document): () => void {
  let timer: ReturnType<typeof setTimeout> | undefined;
  const end = () => {
    clearTimeout(timer);
    target.removeEventListener("contextmenu", take, true);
    target.removeEventListener("keyup", released, true);
    target.removeEventListener("pointerdown", end, true);
  };
  const take = (event: Event) => { event.preventDefault(); event.stopPropagation(); end(); };
  // A request that comes later than that is another one.
  const released = () => { clearTimeout(timer); timer = setTimeout(end, keyContextMenuDelay); };
  target.addEventListener("contextmenu", take, true);
  target.addEventListener("keyup", released, true);
  // A press of the pointer asks for a menu of its own.
  target.addEventListener("pointerdown", end, true);
  return end;
}
