type Press = { key: string; target: EventTarget | null };
const activates = (key: string) => key === "Enter" || key === " ";

/**
 * Decides whether a key release may press the control it lands on. Blueprint buttons click on key-up, and a
 * native button does so for Space: when Enter runs a command that opens a window, the release of that same
 * Enter arrives on whatever the new window focused (its Close button) and would close it at once. Only a
 * release on the element that received the key-down counts as a press.
 */
export function createKeyboardClickGuard() {
  let pressed: Press | null = null;
  return {
    down(key: string, target: EventTarget | null) { if (activates(key)) pressed = { key, target }; },
    /** True when the release must not activate `target`. */
    strayRelease(key: string, target: EventTarget | null): boolean {
      if (!activates(key)) return false;
      const origin = pressed;
      pressed = null;
      return !origin || origin.key !== key || origin.target !== target;
    },
  };
}

/** Installs the guard ahead of every other key handler of the window. */
export function installKeyboardClickGuard(target: Pick<Window, "addEventListener">): void {
  const guard = createKeyboardClickGuard();
  target.addEventListener("keydown", event => guard.down(event.key, event.target), true);
  target.addEventListener("keyup", event => {
    if (!guard.strayRelease(event.key, event.target)) return;
    event.stopPropagation();
    event.preventDefault();
  }, true);
}
