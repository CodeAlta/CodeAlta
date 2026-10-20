import { useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { createPortal } from "react-dom";
import { monaco } from "../monaco/monacoEnvironment";
import { useShellLanguage } from "../shellLanguage";
import { neighbourThread, orderThreads, type ReviewSide, type ReviewThread } from "./reviewThreads";
import { ReviewCommentEditor, ReviewThreadCard, threadLabel, type ReviewThreadActions } from "./ReviewThreadCard";

type Lines = Readonly<{ startLine: number; endLine: number }>;

export type LineCommentLayerOptions = Readonly<{
  /** The editor the comments are drawn in: a plain editor, or one side of a diff. Null draws nothing. */
  editor: monaco.editor.ICodeEditor | null;
  /** The file of the model the editor shows, for the labels; null while it shows none. */
  path: string | null;
  /** The side of the diff the editor shows; a plain editor shows the `new` side. */
  side?: ReviewSide;
  /** The threads of that file and side to draw on their lines: not resolved, not outdated (see `threadsOnLines`). */
  threads: readonly ReviewThread[];
  /** What the cards can do; null shows the threads without their actions. */
  actions: ReviewThreadActions | null;
  /** Writes a new comment on lines of the model; null: no comment can be added here. */
  onAdd: ((lines: Lines, body: string, suggestion: string | null) => Promise<boolean>) | null;
  /** Why a comment cannot be added now ("Save the file to comment on it"); null when it can. */
  blocked?: string | null;
  /** The editor's own text keys are free (a read-only editor): `c` comments the line of the caret, `n` and `p` go to the next and previous thread. */
  keys?: boolean;
  /** A key changes when the editor is given another model: the zones of the old one are gone with it. */
  modelKey?: string | number | null;
}>;

export type LineCommentLayer = Readonly<{
  /** The cards, drawn into the view zones of the editor: render them anywhere in the tree of the host. */
  portals: ReactNode;
  /** Opens the editor of a new comment on lines, or on the selection (the line of the caret) of the editor. */
  comment: (lines?: Lines) => void;
  /** Scrolls to a thread and gives its card the keyboard; false when it is not drawn here. */
  reveal: (id: string) => boolean;
  /** Goes to the next or previous thread of the file, from the caret or the thread that has the keyboard. */
  step: (direction: 1 | -1) => boolean;
  /** A new comment is being written. */
  composing: boolean;
}>;

type Zone = { id: string | null; zone: monaco.editor.IViewZone; view: number };
const composerKey = "\u0000composer";
const lastLine = (model: monaco.editor.ITextModel | null, line: number) => Math.min(Math.max(line, 1), model?.getLineCount() ?? 1);

/**
 * Comments in one Monaco editor. A **+** shows in the glyph margin of the line under the pointer; a click there
 * comments that line, a drag or a click and then a Shift-click a range of lines, and `c` (with `keys`) the line of
 * the caret or the lines of the selection. Each thread is a card under its last line, in a view zone whose content
 * is a React portal; the lines it is about are marked. The zones are drawn again from the threads each time they,
 * the text or the model change: nothing is kept in Monaco between two readings of the store.
 */
export function useLineCommentLayer({ editor, path, side = "new", threads, actions, onAdd, blocked = null, keys = false, modelKey = null }: LineCommentLayerOptions): LineCommentLayer {
  const { t } = useShellLanguage();
  const [composer, setComposer] = useState<Lines | null>(null);
  // The model the zones were drawn for, bumped when Monaco replaces the view (another model) or the text (a flush).
  const [view, setView] = useState(0);
  const nodes = useRef(new Map<string, HTMLDivElement>());
  const zones = useRef(new Map<string, Zone>());
  const heights = useRef(new Map<string, number>());
  const cards = useRef(new Map<string, HTMLElement>());
  const composerBox = useRef<HTMLTextAreaElement | null>(null);
  const anchor = useRef<number | null>(null);
  const placed = useMemo(() => path === null ? [] : orderThreads(threads.filter(thread => thread.path === path && thread.side === side)), [threads, path, side]);
  const latest = useRef({ placed, composer, onAdd, blocked, keys, path });
  latest.current = { placed, composer, onAdd, blocked, keys, path };

  function nodeFor(key: string): HTMLDivElement {
    let node = nodes.current.get(key);
    if (!node) {
      node = document.createElement("div");
      node.className = "review-zone";
      // Above the text layer, so that the card takes the pointer; the editor leaves a zone's own events alone.
      node.style.zIndex = "10";
      nodes.current.set(key, node);
    }
    return node;
  }

  function open(lines: Lines) {
    const current = latest.current;
    if (!current.onAdd || current.blocked || !editor?.getModel()) return;
    const model = editor.getModel()!;
    const next = { startLine: lastLine(model, Math.min(lines.startLine, lines.endLine)), endLine: lastLine(model, Math.max(lines.startLine, lines.endLine)) };
    anchor.current = next.startLine;
    setComposer(next);
    focusSoon(() => composerBox.current, next.endLine);
  }
  // A new card is in the page once the editor has laid out its zone, a frame or two later.
  function focusSoon(find: () => HTMLElement | null, line: number, attempts = 12) {
    const element = find();
    if (element && element.offsetParent !== null) {
      editor?.revealLineInCenterIfOutsideViewport(line);
      element.focus({ preventScroll: true });
    } else if (attempts > 0) requestAnimationFrame(() => focusSoon(find, line, attempts - 1));
  }
  function backToLine(line: number) {
    if (!editor?.getModel()) return;
    editor.setPosition({ lineNumber: lastLine(editor.getModel(), line), column: 1 });
    editor.focus();
  }
  function selectionLines(): Lines | null {
    const selection = editor?.getSelection();
    if (!selection) return null;
    // A selection that ends at the start of a line is about the lines above it.
    const end = selection.endColumn === 1 && selection.endLineNumber > selection.startLineNumber ? selection.endLineNumber - 1 : selection.endLineNumber;
    return { startLine: selection.startLineNumber, endLine: end };
  }
  function reveal(id: string): boolean {
    const thread = latest.current.placed.find(item => item.id === id);
    if (!thread || !editor) return false;
    editor.revealLineInCenterIfOutsideViewport(thread.endLine);
    focusSoon(() => cards.current.get(id) ?? null, thread.endLine);
    return true;
  }
  function step(direction: 1 | -1): boolean {
    const focused = [...cards.current].find(([, card]) => card.contains(document.activeElement))?.[0] ?? null;
    const line = editor?.getPosition()?.lineNumber ?? 0;
    const next = neighbourThread(latest.current.placed, focused ? { id: focused } : { path: latest.current.path ?? "", line }, direction);
    return next ? reveal(next.id) : false;
  }
  const commands = useRef({ open, step, selectionLines });
  commands.current = { open, step, selectionLines };

  // The pointer and the keys of the editor.
  useEffect(() => {
    if (!editor) return;
    let hover: string[] = [];
    let range: string[] = [];
    let drag: { from: number; to: number } | null = null;
    const able = () => !!latest.current.onAdd && !latest.current.blocked && !!editor.getModel();
    const mark = (lines: Lines | null) => {
      const model = editor.getModel();
      if (!model) return;
      range = model.deltaDecorations(range, lines ? [{ range: new monaco.Range(lines.startLine, 1, lines.endLine, 1),
        options: { isWholeLine: true, className: "review-line-range", glyphMarginClassName: "review-glyph-range" } }] : []);
    };
    const glyphLine = (event: monaco.editor.IEditorMouseEvent) => event.target.type === monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN
      || event.target.type === monaco.editor.MouseTargetType.GUTTER_LINE_DECORATIONS ? event.target.position?.lineNumber ?? null : null;
    const finish = () => {
      window.removeEventListener("pointerup", finish, true);
      if (!drag) return;
      const lines = { startLine: Math.min(drag.from, drag.to), endLine: Math.max(drag.from, drag.to) };
      drag = null;
      mark(null);
      commands.current.open(lines);
    };
    const down = editor.onMouseDown(event => {
      const line = glyphLine(event);
      if (line === null || !event.event.leftButton || !able()) return;
      event.event.preventDefault();
      // Shift extends the comment being written, or the last line clicked, to this line.
      const from = event.event.shiftKey ? latest.current.composer?.startLine ?? anchor.current : null;
      if (from !== null && from !== undefined) { commands.current.open({ startLine: from, endLine: line }); return; }
      drag = { from: line, to: line };
      mark({ startLine: line, endLine: line });
      window.addEventListener("pointerup", finish, true);
    });
    const moved = editor.onMouseMove(event => {
      const model = editor.getModel();
      if (!model) return;
      const line = event.target.position?.lineNumber ?? null;
      if (drag && line !== null) {
        drag.to = line;
        mark({ startLine: Math.min(drag.from, line), endLine: Math.max(drag.from, line) });
        return;
      }
      const over = glyphLine(event);
      hover = model.deltaDecorations(hover, over !== null && able()
        ? [{ range: new monaco.Range(over, 1, over, 1), options: { glyphMarginClassName: "review-glyph-add", glyphMarginHoverMessage: { value: t("Comment on this line · drag or Shift-click for a range") } } }]
        : []);
    });
    const up = editor.onMouseUp(() => { if (drag) finish(); });
    const left = editor.onMouseLeave(() => { const model = editor.getModel(); if (model) hover = model.deltaDecorations(hover, []); });
    const typed = editor.onKeyDown(event => {
      if (!latest.current.keys || event.ctrlKey || event.metaKey || event.altKey || event.shiftKey) return;
      if (event.keyCode === monaco.KeyCode.KeyC && able()) { const lines = commands.current.selectionLines(); if (lines) commands.current.open(lines); }
      else if (event.keyCode === monaco.KeyCode.KeyN || event.keyCode === monaco.KeyCode.KeyP) { if (!commands.current.step(event.keyCode === monaco.KeyCode.KeyN ? 1 : -1)) return; }
      else return;
      event.preventDefault(); event.stopPropagation();
    });
    // Another model is another view: its zones are gone. A text read again leaves the zones where they were drawn.
    const modelChanged = editor.onDidChangeModel(() => { zones.current.clear(); hover = []; range = []; drag = null; setComposer(null); setView(value => value + 1); });
    const flushed = editor.onDidChangeModelContent(event => { if (event.isFlush) setView(value => value + 1); });
    // A card is as wide as the text area, and stays in it when the text scrolls sideways.
    const fit = () => {
      const layout = editor.getLayoutInfo(), left = editor.getScrollLeft();
      const width = `${Math.max(160, layout.contentWidth - layout.verticalScrollbarWidth - 12)}px`, shift = `translateX(${left}px)`;
      for (const node of nodes.current.values()) { if (node.style.width !== width) node.style.width = width; if (node.style.transform !== shift) node.style.transform = shift; }
    };
    const laid = editor.onDidLayoutChange(fit);
    const scrolled = editor.onDidScrollChange(event => { if (event.scrollLeftChanged) fit(); });
    fitZones.current = fit;
    return () => {
      window.removeEventListener("pointerup", finish, true);
      const model = editor.getModel();
      if (model) { model.deltaDecorations(hover, []); model.deltaDecorations(range, []); }
      down.dispose(); moved.dispose(); up.dispose(); left.dispose(); typed.dispose(); modelChanged.dispose(); flushed.dispose(); laid.dispose(); scrolled.dispose();
      fitZones.current = () => { };
    };
  }, [editor, t]);
  const fitZones = useRef(() => { });

  // A model given by the host (a key) is another view as well.
  const lastModel = useRef(modelKey);
  useLayoutEffect(() => {
    if (lastModel.current === modelKey) return;
    lastModel.current = modelKey;
    zones.current.clear();
    setComposer(null);
    setView(value => value + 1);
  }, [modelKey]);

  // The zones and the marks of the threads, drawn from the threads. An editor that goes takes its zones and marks with it.
  const marks = useRef<monaco.editor.IEditorDecorationsCollection | null>(null);
  useLayoutEffect(() => () => { zones.current.clear(); marks.current = null; }, [editor]);
  useLayoutEffect(() => {
    if (!editor) return;
    const model = editor.getModel();
    const wanted = new Map<string, number>();
    if (model) {
      for (const thread of placed) wanted.set(thread.id, lastLine(model, thread.endLine));
      if (composer) wanted.set(composerKey, lastLine(model, composer.endLine));
    }
    editor.changeViewZones(accessor => {
      for (const [key, zone] of zones.current) {
        if (wanted.has(key)) continue;
        if (zone.id) accessor.removeZone(zone.id);
        zones.current.delete(key);
      }
      let ordinal = 0;
      for (const [key, line] of wanted) {
        const known = zones.current.get(key);
        if (known?.id && known.zone.afterLineNumber === line && known.view === view) { ordinal++; continue; }
        if (known?.id) accessor.removeZone(known.id);
        const zone: monaco.editor.IViewZone = { afterLineNumber: line, heightInPx: heights.current.get(key) ?? 96, domNode: nodeFor(key), ordinal: ordinal++,
          suppressMouseDown: false };
        zones.current.set(key, { id: accessor.addZone(zone), zone, view });
      }
    });
    for (const key of [...nodes.current.keys()]) if (!wanted.has(key)) { nodes.current.delete(key); heights.current.delete(key); }
    // The cards are read by screen readers: Monaco hides its zones from them.
    const layer = editor.getDomNode()?.querySelector(".view-zones");
    if (layer?.getAttribute("aria-hidden")) { layer.removeAttribute("aria-hidden"); layer.removeAttribute("role"); }
    fitZones.current();
    if (!marks.current) marks.current = editor.createDecorationsCollection();
    marks.current.set(model ? [
      ...placed.map(thread => ({ range: new monaco.Range(lastLine(model, thread.startLine), 1, lastLine(model, thread.endLine), 1),
        options: { isWholeLine: true, className: `review-line-commented${thread.state === "draft" ? " review-line-draft" : ""}`, glyphMarginClassName: "review-glyph-thread",
          stickiness: monaco.editor.TrackedRangeStickiness.NeverGrowsWhenTypingAtEdges } })),
      ...composer ? [{ range: new monaco.Range(lastLine(model, composer.startLine), 1, lastLine(model, composer.endLine), 1),
        options: { isWholeLine: true, className: "review-line-range", glyphMarginClassName: "review-glyph-range" } }] : [],
    ] : []);
  }, [editor, placed, composer, view]);
  useEffect(() => { if (blocked) setComposer(null); }, [blocked]);

  function sized(key: string, height: number) {
    heights.current.set(key, height);
    const zone = zones.current.get(key);
    if (!editor || !zone?.id || zone.zone.heightInPx === height) return;
    zone.zone.heightInPx = height;
    editor.changeViewZones(accessor => accessor.layoutZone(zone.id!));
  }

  const model = editor?.getModel() ?? null;
  // The node of each zone exists before its zone: the card is drawn into it as the zone is added.
  const portals = path === null || !editor ? null : <>
    {placed.map(thread => {
      const node = nodeFor(thread.id);
      return createPortal(<Measured onHeight={height => sized(thread.id, height)}>
        <ReviewThreadCard thread={thread} actions={actions} cardRef={card => { if (card) cards.current.set(thread.id, card); else cards.current.delete(thread.id); }}
          original={model ? lineText(model, thread) : undefined} onLeave={() => backToLine(thread.endLine)} />
      </Measured>, node, thread.id);
    })}
    {composer && onAdd && createPortal(<Measured onHeight={height => sized(composerKey, height)}>
      <ReviewCommentEditor key={`${composer.startLine}-${composer.endLine}`} label={threadLabel(t, composer, path)} lines={composer} editorRef={composerBox}
        original={model ? lineText(model, composer) : ""}
        onCancel={() => { const line = composer.startLine; setComposer(null); backToLine(line); }}
        onSave={async (body, suggestion) => {
          const done = await onAdd(composer, body, suggestion);
          if (done) { const line = composer.endLine; setComposer(null); backToLine(line + 1); }
          return done;
        }} />
    </Measured>, nodeFor(composerKey), composerKey)}
  </>;
  return { portals, comment: lines => { const chosen = lines ?? selectionLines(); if (chosen) open(chosen); }, reveal, step, composing: composer !== null };
}

const lineText = (model: monaco.editor.ITextModel, lines: Lines) => {
  const start = lastLine(model, lines.startLine), end = lastLine(model, lines.endLine);
  return model.getValueInRange(new monaco.Range(start, 1, end, model.getLineMaxColumn(end))).replace(/\r\n?/gu, "\n");
};

/** Reports the height of what it holds as it changes, for the zone around it to follow. */
function Measured({ onHeight, children }: { onHeight: (height: number) => void; children: ReactNode }) {
  const box = useRef<HTMLDivElement>(null);
  const report = useRef(onHeight); report.current = onHeight;
  useLayoutEffect(() => {
    const node = box.current;
    if (!node) return;
    // A zone out of the view is not displayed: its card has no height then, and keeps the one it had.
    const measure = () => { const height = node.getBoundingClientRect().height; if (height > 0) report.current(Math.ceil(height) + 10); };
    const observer = new ResizeObserver(measure);
    observer.observe(node);
    measure();
    return () => observer.disconnect();
  }, []);
  return <div className="review-zone-content" ref={box}>{children}</div>;
}
