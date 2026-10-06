import { useLayoutEffect, useRef, type RefObject } from "react";
import { Classes } from "@blueprintjs/core";
import { followShellTheme, monaco } from "../monaco/monacoEnvironment";
import { ensureMonacoLanguage } from "../monaco/monacoLanguages";
import { fileLanguage, type EditorLanguage } from "../monaco/fileLanguage";

/** A text document the surface holds a model for: `text` is what was read from the disk, `generation` when it was read. */
export type SurfaceDocument = Readonly<{ id: number; path: string; text: string; generation: number }>;
export type SurfaceCursor = Readonly<{ line: number; column: number; selected: number }>;
/** What the status bar says of the document shown. */
export type SurfaceInfo = Readonly<{ language: string; eol: "LF" | "CRLF"; spaces: boolean; tabSize: number }>;
export type EditorSurfaceHandle = Readonly<{
  focus: () => void;
  /** Puts the caret at a 1-based position of the document shown, selecting `length` characters, and focuses the editor. */
  reveal: (line: number, column: number, length?: number) => void;
  /** The text of a document as it is now, and the version to name once the disk holds that text. */
  read: (id: number) => Readonly<{ text: string; version: number }> | null;
  /** The disk holds the text that had this version: the document is unsaved only if it changed since. */
  saved: (id: number, version: number) => void;
  /** Runs an action of the editor by its id ("editor.action.gotoLine", "actions.find"…). */
  run: (action: string) => void;
  /** The selected text when it is on one line, for a search to start from. */
  selection: () => string;
}>;

type Entry = { model: monaco.editor.ITextModel; generation: number; saved: number; dirty: boolean; language: EditorLanguage | null; applying: boolean;
  view: monaco.editor.ICodeEditorViewState | null; changed: monaco.IDisposable };

const registered = (language: string) => monaco.languages.getLanguages().some(known => known.id === language);

// Replaces what a model holds by what the disk holds now, touching only the part that differs: the caret and the
// scroll position stay where they are, and one undo brings the previous text back.
function replaceText(model: monaco.editor.ITextModel, text: string) {
  const eol = text.includes("\r\n") ? "\r\n" : "\n";
  if (model.getEOL() !== eol && /[\r\n]/u.test(text)) model.setEOL(eol === "\r\n" ? monaco.editor.EndOfLineSequence.CRLF : monaco.editor.EndOfLineSequence.LF);
  const next = text.replace(/\r\n|\r|\n/gu, model.getEOL()), old = model.getValue();
  if (old === next) return;
  let start = 0;
  const shortest = Math.min(old.length, next.length);
  while (start < shortest && old.charCodeAt(start) === next.charCodeAt(start)) start++;
  let oldEnd = old.length, nextEnd = next.length;
  while (oldEnd > start && nextEnd > start && old.charCodeAt(oldEnd - 1) === next.charCodeAt(nextEnd - 1)) { oldEnd--; nextEnd--; }
  model.pushStackElement();
  model.pushEditOperations([], [{ range: monaco.Range.fromPositions(model.getPositionAt(start), model.getPositionAt(oldEnd)), text: next.slice(start, nextEnd) }], () => null);
  model.pushStackElement();
}

/**
 * The text editor of the project code editor: one Monaco editor that shows one of several documents. Each
 * document keeps its own text, undo history, caret and scroll position while it is open, and is unsaved while
 * its text is not the one last read from or written to the disk.
 */
export function EditorSurface({ documents, active, readOnly, wrap, minimap, label, hidden, handle, onDirty, onCursor, onInfo }: {
  documents: readonly SurfaceDocument[];
  /** The document shown, by id; null shows none. */
  active: number | null;
  readOnly: boolean; wrap: boolean; minimap: boolean; label: string;
  /**
   * Something else is shown over it (a preview, a notice). The editor keeps its place and its size underneath, so
   * that a document shown again, or gone to a line as it opens, is laid out for the pane it is in.
   */
  hidden: boolean;
  handle: RefObject<EditorSurfaceHandle | null>;
  onDirty: (id: number, dirty: boolean) => void;
  onCursor: (cursor: SurfaceCursor) => void;
  onInfo: (info: SurfaceInfo | null) => void;
}) {
  const host = useRef<HTMLDivElement>(null);
  const editor = useRef<monaco.editor.IStandaloneCodeEditor | null>(null);
  const models = useRef(new Map<number, Entry>());
  const shown = useRef<number | null>(null);
  const caret = useRef(() => { });
  const latest = useRef({ onDirty, onCursor, onInfo }); latest.current = { onDirty, onCursor, onInfo };

  useLayoutEffect(() => {
    const node = host.current!;
    const instance = monaco.editor.create(node, { model: null, automaticLayout: true, ariaLabel: label, readOnly,
      fontFamily: getComputedStyle(node).fontFamily, fontSize: 13, lineHeight: 20, minimap: { enabled: minimap, renderCharacters: false, maxColumn: 100 },
      lineNumbers: "on", lineNumbersMinChars: 4, scrollBeyondLastLine: false, wordWrap: wrap ? "on" : "off", renderLineHighlight: "line", stickyScroll: { enabled: false },
      padding: { top: 8, bottom: 8 }, quickSuggestions: false, suggestOnTriggerCharacters: false, links: true,
      folding: true, showFoldingControls: "mouseover", matchBrackets: "always", occurrencesHighlight: "singleFile", selectionHighlight: true,
      bracketPairColorization: { enabled: true }, guides: { indentation: true, highlightActiveIndentation: true, bracketPairs: false }, renderWhitespace: "selection",
      smoothScrolling: true, fixedOverflowWidgets: true, unicodeHighlight: { ambiguousCharacters: false },
      find: { addExtraSpaceOnTop: false, seedSearchStringFromSelection: "selection" }, scrollbar: { useShadows: false } });
    editor.current = instance;
    const unfollowTheme = followShellTheme();
    const describe = () => {
      const model = instance.getModel();
      if (!model) { latest.current.onInfo(null); return; }
      const id = model.getLanguageId(), options = model.getOptions();
      latest.current.onInfo({ language: monaco.languages.getLanguages().find(known => known.id === id)?.aliases?.[0] ?? id,
        eol: model.getEOL() === "\r\n" ? "CRLF" : "LF", spaces: options.insertSpaces, tabSize: options.tabSize });
    };
    const place = () => {
      const model = instance.getModel(), selection = instance.getSelection();
      if (model && selection) latest.current.onCursor({ line: selection.positionLineNumber, column: selection.positionColumn,
        selected: selection.isEmpty() ? 0 : model.getValueLengthInRange(selection) });
    };
    caret.current = place;
    const disposables = [
      instance.onDidChangeCursorSelection(place),
      instance.onDidChangeModel(describe), instance.onDidChangeModelOptions(describe), instance.onDidChangeModelLanguage(describe),
    ];
    handle.current = {
      focus: () => instance.focus(),
      reveal: (line, column, length = 0) => {
        const model = instance.getModel();
        if (!model) return;
        const lineNumber = Math.min(Math.max(line, 1), model.getLineCount());
        const start = Math.min(Math.max(column, 1), model.getLineMaxColumn(lineNumber));
        const range = new monaco.Range(lineNumber, start, lineNumber, Math.min(start + Math.max(length, 0), model.getLineMaxColumn(lineNumber)));
        // Measured now: a line is not found where it was in a pane of another width.
        instance.layout();
        instance.setSelection(range); instance.revealRangeInCenterIfOutsideViewport(range); instance.focus();
      },
      read: id => { const entry = models.current.get(id); return entry ? { text: entry.model.getValue(), version: entry.model.getAlternativeVersionId() } : null; },
      saved: (id, version) => { const entry = models.current.get(id); if (entry) { entry.saved = version; report(id, entry); } },
      run: action => { instance.focus(); void instance.getAction(action)?.run(); },
      selection: () => {
        const model = instance.getModel(), selection = instance.getSelection();
        if (!model || !selection || selection.isEmpty() || selection.startLineNumber !== selection.endLineNumber) return "";
        return model.getValueInRange(selection).slice(0, 256);
      },
    };
    return () => {
      handle.current = null;
      unfollowTheme();
      disposables.forEach(value => value.dispose());
      instance.dispose();
      models.current.forEach(entry => { entry.changed.dispose(); entry.model.dispose(); });
      models.current.clear(); shown.current = null; editor.current = null;
    };
  }, []);

  function report(id: number, entry: Entry) {
    const dirty = entry.model.getAlternativeVersionId() !== entry.saved;
    if (dirty === entry.dirty) return;
    entry.dirty = dirty;
    latest.current.onDirty(id, dirty);
  }
  // A language that is not registered yet is set once its definition has loaded (see CodeEditor).
  function setLanguage(entry: Entry, language: EditorLanguage) {
    entry.language = language;
    if (registered(language)) monaco.editor.setModelLanguage(entry.model, language);
    else void ensureMonacoLanguage(language).then(loaded => { if (loaded && entry.language === language && !entry.model.isDisposed()) monaco.editor.setModelLanguage(entry.model, language); });
  }

  // One model per open document, made when its text is first there and kept until it closes.
  useLayoutEffect(() => {
    const instance = editor.current;
    if (!instance) return;
    const known = models.current;
    const wanted = new Set(documents.map(document => document.id));
    for (const [id, entry] of known) {
      if (wanted.has(id)) continue;
      if (shown.current === id) { instance.setModel(null); shown.current = null; }
      entry.changed.dispose(); entry.model.dispose(); known.delete(id);
    }
    for (const document of documents) {
      const language = fileLanguage(document.path);
      let entry = known.get(document.id);
      if (!entry) {
        const model = monaco.editor.createModel(document.text, registered(language) ? language : "plaintext");
        const created: Entry = { model, generation: document.generation, saved: model.getAlternativeVersionId(), dirty: false, language: null, applying: false, view: null,
          changed: model.onDidChangeContent(() => { if (!created.applying) report(document.id, created); }) };
        known.set(document.id, entry = created);
        setLanguage(entry, language);
        continue;
      }
      if (entry.generation !== document.generation) {
        // Read again from the disk: what it holds replaces the text, and is what the document is compared with.
        const view = shown.current === document.id ? instance.saveViewState() : null;
        entry.applying = true;
        try { replaceText(entry.model, document.text); } finally { entry.applying = false; }
        if (view) instance.restoreViewState(view);
        entry.generation = document.generation;
        entry.saved = entry.model.getAlternativeVersionId();
        if (entry.dirty) { entry.dirty = false; latest.current.onDirty(document.id, false); }
      }
      if (entry.language !== language) setLanguage(entry, language);
    }
    if (shown.current === active && (active === null || known.has(active))) return;
    const previous = shown.current === null ? undefined : known.get(shown.current);
    if (previous) previous.view = instance.saveViewState();
    const next = active === null ? undefined : known.get(active);
    instance.setModel(next?.model ?? null);
    if (next?.view) instance.restoreViewState(next.view);
    shown.current = next ? active : null;
    // The caret of the document now shown, where it was left.
    caret.current();
  }, [documents, active]);

  useLayoutEffect(() => {
    editor.current?.updateOptions({ readOnly, ariaLabel: label, wordWrap: wrap ? "on" : "off", minimap: { enabled: minimap, renderCharacters: false, maxColumn: 100 } });
  }, [readOnly, label, wrap, minimap]);

  return <div ref={host} className={`code-editor editor-surface ${Classes.MONOSPACE_TEXT}`} data-editor-keys data-hidden={hidden} aria-hidden={hidden} />;
}
