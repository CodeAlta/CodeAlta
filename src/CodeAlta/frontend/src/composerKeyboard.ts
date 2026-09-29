import type { ShortcutKey } from "./shortcuts";

type ComposerKey = ShortcutKey & { isComposing?: boolean; keyCode?: number; repeat?: boolean; defaultPrevented?: boolean };

/** Expanded editing never submits: Escape/Ctrl+Enter close; Enter inserts a newline. */
export function dispatchExpandedComposerKey(event: ComposerKey, close: () => void): boolean {
  if (event.defaultPrevented || event.isComposing || event.keyCode === 229 || event.repeat
    || event.shiftKey || event.altKey || event.metaKey) return false;
  if (!(event.key === "Enter" && event.ctrlKey) && !(event.key === "Escape" && !event.ctrlKey)) return false;
  close();
  return true;
}

/** Dispatch only deliberate composer actions; IME confirmation and newline gestures remain editor input. */
export function dispatchComposerKey(event: ComposerKey, send: () => void, steer: () => void): boolean {
  if (event.defaultPrevented || event.isComposing || event.keyCode === 229 || event.repeat
    || event.key !== "Enter" || event.shiftKey || event.altKey || event.metaKey) return false;
  if (event.ctrlKey) steer();
  else send();
  return true;
}

/** Only actual single-character keys at the empty regular prompt open shell navigation; pasted or composed text remains a draft. */
export function dispatchTransientComposerKey(event: ComposerKey, input: Pick<HTMLTextAreaElement, "value" | "selectionStart" | "selectionEnd">,
  openHelp?: () => void, openPalette?: () => void): boolean {
  if (event.defaultPrevented || event.repeat || event.isComposing || event.keyCode === 229 ||
    event.ctrlKey || event.metaKey || event.altKey || input.value.length !== 0 ||
    input.selectionStart !== 0 || input.selectionEnd !== 0) return false;
  if (event.key === "?" && openHelp) openHelp();
  else if (event.key === "/" && openPalette) openPalette();
  else return false;
  return true;
}
