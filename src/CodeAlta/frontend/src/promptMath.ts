import { loadMath, loadedMath } from "./math";
import { mathSpanAt, mathSpans, type MathSpan } from "./markdownMath";
import { monaco } from "./monaco/monacoEnvironment";

// A prompt without these characters holds no formula: nothing else is read, and KaTeX is never loaded.
const mayHoldMath = /[$\\]/;

/**
 * Shows the formulas of a prompt as they are written: each is tinted, and the one that holds the caret is drawn by KaTeX
 * in a panel above it (loaded the first time the caret enters a formula), with a button that opens it in the equation
 * editor. Returns what removes it all.
 */
export function attachPromptMath(editor: monaco.editor.IStandaloneCodeEditor,
  options: Readonly<{ editLabel: string; onEdit: (span: MathSpan) => void }>): () => void {
  const document = editor.getContainerDomNode().ownerDocument;
  const node = document.createElement("div");
  node.className = "prompt-math-preview";
  const formula = document.createElement("div");
  formula.className = "prompt-math-preview-formula";
  const edit = document.createElement("button");
  edit.type = "button";
  edit.className = "prompt-math-preview-edit";
  edit.title = options.editLabel;
  edit.setAttribute("aria-label", options.editLabel);
  edit.textContent = "✎";
  node.append(formula, edit);
  let shown: MathSpan | null = null, position: monaco.IPosition | null = null, added = false, disposed = false;
  const widget: monaco.editor.IContentWidget = {
    allowEditorOverflow: true,
    getId: () => "codealta.prompt-math-preview",
    getDomNode: () => node,
    // Above the formula: the prompt is at the bottom of the window.
    getPosition: () => position && { position, preference: [monaco.editor.ContentWidgetPositionPreference.ABOVE, monaco.editor.ContentWidgetPositionPreference.BELOW] },
  };
  const hide = () => {
    shown = null; position = null;
    if (added) { editor.removeContentWidget(widget); added = false; }
  };
  const draw = () => {
    if (disposed) return;
    const model = editor.getModel(), selection = editor.getSelection();
    if (!model || !selection || !selection.isEmpty() || !editor.hasTextFocus()) { hide(); return; }
    const text = model.getValue();
    const span = mayHoldMath.test(text) ? mathSpanAt(text, model.getOffsetAt(selection.getPosition())) : null;
    if (!span || !span.tex.trim()) { hide(); return; }
    const library = loadedMath();
    if (!library) { void loadMath().then(draw, () => { /* Not loaded: no preview. */ }); return; }
    formula.innerHTML = library.formulas().render(span.tex, span.display);
    shown = span;
    position = model.getPositionAt(span.start);
    if (added) editor.layoutContentWidget(widget);
    else { editor.addContentWidget(widget); added = true; }
  };
  const tint = editor.createDecorationsCollection();
  const retint = () => {
    const model = editor.getModel();
    if (disposed || !model) return;
    const text = model.getValue();
    tint.set((mayHoldMath.test(text) ? mathSpans(text) : []).map(span => ({
      range: monaco.Range.fromPositions(model.getPositionAt(span.start), model.getPositionAt(span.end)),
      options: { description: "prompt-math", inlineClassName: "prompt-math" } })));
  };
  let timer: ReturnType<typeof setTimeout> | undefined;
  // The button keeps the focus in the editor: the panel stays until the editor opens.
  edit.addEventListener("pointerdown", event => event.preventDefault());
  edit.addEventListener("click", () => { if (shown) options.onEdit(shown); });
  const listeners = [
    editor.onDidChangeCursorSelection(draw),
    editor.onDidFocusEditorText(draw),
    editor.onDidBlurEditorText(hide),
    editor.onDidChangeModelContent(() => { draw(); clearTimeout(timer); timer = setTimeout(retint, 120); }),
    editor.onDidChangeModel(() => { hide(); retint(); }),
  ];
  retint();
  return () => {
    disposed = true;
    clearTimeout(timer);
    for (const listener of listeners) listener.dispose();
    hide();
    tint.clear();
  };
}
