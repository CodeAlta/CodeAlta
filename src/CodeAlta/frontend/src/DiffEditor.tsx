import { useLayoutEffect, useRef, type RefObject } from "react";
import { Classes } from "@blueprintjs/core";
import "monaco-editor/features/diffEditor/register.js";
import { followShellTheme, monaco } from "./monacoEnvironment";
import { ensureMonacoLanguage } from "./monacoLanguages";
import type { EditorLanguage } from "./fileLanguage";

/** What a host can ask of the mounted diff editor. */
export type DiffEditorHandle = Readonly<{ focus: () => void;
  /** Moves the caret to the next or previous change and shows it. */
  go: (target: "next" | "previous") => void }>;
/** The changes of the shown file: how many there are, and the one the caret is in or after (0 before the first). */
export type DiffEditorChanges = Readonly<{ count: number; current: number }>;

/**
 * A read-only Monaco diff of two texts with the colors of their language: side by side or in one column,
 * with the unchanged regions folded away. The two texts of a `documentKey` keep their models, so a file that
 * is read again keeps its scroll position.
 */
export function DiffEditor({ documentKey, original, modified, language, label, sideBySide, wrap, ignoreWhitespace, collapseUnchanged, onChanges, handle }: {
  /** Identifies the compared document: another key starts at the first change. */
  documentKey: string; original: string; modified: string; language: EditorLanguage; label: string;
  sideBySide: boolean; wrap: boolean; ignoreWhitespace: boolean; collapseUnchanged: boolean;
  onChanges?: (changes: DiffEditorChanges) => void; handle?: RefObject<DiffEditorHandle | null>;
}) {
  const host = useRef<HTMLDivElement>(null);
  const editor = useRef<monaco.editor.IStandaloneDiffEditor>(null);
  const latest = useRef({ original, modified, onChanges }); latest.current = { original, modified, onChanges };
  const reported = useRef<DiffEditorChanges>({ count: -1, current: -1 });

  useLayoutEffect(() => {
    const node = host.current!;
    const instance = monaco.editor.createDiffEditor(node, { automaticLayout: true, readOnly: true, originalEditable: false,
      fontFamily: getComputedStyle(node).fontFamily, fontSize: 13, lineHeight: 20, minimap: { enabled: false }, scrollBeyondLastLine: false,
      stickyScroll: { enabled: false }, renderOverviewRuler: true, renderIndicators: true, renderMarginRevertIcon: false, renderGutterMenu: false,
      useInlineViewWhenSpaceIsLimited: true, renderSideBySideInlineBreakpoint: 780, diffAlgorithm: "advanced", lineNumbersMinChars: 4,
      glyphMargin: false, folding: false, links: false, padding: { top: 6, bottom: 6 }, renderLineHighlight: "none", scrollbar: { useShadows: false } });
    editor.current = instance;
    if (handle) handle.current = { focus: () => instance.getModifiedEditor().focus(), go: target => { instance.goToDiff(target); report(); } };
    const report = () => {
      const changes = instance.getLineChanges() ?? [];
      const line = instance.getModifiedEditor().getPosition()?.lineNumber ?? 0;
      // A removal has no line of its own in the new text: it sits after the line it names.
      const current = changes.filter(change => (change.modifiedEndLineNumber ? change.modifiedStartLineNumber : change.modifiedStartLineNumber + 1) <= line).length;
      if (reported.current.count === changes.length && reported.current.current === current) return;
      reported.current = { count: changes.length, current };
      latest.current.onChanges?.(reported.current);
    };
    const unfollowTheme = followShellTheme();
    const updated = instance.onDidUpdateDiff(report);
    const moved = instance.getModifiedEditor().onDidChangeCursorPosition(report);
    return () => {
      if (handle) handle.current = null;
      unfollowTheme(); updated.dispose(); moved.dispose();
      const model = instance.getModel();
      instance.dispose(); model?.original.dispose(); model?.modified.dispose(); editor.current = null;
    };
  }, []);

  // One pair of models per document; the previous pair goes with the document it showed.
  useLayoutEffect(() => {
    const instance = editor.current;
    if (!instance) return;
    // A language that is not registered yet starts as plain text and is set once its definition has loaded.
    const known = monaco.languages.getLanguages().some(value => value.id === language) ? language : "plaintext";
    const models = { original: monaco.editor.createModel(latest.current.original, known), modified: monaco.editor.createModel(latest.current.modified, known) };
    const previous = instance.getModel();
    instance.setModel(models);
    previous?.original.dispose(); previous?.modified.dispose();
    reported.current = { count: -1, current: -1 };
    let disposed = false;
    void ensureMonacoLanguage(language).then(registered => {
      if (!registered || disposed || models.original.isDisposed()) return;
      monaco.editor.setModelLanguage(models.original, language); monaco.editor.setModelLanguage(models.modified, language);
    });
    // Once its first comparison is done, a document opens on its first change.
    const first = instance.onDidUpdateDiff(() => { first.dispose(); if (!disposed) instance.revealFirstDiff(); });
    return () => { disposed = true; first.dispose(); };
  }, [documentKey, language]);

  // The same document read again: its text is replaced where it is, and the view stays where it was.
  useLayoutEffect(() => {
    const instance = editor.current, model = instance?.getModel();
    if (!instance || !model || model.original.getValue() === original && model.modified.getValue() === modified) return;
    const view = instance.saveViewState();
    if (model.original.getValue() !== original) model.original.setValue(original);
    if (model.modified.getValue() !== modified) model.modified.setValue(modified);
    if (view) instance.restoreViewState(view);
  }, [original, modified]);

  useLayoutEffect(() => {
    editor.current?.updateOptions({ renderSideBySide: sideBySide, ignoreTrimWhitespace: ignoreWhitespace, wordWrap: wrap ? "on" : "off",
      diffWordWrap: wrap ? "on" : "off",
      hideUnchangedRegions: { enabled: collapseUnchanged, contextLineCount: 3, minimumLineCount: 4, revealLineCount: 20 } });
  }, [sideBySide, wrap, ignoreWhitespace, collapseUnchanged]);

  return <div ref={host} className={`diff-editor ${Classes.MONOSPACE_TEXT}`} role="group" aria-label={label} />;
}
