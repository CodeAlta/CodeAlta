import { useImperativeHandle, useLayoutEffect, useRef, type ClipboardEventHandler, type KeyboardEventHandler, type Ref } from "react";
import "monaco-editor/languages/definitions/markdown/register.js";
import { promptEditorMinimum } from "./composerHeight";
import { followPageFont, followShellTheme, monaco, pageFontFamily } from "./monaco/monacoEnvironment";
import { spokenInsertion } from "./speech/spokenText";


export type PromptInput = Pick<HTMLTextAreaElement, "value" | "disabled" | "selectionStart" | "selectionEnd"
  | "isConnected" | "focus" | "contains" | "closest" | "setSelectionRange" | "addEventListener" | "removeEventListener"> & {
  /**
   * Writes a spoken piece at the caret, in place of the selection, as one step of undo; the caret ends after
   * it. It is separated by a space from the word it follows. False when the editor took nothing.
   */
  insertAtCaret: (text: string) => boolean;
  /** Shows a text greyed at the caret that is no part of the prompt, of its draft or of undo; an empty text removes it. */
  setInterimText: (text: string) => void;
};

export function PromptEditor({ ref, value, onChange, disabled = false, expanded = false, id, label, placeholder,
  suggested = false, onPaste, onKeyDown, onCompositionStart, covered = false }: {
  ref?: Ref<PromptInput>; value: string; onChange: (text: string) => void; disabled?: boolean; expanded?: boolean;
  id?: string; label: string; placeholder?: string; onPaste?: ClipboardEventHandler<HTMLElement>;
  /** Whether the placeholder is a prompt suggested to the user, which a key takes, and not the usual hint. */
  suggested?: boolean;
  onKeyDown?: KeyboardEventHandler<HTMLDivElement>; onCompositionStart?: () => void;
  /** Something is shown in its place: the editor is hidden, its text and its size kept. */
  covered?: boolean;
}) {
  const host = useRef<HTMLDivElement>(null);
  const editor = useRef<monaco.editor.IStandaloneCodeEditor>(null);
  const latest = useRef({ value, onChange, disabled }); latest.current = { value, onChange, disabled };
  // What a dictation is hearing: a decoration at the caret, which the text of the model never holds.
  const interim = useRef<{ text: string; shown: monaco.editor.IEditorDecorationsCollection | null }>({ text: "", shown: null });
  // The character before the caret, a line break at the start of a line, nothing at the start of the prompt.
  function beforeCaret(model: monaco.editor.ITextModel, position: monaco.IPosition) {
    return position.column > 1 ? model.getValueInRange(new monaco.Range(position.lineNumber, position.column - 1, position.lineNumber, position.column))
      : position.lineNumber > 1 ? "\n" : "";
  }
  function showInterim() {
    const instance = editor.current, model = instance?.getModel(), node = host.current;
    if (!instance || !model || !node) return;
    const position = instance.getSelection()?.getStartPosition() ?? model.getFullModelRange().getEndPosition();
    const content = interim.current.text ? spokenInsertion(beforeCaret(model, position), interim.current.text) : "";
    // The placeholder of an empty prompt gives its place to what is being said.
    node.toggleAttribute("data-interim", !!content);
    const decorations = content ? [{ range: monaco.Range.fromPositions(position), options: { description: "speech-interim", showIfCollapsed: true,
      after: { content, inlineClassName: "speech-interim", cursorStops: monaco.editor.InjectedTextCursorStops.None } } }] : [];
    if (interim.current.shown) interim.current.shown.set(decorations);
    else if (decorations.length) interim.current.shown = instance.createDecorationsCollection(decorations);
  }
  useLayoutEffect(() => {
    const node = host.current!;
    const model = monaco.editor.createModel(latest.current.value, "markdown");
    const instance = monaco.editor.create(node.firstElementChild as HTMLElement, { model, automaticLayout: true, ariaLabel: label,
      readOnly: latest.current.disabled, fontFamily: pageFontFamily("prompt"), fontSize: 13, lineHeight: 20,
      minimap: { enabled: false }, lineNumbers: "off", glyphMargin: false, folding: false,
      lineDecorationsWidth: 0, lineNumbersMinChars: 0, scrollBeyondLastLine: false, wordWrap: "on",
      overviewRulerLanes: 0, renderLineHighlight: "none", stickyScroll: { enabled: false },
      padding: { top: 12, bottom: 12 }, scrollbar: { alwaysConsumeMouseWheel: false },
      quickSuggestions: false, suggestOnTriggerCharacters: false, tabFocusMode: true, links: false, occurrencesHighlight: "off", matchBrackets: "never" });
    editor.current = instance;
    const unfollowTheme = followShellTheme(), unfollowFont = followPageFont(instance, "prompt");

    const size = () => { if (!expanded) node.style.height = `${Math.max(promptEditorMinimum, Math.min(248, instance.getContentHeight()))}px`; };
    const resized = instance.onDidContentSizeChange(size); size();
    let updating = false;
    const changed = model.onDidChangeContent(() => {
      if (updating) return;
      const next = model.getValue();
      if (next.length > 32768) {
        updating = true; instance.trigger("prompt", "undo", null); updating = false; return;
      }
      if (next !== latest.current.value) latest.current.onChange(next);
      node.dispatchEvent(new Event("input", { bubbles: true }));
    });
    const selection = instance.onDidChangeCursorSelection(() => {
      node.dispatchEvent(new Event("select"));
      // What is being said follows the caret.
      if (interim.current.text) showInterim();
    });
    return () => { unfollowTheme(); unfollowFont(); changed.dispose(); selection.dispose(); resized.dispose(); instance.dispose(); model.dispose(); editor.current = null;
      interim.current = { text: "", shown: null }; node.removeAttribute("data-interim"); };
  }, [expanded]);
  useImperativeHandle(ref, () => ({
    get value() { return editor.current?.getValue() ?? latest.current.value; },
    get disabled() { return latest.current.disabled; },
    get isConnected() { return host.current?.isConnected ?? false; },
    get selectionStart() { const e = editor.current; return e?.getModel()?.getOffsetAt(e.getSelection()!.getStartPosition()) ?? 0; },
    get selectionEnd() { const e = editor.current; return e?.getModel()?.getOffsetAt(e.getSelection()!.getEndPosition()) ?? 0; },
    focus: () => editor.current?.focus(), contains: node => host.current?.contains(node) ?? false,
    closest: ((selector: string) => host.current?.closest(selector) ?? null) as PromptInput["closest"],
    setSelectionRange: (start, end) => { const model = editor.current?.getModel(); if (!model) return;
      const a = model.getPositionAt(start ?? 0), b = model.getPositionAt(end ?? 0);
      editor.current?.setSelection(new monaco.Selection(a.lineNumber, a.column, b.lineNumber, b.column)); },
    addEventListener: (...args: Parameters<HTMLElement["addEventListener"]>) => host.current?.addEventListener(...args),
    removeEventListener: (...args: Parameters<HTMLElement["removeEventListener"]>) => host.current?.removeEventListener(...args),
    insertAtCaret: text => {
      const instance = editor.current, model = instance?.getModel();
      if (!instance || !model || latest.current.disabled) return false;
      const range = instance.getSelection() ?? monaco.Range.fromPositions(model.getFullModelRange().getEndPosition());
      const insertion = spokenInsertion(beforeCaret(model, range.getStartPosition()), text);
      if (!insertion) return false;
      // The same path as typing: the change of the model tells the owner of the draft, and a text that is too long is undone.
      instance.pushUndoStop();
      const done = instance.executeEdits("dictation", [{ range, text: insertion, forceMoveMarkers: true }], inverse => {
        const end = inverse[0].range.getEndPosition();
        return [new monaco.Selection(end.lineNumber, end.column, end.lineNumber, end.column)];
      });
      instance.pushUndoStop();
      const caret = instance.getPosition();
      if (caret) instance.revealPosition(caret);
      return done;
    },
    setInterimText: text => { interim.current.text = text; showInterim(); },
  }), []);
  useLayoutEffect(() => {
    const instance = editor.current;
    if (!instance) return;
    instance.updateOptions({ readOnly: disabled, ariaLabel: label });
    if (instance.getValue() !== value) {
      const model = instance.getModel()!;
      instance.executeEdits("draft", [{ range: model.getFullModelRange(), text: value }]);
    }
  }, [value, disabled, label]);
  return <div id={id} ref={host} className={`prompt-editor${expanded ? " expanded" : ""}${covered ? " covered" : ""}`}
    tabIndex={-1} onFocus={event => { if (event.target === event.currentTarget) editor.current?.focus(); }}
    onKeyDownCapture={onKeyDown} onPasteCapture={event => { onPaste?.(event); if (event.defaultPrevented) event.stopPropagation(); }} onCompositionStart={onCompositionStart}
    aria-label={label} data-placeholder={!value ? placeholder : undefined} data-suggested={!value && placeholder && suggested ? "" : undefined}
    // The placeholder is drawn by a style: a suggested prompt is also said to who does not see it.
    aria-description={!value && placeholder && suggested ? placeholder : undefined}><div className="prompt-editor-surface" /></div>;
}
