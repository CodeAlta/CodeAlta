import type * as Monaco from "monaco-editor/editor/editor.api.js";

/**
 * Lets the UI tools write in the editors of the page.
 *
 * An editor takes its text from the browser's own text input (its EditContext), which only real typing feeds. The
 * key events a tool dispatches reach the editor, so Enter, the arrows and the shortcuts work, but they leave no
 * character. The characters of such events are typed here, and one event replaces the whole text of an editor.
 * What the user types never goes through this: it reaches the editor by itself.
 */

/** The event a tool dispatches on an editor to replace its text. Its detail is the new text; a handled event is canceled. */
export const editorFillEvent = "codealta:fill";

type Editor = Monaco.editor.ICodeEditor;

/**
 * Installs the bridge on a document.
 * @returns A function that removes it.
 */
export function installAutomationTyping(monaco: typeof Monaco, target: Document = document): () => void {
  const editorOf = (node: EventTarget | null): Editor | null => {
    if (!(node instanceof Node)) return null;
    const editor = monaco.editor.getEditors().find(candidate => candidate.getDomNode()?.contains(node));
    return editor && editor.getModel() && !editor.getOption(monaco.editor.EditorOption.readOnly) ? editor : null;
  };
  // The character of the key that is down, and the version of the text before the editor saw the key.
  let pending: { editor: Editor; text: string; version: number } | null = null;

  const keyDown = (event: KeyboardEvent) => {
    pending = null;
    if (event.isTrusted || event.ctrlKey || event.metaKey || event.altKey || [...event.key].length !== 1) return;
    const editor = editorOf(event.target);
    if (editor) pending = { editor, text: event.key, version: editor.getModel()!.getAlternativeVersionId() };
  };
  const keyUp = (event: KeyboardEvent) => {
    const typed = pending;
    pending = null;
    if (!typed || event.isTrusted || event.key !== typed.text) return;
    // Whatever wrote the character itself (a tool that feeds the editor's text input) leaves nothing to type.
    if (typed.editor.getModel()?.getAlternativeVersionId() === typed.version) typed.editor.trigger("automation", "type", { text: typed.text });
  };
  const fill = (event: Event) => {
    if (!(event instanceof CustomEvent) || typeof event.detail !== "string") return;
    const editor = editorOf(event.target);
    const model = editor?.getModel();
    if (!editor || !model) return;
    editor.focus();
    editor.pushUndoStop();
    editor.executeEdits("automation", [{ range: model.getFullModelRange(), text: event.detail, forceMoveMarkers: true }]);
    editor.pushUndoStop();
    editor.setPosition(model.getFullModelRange().getEndPosition());
    event.preventDefault();
  };

  target.addEventListener("keydown", keyDown, true);
  target.addEventListener("keyup", keyUp, true);
  target.addEventListener(editorFillEvent, fill, true);
  return () => {
    target.removeEventListener("keydown", keyDown, true);
    target.removeEventListener("keyup", keyUp, true);
    target.removeEventListener(editorFillEvent, fill, true);
  };
}
