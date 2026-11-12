/** A field of a single line or of several where text is typed: what the menu of a right-click edits. */
export type TextField = HTMLInputElement | HTMLTextAreaElement;

/** What the menu of a text field does. */
export type TextFieldEdit = "cut" | "copy" | "paste" | "selectAll";

/** Which entries of the menu of a field are enabled. */
export type TextFieldEdits = Readonly<Record<TextFieldEdit, boolean>>;

/** What the menu reads and writes on the clipboard: the one of the system in the application. */
export type TextClipboard = Pick<Clipboard, "readText" | "writeText">;

// The inputs whose value is text typed by the user; a checkbox, a button or a color has no menu of text.
const textTypes: ReadonlySet<string> = new Set(["text", "search", "url", "tel", "email", "password", "number"]);

/**
 * The text field a right-click landed on, or null when it is none or not one of the page: an editor of Monaco (the
 * prompt, a file) and a terminal type into a hidden text area of theirs, and edit their own text.
 */
export function textFieldOf(target: EventTarget | null): TextField | null {
  const element = target as (Element & Partial<TextField>) | null;
  if (!element || typeof element.closest !== "function") return null;
  const tag = element.tagName.toLowerCase();
  if (tag !== "textarea" && !(tag === "input" && textTypes.has((element.type ?? "text").toLowerCase()))) return null;
  return element.closest(".monaco-editor, .xterm") ? null : element as TextField;
}

/**
 * Which entries are enabled, as the menu of the system enables them: nothing is changed in a field or an editor that is
 * read-only or disabled, and the text of a password is neither copied nor cut.
 * @param field What the field says of itself: whether its text is hidden (a password), the length of its text, and
 * `writable`, false for a field that is read-only or disabled.
 * @param selected Whether text is selected in the field.
 */
export function textFieldEdits(field: Readonly<{ hidden: boolean; length: number; writable: boolean }>, selected: boolean): TextFieldEdits {
  const shown = selected && !field.hidden;
  return { cut: field.writable && shown, copy: shown, paste: field.writable, selectAll: field.length > 0 };
}

/** Whether the field can be edited: neither read-only nor disabled (also in a disabled fieldset, or as it says to assistive technologies). */
export function fieldIsWritable(field: TextField): boolean {
  return !field.matches(":read-only, [aria-readonly='true'], [aria-disabled='true']");
}

/** Whether the field ignores the pointer and the keyboard: disabled, or in a disabled fieldset. Such a field has no menu. */
export function fieldIsDisabled(field: TextField): boolean {
  return field.matches(":disabled");
}

/** The selection of a field, as offsets of its value; null for an input that has none of its own (an email, a number). */
function selectionOf(field: TextField): { start: number; end: number } | null {
  try {
    const { selectionStart: start, selectionEnd: end } = field;
    return start === null || end === null ? null : { start, end };
  } catch { return null; }
}

/** Whether text is selected in the field, without reading it. */
export function hasSelection(field: TextField): boolean {
  const range = selectionOf(field);
  if (range) return range.start !== range.end;
  return field.ownerDocument.activeElement === field && field.ownerDocument.getSelection()?.isCollapsed === false;
}

/** The text selected in the field. */
export function selectedText(field: TextField): string {
  const range = selectionOf(field);
  if (range) return field.value.slice(range.start, range.end);
  // Chromium and WebKit give the selection of the focused field to the document.
  return field.ownerDocument.activeElement === field ? field.ownerDocument.getSelection()?.toString() ?? "" : "";
}

/**
 * Replaces the selection of the focused field with `text` as typing does: the edit can be undone, the field keeps its
 * maximum length, and the page hears the input (React's change of a controlled field).
 */
function replaceSelection(field: TextField, text: string): void {
  const document = field.ownerDocument;
  if (document.activeElement !== field) field.focus({ preventScroll: true });
  if (document.execCommand(text ? "insertText" : "delete", false, text)) return;
  // A browser that has not the command of the editing of a page.
  const range = selectionOf(field);
  if (!range) return;
  field.setRangeText(text, range.start, range.end, "end");
  field.dispatchEvent(new InputEvent("input", { bubbles: true, inputType: text ? "insertText" : "deleteContent", data: text || null }));
}

/** Whether a right-click on `target` lands on text selected in the page, not beside it. */
export function hasPageSelectionAt(target: EventTarget | null): boolean {
  const node = target as Node | null;
  const selection = node?.ownerDocument?.getSelection();
  if (!node || !selection || selection.isCollapsed) return false;
  for (let index = 0; index < selection.rangeCount; index++) if (selection.getRangeAt(index).intersectsNode(node)) return true;
  return false;
}

/**
 * Copies the text selected in the page as Ctrl+C does, through the copy of the page and what listens to it; with the
 * clipboard when the page has no such command.
 */
export async function copyPageSelection(document: Document, clipboard: TextClipboard): Promise<void> {
  if (document.execCommand("copy")) return;
  await clipboard.writeText(document.getSelection()?.toString() ?? "");
}

/**
 * Does what an entry of the menu asks, in the field, which keeps its focus.
 * @returns Once the clipboard answered; rejects when it cannot be read or written, and nothing then changed.
 */
export async function runTextFieldEdit(field: TextField, edit: TextFieldEdit, clipboard: TextClipboard): Promise<void> {
  if (field.ownerDocument.activeElement !== field) field.focus({ preventScroll: true });
  switch (edit) {
    case "selectAll":
      field.select();
      return;
    case "copy":
      await clipboard.writeText(selectedText(field));
      return;
    case "cut": {
      const text = selectedText(field), range = selectionOf(field);
      if (!text) return;
      await clipboard.writeText(text);
      // What the clipboard holds is removed, even when the selection moved while it was written.
      if (range && field.value.slice(range.start, range.end) === text) field.setSelectionRange(range.start, range.end);
      replaceSelection(field, "");
      return;
    }
    case "paste": {
      const text = await clipboard.readText();
      if (text) replaceSelection(field, text);
      return;
    }
  }
}
