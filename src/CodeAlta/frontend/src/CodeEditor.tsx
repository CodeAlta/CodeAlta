import { useLayoutEffect, useRef } from "react";
import { Classes } from "@blueprintjs/core";
import "monaco-editor/languages/definitions/ini/register.js";
import { followShellTheme, monaco } from "./monacoEnvironment";

/** One diagnostic shown as an error marker; lines and columns are 1-based. */
export type CodeEditorMarker = Readonly<{ line: number; column: number; message: string }>;

/** A full-size Monaco source editor for configuration text, with line numbers and one optional error marker. */
export function CodeEditor({ value, onChange, language, label, readOnly = false, marker = null, wrap = false }: {
  value: string; onChange: (text: string) => void; language: "ini" | "markdown" | "plaintext"; label: string;
  readOnly?: boolean; marker?: CodeEditorMarker | null;
  /** Wrap long lines (prose) instead of scrolling horizontally (configuration). */
  wrap?: boolean;
}) {
  const host = useRef<HTMLDivElement>(null);
  const editor = useRef<monaco.editor.IStandaloneCodeEditor>(null);
  const latest = useRef({ value, onChange }); latest.current = { value, onChange };
  useLayoutEffect(() => {
    const node = host.current!;
    const model = monaco.editor.createModel(latest.current.value, language);
    const instance = monaco.editor.create(node, { model, automaticLayout: true, ariaLabel: label, readOnly,
      fontFamily: getComputedStyle(node).fontFamily, fontSize: 13, lineHeight: 20, minimap: { enabled: false },
      scrollBeyondLastLine: false, wordWrap: wrap ? "on" : "off", renderLineHighlight: "line", stickyScroll: { enabled: false },
      padding: { top: 8, bottom: 8 }, quickSuggestions: false, suggestOnTriggerCharacters: false, links: false, tabSize: 2 });
    editor.current = instance;
    const unfollowTheme = followShellTheme();
    const changed = model.onDidChangeContent(() => {
      const next = model.getValue();
      if (next !== latest.current.value) latest.current.onChange(next);
    });
    return () => { unfollowTheme(); changed.dispose(); instance.dispose(); model.dispose(); editor.current = null; };
  }, [language]);
  useLayoutEffect(() => {
    const instance = editor.current;
    if (!instance) return;
    instance.updateOptions({ readOnly, ariaLabel: label });
    // An external replacement (a reload) resets the text; ordinary typing already matches.
    if (instance.getValue() !== value) instance.getModel()!.setValue(value);
  }, [value, readOnly, label]);
  useLayoutEffect(() => {
    const model = editor.current?.getModel();
    if (!model) return;
    const line = marker ? Math.min(Math.max(marker.line, 1), model.getLineCount()) : 0;
    monaco.editor.setModelMarkers(model, "codealta", marker ? [{ severity: monaco.MarkerSeverity.Error, message: marker.message,
      startLineNumber: line, startColumn: Math.max(marker.column, 1), endLineNumber: line, endColumn: model.getLineMaxColumn(line) }] : []);
  }, [marker, value]);
  return <div ref={host} className={`code-editor ${Classes.MONOSPACE_TEXT}`} />;
}
