import type * as Monaco from "monaco-editor/editor/editor.api.js";

/**
 * Keeps the text focus with one editor of the page.
 *
 * An editor writes what is typed, and runs the keys, in the first editor that says it has the text focus. An
 * editor says so once its input is the active element, and it learns that it lost the focus from a blur event
 * only. The browser sends none while the window is in the background: an editor that took the focus there (the
 * prompt of a tab that opened, a file that an agent opened) goes on claiming it once another editor has it, and
 * what is typed in the second one is written in the first. When an editor takes the text focus, the ones that
 * still claim it without having the keyboard are told that they lost it.
 */

/** Installs the guard on the editors created from now on. */
export function installEditorFocus(monaco: typeof Monaco): void {
  // The element of each editor that had the keyboard when the editor took the focus: the one that hears a blur.
  const inputs = new WeakMap<Monaco.editor.ICodeEditor, Element>();
  monaco.editor.onDidCreateEditor(editor => {
    editor.onDidFocusEditorText(() => {
      const node = editor.getDomNode();
      const active = node?.ownerDocument.activeElement;
      if (node && active && node.contains(active)) inputs.set(editor, active);
      for (const other of monaco.editor.getEditors()) {
        if (other === editor || !other.hasTextFocus()) continue;
        const input = inputs.get(other);
        if (input && input !== active) input.dispatchEvent(new FocusEvent("blur"));
      }
    });
  });
}
