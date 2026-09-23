import type { ShortcutKey } from "./shortcuts";

type ComposerKey = ShortcutKey & { isComposing?: boolean; keyCode?: number; repeat?: boolean; defaultPrevented?: boolean };

/** Dispatch only deliberate composer actions; IME confirmation and newline gestures remain editor input. */
export function dispatchComposerKey(event: ComposerKey, send: () => void, steer: () => void): boolean {
  if (event.defaultPrevented || event.isComposing || event.keyCode === 229 || event.repeat
    || event.key !== "Enter" || event.shiftKey || event.altKey || event.metaKey) return false;
  if (event.ctrlKey) steer();
  else send();
  return true;
}
