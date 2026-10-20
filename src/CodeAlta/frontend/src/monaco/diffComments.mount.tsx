// The diff editor alone, with its comment opt-ins, in a frame of a set width, over literal texts: no application host.
// The page drives it through `diffCommentsFixture`, and adds view zones as a comment layer would.
import { useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import { monaco } from "./monacoEnvironment";
import { CodeEditor } from "./CodeEditor";
import { DiffEditor, removedLineAt, type DiffEditorHandle, type DiffEditors, type DiffSide } from "./DiffEditor";

const lines = (count: number, name: (line: number) => string) => Array.from({ length: count }, (_, index) => name(index + 1)).join("\n") + "\n";
// Four hundred old lines. The new text changes 50-52, removes 100-104 and adds three lines after 300.
const original = lines(400, line => `line ${line}`);
const modified = lines(400, line => line >= 50 && line <= 52 ? `line ${line} changed` : `line ${line}`)
  .replace(/line 10[0-4]\n/g, "").replace("line 300\n", "line 300\nadded 1\nadded 2\nadded 3\n");

type MouseRecord = { side: DiffSide; type: number; line: number | null; zone: number | null; removed: number | null };
const state = {
  editors: null as DiffEditors | null,
  inline: [] as boolean[],
  /** How many comparisons the diff has finished. */
  compared: 0,
  mouse: [] as MouseRecord[],
  single: null as monaco.editor.ICodeEditor | null,
  handle: { current: null } as { current: DiffEditorHandle | null },
  set: (_: Partial<Look>) => { },
};
type Look = { width: number; sideBySide: boolean; wrap: boolean; collapse: boolean; comments: boolean; original: string; modified: string; documentKey: string };

function record(side: DiffSide, event: monaco.editor.IEditorMouseEvent) {
  const diff = state.editors?.diff;
  const { target } = event;
  state.mouse.push({ side, type: target.type, line: target.position?.lineNumber ?? null,
    zone: target.type === monaco.editor.MouseTargetType.GUTTER_VIEW_ZONE || target.type === monaco.editor.MouseTargetType.CONTENT_VIEW_ZONE ? target.detail.afterLineNumber : null,
    removed: diff && side === "new" ? removedLineAt(diff, event) : null });
}

function Fixture() {
  const [look, setLook] = useState<Look>({ width: 1000, sideBySide: true, wrap: false, collapse: true, comments: true, original, modified, documentKey: "a" });
  const handle = useRef<DiffEditorHandle | null>(null);
  state.handle = handle;
  state.set = values => setLook(current => ({ ...current, ...values }));
  return <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
    <div id="diff" style={{ width: look.width, height: 600 }}>
      <DiffEditor documentKey={look.documentKey} original={look.original} modified={look.modified} language="plaintext" label="diff"
        sideBySide={look.sideBySide} wrap={look.wrap} ignoreWhitespace={false} collapseUnchanged={look.collapse} comments={look.comments} handle={handle}
        onEditors={editors => {
          state.editors = editors;
          if (!editors) return;
          editors.original.onMouseDown(event => record("old", event));
          editors.modified.onMouseDown(event => record("new", event));
          editors.diff.onDidUpdateDiff(() => { state.compared++; });
        }}
        onInline={inline => state.inline.push(inline)} />
    </div>
    <div id="single" style={{ width: 600, height: 200 }}>
      <CodeEditor value={original} onChange={() => { }} language="plaintext" label="single" readOnly comments={look.comments} onEditor={editor => { state.single = editor; }} />
    </div>
  </div>;
}

const editorOf = (side: DiffSide) => side === "old" ? state.editors!.original : state.editors!.modified;
const zones = new Map<string, { side: DiffSide; id: string; node: HTMLElement }>();
Object.assign(window, { diffCommentsFixture: {
  state, monaco, original, modified,
  /** Adds a view zone of a given height under a line of a side, as a comment thread would be; `ordinal` places it among zones of the same line. */
  addZone(name: string, side: DiffSide, afterLineNumber: number, heightInPx: number, ordinal?: number) {
    const node = document.createElement("div");
    node.className = `fixture-zone fixture-zone-${name}`;
    node.style.background = "rgba(255, 128, 0, .4)";
    let id = "";
    editorOf(side).changeViewZones(accessor => { id = accessor.addZone({ afterLineNumber, heightInPx, domNode: node, ...(ordinal === undefined ? null : { ordinal }) }); });
    zones.set(name, { side, id, node });
  },
  removeZone(name: string) {
    const zone = zones.get(name)!;
    editorOf(zone.side).changeViewZones(accessor => accessor.removeZone(zone.id));
    zones.delete(name);
  },
  /** Places every zone again at the line it was given, as a layer does once the text is replaced. */
  layoutZones() {
    for (const zone of zones.values()) editorOf(zone.side).changeViewZones(accessor => accessor.layoutZone(zone.id));
  },
  /** Forgets every zone: those of a document that was replaced are gone with its view already. */
  clearZones() {
    for (const name of [...zones.keys()]) this.removeZone(name);
  },
  /** Where a zone is drawn: its top in the page and its height, or null when Monaco hides it. */
  zone(name: string) {
    const node = zones.get(name)!.node, box = node.getBoundingClientRect();
    return node.isConnected && node.offsetParent && box.height > 0 ? { top: Math.round(box.top), height: Math.round(box.height) } : null;
  },
  /** The top of a line of a side in the page, or null when the line is not drawn (hidden, or out of the view). */
  lineTop(side: DiffSide, line: number) {
    const editor = editorOf(side), position = editor.getScrolledVisiblePosition({ lineNumber: line, column: 1 });
    const node = editor.getDomNode()!, box = node.getBoundingClientRect();
    const visible = editor.getVisibleRanges().some(range => range.startLineNumber <= line && line <= range.endLineNumber);
    return position && visible ? Math.round(box.top + position.top) : null;
  },
  /** Page coordinates of the middle of the glyph margin, or of the text, of a line of a side. */
  point(side: DiffSide, line: number, where: "glyph" | "text") {
    const editor = editorOf(side), position = editor.getScrolledVisiblePosition({ lineNumber: line, column: 1 })!;
    const layout = editor.getLayoutInfo(), box = editor.getDomNode()!.getBoundingClientRect();
    const x = where === "glyph" ? layout.glyphMarginLeft + layout.glyphMarginWidth / 2 : layout.contentLeft + 10;
    return { x: Math.round(box.left + x), y: Math.round(box.top + position.top + position.height / 2) };
  },
  /** Page coordinates of a row of a block of removed lines Monaco draws in the new side in one column, in its margin or its text. */
  removedPoint(block: number, row: number, where: "margin" | "text") {
    const node = document.querySelectorAll(where === "margin" ? "#diff .inline-deleted-margin-view-zone" : "#diff .view-lines.line-delete")[block]!;
    const box = node.getBoundingClientRect(), lineHeight = state.editors!.modified.getOption(monaco.editor.EditorOption.lineHeight);
    const margin = state.editors!.modified.getLayoutInfo();
    return { x: Math.round(where === "margin" ? state.editors!.modified.getDomNode()!.getBoundingClientRect().left + margin.glyphMarginLeft + margin.glyphMarginWidth / 2 : box.left + 20),
      y: Math.round(box.top + row * lineHeight + lineHeight / 2) };
  },
  /** A decoration in the glyph margin of a line of a side, as the mark of a thread would be. */
  decorate(side: DiffSide, line: number) {
    return editorOf(side).createDecorationsCollection([{ range: new monaco.Range(line, 1, line, 1), options: { isWholeLine: true, glyphMarginClassName: "fixture-glyph" } }]);
  },
} });

document.documentElement.classList.add("bp6-dark");
createRoot(document.getElementById("root")!).render(<Fixture />);
