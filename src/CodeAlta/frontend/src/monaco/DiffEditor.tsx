import { useLayoutEffect, useRef, type RefObject } from "react";
import { Classes } from "@blueprintjs/core";
import "monaco-editor/features/diffEditor/register.js";
import { followPageFont, followShellTheme, monaco, pageFontFamily } from "./monacoEnvironment";
import { ensureMonacoLanguage } from "./monacoLanguages";
import type { EditorLanguage } from "./fileLanguage";

/** A side of the diff: the old text (the original editor) or the new one (the modified editor). */
export type DiffSide = "old" | "new";
/** What a host can ask of the mounted diff editor. */
export type DiffEditorHandle = Readonly<{ focus: () => void;
  /** Moves the caret to the next or previous change and shows it. */
  go: (target: "next" | "previous") => void;
  /**
   * Puts the caret on a 1-based line of a side and shows it in the middle of the view, unfolding the hidden
   * unchanged region it is in, with the line after it, so a view zone under the line shows too. While the texts
   * are being compared (a document that opens, a text read again) it waits for the comparison, whose folds would
   * hide the line again; a document that opens then shows that line in the place of its first change.
   */
  reveal: (side: DiffSide, line: number) => void }>;
/** The changes of the shown file: how many there are, and the one the caret is in or after (0 before the first). */
export type DiffEditorChanges = Readonly<{ count: number; current: number }>;
/** The editors of a mounted diff, for a layer that adds view zones and decorations to them. */
export type DiffEditors = Readonly<{ original: monaco.editor.ICodeEditor; modified: monaco.editor.ICodeEditor; diff: monaco.editor.IStandaloneDiffEditor }>;

/**
 * The old line under the pointer when the diff is in one column and the pointer is on removed lines: Monaco draws
 * them in a view zone of the new side, so its mouse events name no old line. Null elsewhere, and when a removed
 * line is wrapped, since the rows of the zone are no longer lines then.
 */
export function removedLineAt(diff: monaco.editor.IDiffEditor, event: monaco.editor.IEditorMouseEvent): number | null {
  const { target } = event;
  if (target.type !== monaco.editor.MouseTargetType.GUTTER_VIEW_ZONE && target.type !== monaco.editor.MouseTargetType.CONTENT_VIEW_ZONE) return null;
  const zone = target.element?.closest(".inline-deleted-margin-view-zone, .view-lines.line-delete");
  // The zone of the removed lines sits after the new line before them, which is the start of their change less one
  // (a change that only removes names the line it follows).
  const change = zone && diff.getLineChanges()?.find(value => value.originalEndLineNumber > 0
    && (value.modifiedEndLineNumber ? value.modifiedStartLineNumber - 1 : value.modifiedStartLineNumber) === target.detail.afterLineNumber);
  if (!zone || !change) return null;
  const modified = diff.getModifiedEditor(), lines = change.originalEndLineNumber - change.originalStartLineNumber + 1, top = zone.getBoundingClientRect().top;
  if (modified.getOption(monaco.editor.EditorOption.wrappingInfo).wrappingColumn !== -1) {
    // The text beside the margin, found by its place: one row per line, or a line was wrapped.
    const rows = Array.from(modified.getDomNode()?.querySelectorAll(".view-lines.line-delete") ?? []).find(node => Math.abs(node.getBoundingClientRect().top - top) < 1);
    if (!rows || rows.querySelectorAll(":scope > .view-line").length !== lines) return null;
  }
  const row = Math.floor((event.event.browserEvent.clientY - top) / modified.getOption(monaco.editor.EditorOption.lineHeight));
  return row >= 0 && row < lines ? change.originalStartLineNumber + row : null;
}

/**
 * A read-only Monaco diff of two texts with the colors of their language: side by side or in one column,
 * with the unchanged regions folded away. The two texts of a `documentKey` keep their models, so a file that
 * is read again keeps its scroll position.
 */
export function DiffEditor({ documentKey, original, modified, language, label, sideBySide, wrap, ignoreWhitespace, collapseUnchanged, comments = false,
  onChanges, onContentHeight, onEditors, onInline, handle }: {
  /** Identifies the compared document: another key starts at the first change. */
  documentKey: string; original: string; modified: string; language: EditorLanguage; label: string;
  sideBySide: boolean; wrap: boolean; ignoreWhitespace: boolean; collapseUnchanged: boolean;
  /**
   * For a comment layer: the new side gets a glyph margin, where the layer puts its marks (Monaco gives the old side
   * one of its own side by side, and none in one column), and view zones stay in line when a folded region unfolds.
   */
  comments?: boolean;
  onChanges?: (changes: DiffEditorChanges) => void;
  /** The editors once the diff is created, and null when it goes. */
  onEditors?: (editors: DiffEditors | null) => void;
  /**
   * Whether the diff is in one column, once it is created and whenever that changes: because side by side is off,
   * or because the diff is narrower than the width that shows two columns.
   */
  onInline?: (inline: boolean) => void;
  /**
   * The height of what the diff shows, in pixels, whenever it changes: with it the editor is one of several in
   * a view that scrolls, which gives it that height. It has no overview of its own then, and the wheel moves
   * the view around it once the diff has nothing more to scroll. Read when the editor is created.
   */
  onContentHeight?: (height: number) => void;
  handle?: RefObject<DiffEditorHandle | null>;
}) {
  const host = useRef<HTMLDivElement>(null);
  const editor = useRef<monaco.editor.IStandaloneDiffEditor>(null);
  const latest = useRef({ original, modified, comments, onChanges, onContentHeight, onEditors, onInline });
  latest.current = { original, modified, comments, onChanges, onContentHeight, onEditors, onInline };
  const reported = useRef<DiffEditorChanges>({ count: -1, current: -1 });
  // The first comparison of the document is done: until then the editor holds both texts whole, whatever the diff will fold.
  const compared = useRef(false);
  const rewrapLeft = useRef<(() => void) | null>(null);
  const columns = useRef<(() => void) | null>(null);
  // The comparison matches the texts shown: none is under way.
  const upToDate = useRef(false);
  // A line asked for while the texts were being compared, shown once they are.
  const pendingReveal = useRef<Readonly<{ side: DiffSide; line: number }> | null>(null);
  const showLine = useRef<((side: DiffSide, line: number) => void) | null>(null);

  useLayoutEffect(() => {
    const node = host.current!, embedded = !!latest.current.onContentHeight;
    const instance = monaco.editor.createDiffEditor(node, { automaticLayout: true, readOnly: true, originalEditable: false,
      fontFamily: pageFontFamily("code"), fontSize: 13, lineHeight: 20, minimap: { enabled: false }, scrollBeyondLastLine: false,
      stickyScroll: { enabled: false }, renderOverviewRuler: !embedded, renderIndicators: true, renderMarginRevertIcon: false, renderGutterMenu: false,
      useInlineViewWhenSpaceIsLimited: true, renderSideBySideInlineBreakpoint: 780, diffAlgorithm: "advanced", lineNumbersMinChars: 4,
      glyphMargin: false, folding: false, links: false, padding: { top: 6, bottom: 6 }, renderLineHighlight: "none",
      scrollbar: { useShadows: false, alwaysConsumeMouseWheel: !embedded },
      occurrencesHighlight: "off", matchBrackets: "never" });
    editor.current = instance;
    // Monaco unfolds a hidden unchanged region when the caret enters it, from the side of the region closer to the
    // line; a view zone under the line shows only when the line after it shows too, so the caret visits both.
    const show = (side: DiffSide, line: number) => {
      const target = side === "old" ? instance.getOriginalEditor() : instance.getModifiedEditor(), model = target.getModel();
      if (!model) return;
      const lineNumber = Math.min(Math.max(Math.trunc(line), 1), model.getLineCount());
      target.setPosition({ lineNumber: Math.min(lineNumber + 1, model.getLineCount()), column: 1 });
      target.setPosition({ lineNumber, column: 1 });
      // In one column the old side is a strip of line numbers that scrolls with the new one, its removed lines beside theirs.
      target.revealLineInCenter(lineNumber);
    };
    showLine.current = show;
    if (handle) handle.current = { focus: () => instance.getModifiedEditor().focus(), go: target => { instance.goToDiff(target); report(); },
      reveal: (side, line) => { if (upToDate.current) show(side, line); else pendingReveal.current = { side, line }; } };
    const report = () => {
      const changes = instance.getLineChanges() ?? [];
      const line = instance.getModifiedEditor().getPosition()?.lineNumber ?? 0;
      // A removal has no line of its own in the new text: it sits after the line it names.
      const current = changes.filter(change => (change.modifiedEndLineNumber ? change.modifiedStartLineNumber : change.modifiedStartLineNumber + 1) <= line).length;
      if (reported.current.count === changes.length && reported.current.current === current) return;
      reported.current = { count: changes.length, current };
      latest.current.onChanges?.(reported.current);
    };
    // While the diff is in one column (the choice of the user, or a window too narrow for two) Monaco stops
    // wrapping the left side, which is hidden then, and does not wrap it again when the diff has two columns
    // again: the left side is given back the wrapping of the diff.
    const rewrap = () => {
      const left = instance.getOriginalEditor();
      if (node.querySelector(".monaco-diff-editor")?.classList.contains("side-by-side") && left.getRawOptions().wordWrapOverride2 === "off")
        left.updateOptions({ wordWrapOverride2: "inherit" });
    };
    rewrapLeft.current = rewrap;
    // Monaco says whether it shows two columns only by the class of its root, which it sets with the layout of both
    // sides: the change is seen when either side is laid out again, or when the options change.
    let inline: boolean | null = null;
    const reportColumns = () => {
      const now = !node.querySelector(".monaco-diff-editor")?.classList.contains("side-by-side");
      if (now === inline) return;
      inline = now;
      latest.current.onInline?.(now);
    };
    columns.current = reportColumns;
    const unfollowTheme = followShellTheme(), unfollowFont = followPageFont(instance, "code");
    // Both sides are as tall as each other side by side; in one column the new text holds the removed lines too.
    const sized = () => {
      if (compared.current) latest.current.onContentHeight?.(Math.max(instance.getOriginalEditor().getContentHeight(), instance.getModifiedEditor().getContentHeight()));
    };
    const updated = instance.onDidUpdateDiff(() => {
      const first = !compared.current;
      report(); compared.current = true; upToDate.current = true; sized();
      // The first comparison of a document shows the line asked for in the place of its first change, below.
      const pending = pendingReveal.current;
      if (pending && !first) { pendingReveal.current = null; show(pending.side, pending.line); }
    });
    // A text read again is compared again, a moment later.
    const edited = [instance.getOriginalEditor().onDidChangeModelContent(() => { upToDate.current = false; }),
      instance.getModifiedEditor().onDidChangeModelContent(() => { upToDate.current = false; })];
    const moved = instance.getModifiedEditor().onDidChangeCursorPosition(report);
    // The left side takes its place when the diff goes from one column to two.
    const widened = instance.getOriginalEditor().onDidLayoutChange(() => { rewrap(); reportColumns(); });
    const laidOut = instance.getModifiedEditor().onDidLayoutChange(reportColumns);
    // A view zone under a line of a folded region takes its height when the region unfolds, but Monaco ignores the
    // change of zones that comes with its own unfolding, and leaves the other side without the room to stay in line.
    // A zone added and removed once the unfolding is done makes it align both sides again.
    const realign = () => queueMicrotask(() => {
      if (latest.current.comments && editor.current === instance)
        instance.getModifiedEditor().changeViewZones(accessor => accessor.removeZone(accessor.addZone({ afterLineNumber: 0, heightInPx: 0, domNode: document.createElement("div") })));
    });
    const unfolded = [instance.getOriginalEditor().onDidChangeHiddenAreas(realign), instance.getModifiedEditor().onDidChangeHiddenAreas(realign)];
    const sizes = embedded ? [instance.getOriginalEditor().onDidContentSizeChange(sized), instance.getModifiedEditor().onDidContentSizeChange(sized)] : [];
    reportColumns();
    latest.current.onEditors?.({ original: instance.getOriginalEditor(), modified: instance.getModifiedEditor(), diff: instance });
    return () => {
      if (handle) handle.current = null;
      latest.current.onEditors?.(null);
      columns.current = null; showLine.current = null; pendingReveal.current = null;
      unfollowTheme(); unfollowFont(); updated.dispose(); moved.dispose(); widened.dispose(); laidOut.dispose(); edited.forEach(value => value.dispose()); unfolded.forEach(value => value.dispose()); sizes.forEach(size => size.dispose());
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
    compared.current = false; upToDate.current = false;
    pendingReveal.current = null;
    let disposed = false;
    void ensureMonacoLanguage(language).then(registered => {
      if (!registered || disposed || models.original.isDisposed()) return;
      monaco.editor.setModelLanguage(models.original, language); monaco.editor.setModelLanguage(models.modified, language);
    });
    // Once its first comparison is done, a document opens on its first change, or on the line asked for meanwhile.
    const first = instance.onDidUpdateDiff(() => {
      first.dispose();
      if (disposed) return;
      const pending = pendingReveal.current;
      pendingReveal.current = null;
      if (pending) showLine.current?.(pending.side, pending.line); else instance.revealFirstDiff();
    });
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
      diffWordWrap: wrap ? "on" : "off", glyphMargin: comments,
      hideUnchangedRegions: { enabled: collapseUnchanged, contextLineCount: 3, minimumLineCount: 4, revealLineCount: 20 } });
    rewrapLeft.current?.();
    columns.current?.();
  }, [sideBySide, wrap, ignoreWhitespace, collapseUnchanged, comments]);

  return <div ref={host} className={`diff-editor ${Classes.MONOSPACE_TEXT}`} role="group" aria-label={label} />;
}
