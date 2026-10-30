import { lazy, Suspense, useRef, useState, type ReactNode, type RefObject } from "react";
import type { EquationDraft } from "./EquationEditor";
import { equationText, mathSpanAt, type MathSpan } from "./markdownMath";
import { monaco } from "./monaco/monacoEnvironment";

// The equation editor and MathLive are loaded the first time an equation is written.
const EquationEditor = lazy(() => import("./EquationEditor"));

/** The key of the equation editor: Word's, Alt+=; on macOS Option+= types ≠, and Ctrl+Alt+= takes its place. */
export const equationKey = typeof navigator !== "undefined" && navigator.platform.startsWith("Mac") ? "Ctrl+Alt+=" : "Alt+=";

/** {@link equationKey} as a keybinding of Monaco. */
export const equationKeybinding = () => (equationKey === "Alt+=" ? 0 : monaco.KeyMod.WinCtrl) | monaco.KeyMod.Alt | monaco.KeyCode.Equal;

// Where an equation of the editor goes: the formula it edits, or the selection it replaces.
type EquationTarget = Readonly<{ draft: EquationDraft; start: number; end: number; span: MathSpan | null }>;

/**
 * The equation editor of a Monaco editor (the prompt, a Markdown file): `open` shows it on the formula at the caret,
 * the one given, or the selection, and its result is written back as one step of undo; `window` is what to render.
 */
export function useEquationEditing(editor: RefObject<monaco.editor.ICodeEditor | null>, canEdit: () => boolean): Readonly<{ open: (span?: MathSpan) => void; window: ReactNode }> {
  const [equation, setEquation] = useState<EquationTarget | null>(null);
  const open = (span?: MathSpan) => {
    const instance = editor.current, model = instance?.getModel(), selection = instance?.getSelection();
    if (!instance || !model || !selection || !canEdit()) return;
    const start = model.getOffsetAt(selection.getStartPosition()), end = model.getOffsetAt(selection.getEndPosition());
    const found = span ?? (start === end ? mathSpanAt(model.getValue(), start) : null);
    setEquation(found ? { draft: { tex: found.tex.trim(), display: found.display }, start: found.start, end: found.end, span: found }
      : { draft: { tex: model.getValueInRange(selection), display: false }, start, end, span: null });
  };
  // One function for the life of the component, for the commands and widgets of the editor that keep it.
  const latest = useRef(open); latest.current = open;
  const stable = useRef((span?: MathSpan) => latest.current(span)).current;
  const close = () => { setEquation(null); requestAnimationFrame(() => editor.current?.focus()); };
  const insert = (draft: EquationDraft) => {
    const instance = editor.current, model = instance?.getModel();
    if (!instance || !model || !equation) { close(); return; }
    const length = model.getValueLength();
    const start = Math.min(equation.start, length), end = Math.min(equation.end, length);
    const text = model.getValue();
    const replacement = equationText(draft, equation.span, text.slice(Math.max(0, start - 1), start), text.slice(end, end + 1));
    // One step of undo, as typing it would be; the caret ends after the equation.
    instance.pushUndoStop();
    instance.executeEdits("equation", [{ range: monaco.Range.fromPositions(model.getPositionAt(start), model.getPositionAt(end)), text: replacement, forceMoveMarkers: true }], inverse => {
      const after = inverse[0].range.getEndPosition();
      return [new monaco.Selection(after.lineNumber, after.column, after.lineNumber, after.column)];
    });
    instance.pushUndoStop();
    close();
  };
  return { open: stable, window: equation && <Suspense fallback={null}>
    <EquationEditor draft={equation.draft} editing={!!equation.span} onInsert={insert} onClose={close} />
  </Suspense> };
}
