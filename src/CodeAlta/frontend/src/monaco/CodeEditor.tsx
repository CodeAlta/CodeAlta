import { useLayoutEffect, useRef, type RefObject } from "react";
import { Classes } from "@blueprintjs/core";
import { followShellTheme, monaco } from "./monacoEnvironment";
import { ensureMonacoLanguage } from "./monacoLanguages";
import type { EditorLanguage } from "./fileLanguage";

/** One diagnostic shown as an error marker; lines and columns are 1-based. */
export type CodeEditorMarker = Readonly<{ line: number; column: number; message: string }>;
/** What a host can ask of the mounted editor. */
export type CodeEditorHandle = Readonly<{ focus: () => void;
  /** Puts the caret at a 1-based position, in the middle of the view, and focuses the editor. */
  reveal: (line: number, column: number) => void }>;

/** A full-size Monaco source editor for configuration and project text, with line numbers and one optional error marker. */
export function CodeEditor({ value, onChange, language, label, readOnly = false, marker = null, wrap = false, onSave, onCursor, handle }: {
  value: string; onChange: (text: string) => void; language: EditorLanguage; label: string;
  readOnly?: boolean; marker?: CodeEditorMarker | null;
  /** Wrap long lines (prose) instead of scrolling horizontally (configuration). */
  wrap?: boolean;
  /** Ctrl+S while the editor has focus. */
  onSave?: () => void;
  /** The caret position, 1-based, whenever it moves. */
  onCursor?: (line: number, column: number) => void;
  handle?: RefObject<CodeEditorHandle | null>;
}) {
  const host = useRef<HTMLDivElement>(null);
  const editor = useRef<monaco.editor.IStandaloneCodeEditor>(null);
  const latest = useRef({ value, onChange, onSave, onCursor }); latest.current = { value, onChange, onSave, onCursor };
  useLayoutEffect(() => {
    const node = host.current!;
    // A language that is not registered yet starts as plain text and is set once its definition has loaded. Naming it
    // now would switch the model the moment the id is registered, before its tokenizer is, and leave it without colors.
    const model = monaco.editor.createModel(latest.current.value,
      monaco.languages.getLanguages().some(known => known.id === language) ? language : "plaintext");
    const instance = monaco.editor.create(node, { model, automaticLayout: true, ariaLabel: label, readOnly,
      fontFamily: getComputedStyle(node).fontFamily, fontSize: 13, lineHeight: 20, minimap: { enabled: false },
      scrollBeyondLastLine: false, wordWrap: wrap ? "on" : "off", renderLineHighlight: "line", stickyScroll: { enabled: false },
      padding: { top: 8, bottom: 8 }, quickSuggestions: false, suggestOnTriggerCharacters: false, links: false, tabSize: 2,
      folding: false, occurrencesHighlight: "off", matchBrackets: "never" });
    editor.current = instance;
    if (handle) handle.current = { focus: () => instance.focus(), reveal: (line, column) => {
      const lineNumber = Math.min(Math.max(line, 1), model.getLineCount());
      const position = { lineNumber, column: Math.min(Math.max(column, 1), model.getLineMaxColumn(lineNumber)) };
      instance.setPosition(position); instance.revealPositionInCenter(position); instance.focus();
    } };
    let disposed = false;
    void ensureMonacoLanguage(language).then(registered => { if (registered && !disposed) monaco.editor.setModelLanguage(model, language); });
    const unfollowTheme = followShellTheme();
    const changed = model.onDidChangeContent(() => {
      const next = model.getValue();
      if (next !== latest.current.value) latest.current.onChange(next);
    });
    const moved = instance.onDidChangeCursorPosition(event => latest.current.onCursor?.(event.position.lineNumber, event.position.column));
    // Handled on this instance: a command binding is shared by every editor on the page.
    const keys = instance.onKeyDown(event => {
      if (!latest.current.onSave || event.keyCode !== monaco.KeyCode.KeyS || !(event.ctrlKey || event.metaKey) || event.altKey || event.shiftKey) return;
      event.preventDefault(); event.stopPropagation();
      latest.current.onSave();
    });
    return () => {
      disposed = true;
      if (handle) handle.current = null;
      unfollowTheme(); changed.dispose(); moved.dispose(); keys.dispose(); instance.dispose(); model.dispose(); editor.current = null;
    };
  }, [language]);
  useLayoutEffect(() => {
    const instance = editor.current;
    if (!instance) return;
    instance.updateOptions({ readOnly, ariaLabel: label, wordWrap: wrap ? "on" : "off" });
    // An external replacement (a reload) resets the text; ordinary typing already matches.
    if (instance.getValue() !== value) instance.getModel()!.setValue(value);
  }, [value, readOnly, label, wrap]);
  useLayoutEffect(() => {
    const model = editor.current?.getModel();
    if (!model) return;
    const line = marker ? Math.min(Math.max(marker.line, 1), model.getLineCount()) : 0;
    monaco.editor.setModelMarkers(model, "codealta", marker ? [{ severity: monaco.MarkerSeverity.Error, message: marker.message,
      startLineNumber: line, startColumn: Math.max(marker.column, 1), endLineNumber: line, endColumn: model.getLineMaxColumn(line) }] : []);
  }, [marker, value]);
  return <div ref={host} className={`code-editor ${Classes.MONOSPACE_TEXT}`} />;
}
