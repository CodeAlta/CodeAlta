import type { EditTarget } from "../EditMenu";
import { textFieldEdits } from "../textFieldEdits";
import { monaco } from "./monacoEnvironment";

const source = "edit-menu";

/**
 * The editor of Monaco a request for a menu lands in (the prompt, a file, a diff, the text of a prompt of an agent), as
 * the target of the menu of the text fields of the page: the editors have no menu of their own, so that every text of
 * the application has the same one. Null when the request is not in an editor.
 */
export function monacoEditTarget(target: EventTarget | null, point: Readonly<{ x: number; y: number }> | null): EditTarget | null {
  const node = target instanceof Element ? target.closest<HTMLElement>(".monaco-editor") : null;
  if (!node) return null;
  const editor = monaco.editor.getEditors().find(candidate => candidate.getDomNode() === node);
  const model = editor?.getModel();
  if (!editor || !model) return null;
  editor.focus();
  // A right-click beside the selection moves the caret where it was pressed, as in a text field.
  const position = point ? editor.getTargetAtClientPoint(point.x, point.y)?.position : null;
  if (position && !(editor.getSelections() ?? []).some(selection => selection.containsPosition(position))) editor.setPosition(position);
  // Read when an entry copies it: a whole file may be selected.
  const selected = () => (editor.getSelections() ?? []).map(selection => model.getValueInRange(selection)).filter(text => text).join(model.getEOL());
  return {
    edits: textFieldEdits({ hidden: false, length: model.getValueLength(), writable: !editor.getOption(monaco.editor.EditorOption.readOnly) },
      (editor.getSelections() ?? []).some(selection => !selection.isEmpty())),
    element: node,
    keyboardPoint: () => {
      const box = node.getBoundingClientRect(), caret = editor.getScrolledVisiblePosition(editor.getPosition() ?? { lineNumber: 1, column: 1 });
      return caret ? { x: box.left + caret.left, y: box.top + caret.top + caret.height } : { x: box.left, y: box.top };
    },
    run: async (edit, clipboard) => {
      switch (edit) {
        case "selectAll": editor.setSelection(model.getFullModelRange()); return;
        case "copy": await clipboard.writeText(selected()); return;
        // Monaco's own edits: they can be undone, and act on every caret.
        case "cut": { const text = selected(); if (!text) return; await clipboard.writeText(text); editor.trigger(source, "cut", null); return; }
        case "paste": { const text = await clipboard.readText(); if (text) editor.trigger(source, "paste", { text, pasteOnNewLine: false, multicursorText: null, mode: null }); return; }
      }
    },
    onBlur: close => { const listening = editor.onDidBlurEditorText(close); return () => listening.dispose(); },
  };
}
