import { workspaceEditingSelector } from "./shortcuts";

/** A key pressed in the window, as {@link promptTypedText} reads it. */
export type PromptTypingKey = Readonly<{
  key: string; ctrlKey: boolean; altKey: boolean; metaKey: boolean; isComposing: boolean; keyCode: number;
  defaultPrevented: boolean; repeat: boolean;
}>;

/**
 * What keeps the keys typed while it has the focus: a place where text is written (a field, an editor, a terminal),
 * a widget that answers letters itself (a menu, a list, a tree, a grid, a combo box, a slider), a window over the page,
 * or a frame with its own page.
 */
export const keepsTypedKeysSelector = [workspaceEditingSelector, "[data-terminal-keys]", ".terminal-host", ".xterm", "[data-editor-keys]",
  "[role='textbox']", "[role='searchbox']", "[role='combobox']", "[role='spinbutton']", "[role='slider']",
  "[role='menu']", "[role='menubar']", "[role='listbox']", "[role='tree']", "[role='treegrid']", "[role='grid']",
  "dialog", "[role='dialog']", "[role='alertdialog']", ".bp6-popover", "iframe", "object", "embed"].join(", ");

/**
 * What is open over the page and has the keys while it is: a menu, a popover that is not a tooltip, or the More menu
 * of the composer (a `details` element).
 */
export const typingOverlaySelector = "[role='menu'], .bp6-popover:not(.bp6-tooltip), details.composer-actions-menu[open]";

/**
 * The character a key types into the prompt when the focus is where typing goes nowhere (the page, a button, a row
 * of the Explorer, the timeline), or null when the key is not one to type there. A key that an element used
 * already (the letters of a row of a session, `?` that opens Help), a shortcut (Ctrl, Alt or the Command key),
 * a key being composed by an IME, a held key, a key that is no character (Enter, an arrow, a dead key)
 * and Space (which presses a button and scrolls the timeline) are not; nor is any key typed in what keeps them
 * ({@link keepsTypedKeysSelector}).
 */
export function promptTypedText(key: PromptTypingKey, target: Pick<Element, "closest"> | null): string | null {
  if (key.defaultPrevented || key.isComposing || key.keyCode === 229 || key.repeat) return null;
  if (key.metaKey || key.ctrlKey || key.altKey) return null;
  if (Array.from(key.key).length !== 1 || /[\s\p{Cc}]/u.test(key.key)) return null;
  if (target?.closest(keepsTypedKeysSelector)) return null;
  return key.key;
}

/**
 * The prompt that typing goes to: the one of the session or the new session in front (`#session-prompt`, or
 * `#catalog-prompt`), when it is shown, takes text and is not covered by a banner. Null while a modal window, a
 * menu or a popover is open, or when no such prompt is shown.
 */
export function typingPrompt(page: Document): HTMLElement | null {
  if (page.querySelector('dialog[open], [role="dialog"][aria-modal="true"], [role="alertdialog"][aria-modal="true"]')) return null;
  if (Array.from(page.querySelectorAll(typingOverlaySelector)).some(node => node.getClientRects().length > 0)) return null;
  const prompt = Array.from(page.querySelectorAll<HTMLElement>("#session-prompt, #catalog-prompt"))
    .find(node => node.getClientRects().length > 0 && !node.closest("[inert], [hidden]")
      && page.defaultView?.getComputedStyle(node).visibility === "visible");
  return !prompt || prompt.classList.contains("covered") || prompt.getAttribute("aria-disabled") === "true" ? null : prompt;
}

/**
 * The event a prompt editor answers by taking the keyboard and typing its `detail` (a string) at its caret, as the
 * key would have: it cancels the event when it did.
 */
export const promptTypeEvent = "codealta:prompt-type";

/** Asks the prompt editor `prompt` to take the keyboard and type `text`; true when it did. */
export function typeIntoPrompt(prompt: HTMLElement, text: string): boolean {
  return !prompt.dispatchEvent(new CustomEvent(promptTypeEvent, { detail: text, cancelable: true }));
}

/**
 * Gives a key pressed where typing goes nowhere to the prompt that typing goes to ({@link typingPrompt}): the prompt
 * takes the keyboard and the character the key types. `/` does what it does in an empty prompt: `openCommands` opens
 * the search of the commands. The event is canceled when the key was used; true then.
 */
export function typeKeyIntoPrompt(event: KeyboardEvent, page: Document, openCommands: () => void): boolean {
  const target = event.target instanceof Element ? event.target : null;
  const text = promptTypedText(event, target);
  if (text === null) return false;
  const prompt = typingPrompt(page);
  if (!prompt || (target && prompt.contains(target))) return false;
  if (text === "/" && prompt.hasAttribute("data-empty")) openCommands();
  else if (!typeIntoPrompt(prompt, text)) return false;
  event.preventDefault();
  return true;
}
