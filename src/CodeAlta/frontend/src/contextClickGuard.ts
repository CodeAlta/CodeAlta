/**
 * Decides whether a click belongs to the press that asked for a context menu. On macOS a Ctrl+click is that
 * request, and WebKit still delivers the click (and the double click) of the same press once the page has
 * shown its own menu, which Chromium does not: the row under the pointer would open behind the menu it has
 * just shown. Such a click is not a click of its own.
 */
export function createContextClickGuard() {
  let asked = false;
  return {
    down() { asked = false; },
    contextMenu(button: number, ctrlKey: boolean) { asked = button === 0 && ctrlKey; },
    /** True when the click must not reach the control. A click of the keyboard or of a script has no press (`detail` 0). */
    strayClick(button: number, ctrlKey: boolean, detail: number): boolean { return asked && button === 0 && ctrlKey && detail > 0; },
  };
}

/** Installs the guard ahead of every other mouse handler of the window. */
export function installContextClickGuard(target: Pick<Window, "addEventListener">): void {
  const guard = createContextClickGuard();
  const click = (event: MouseEvent) => {
    if (!guard.strayClick(event.button, event.ctrlKey, event.detail)) return;
    event.stopPropagation();
    event.preventDefault();
  };
  target.addEventListener("mousedown", () => guard.down(), true);
  target.addEventListener("contextmenu", event => guard.contextMenu(event.button, event.ctrlKey), true);
  target.addEventListener("click", click, true);
  target.addEventListener("dblclick", click, true);
}
