// A real code editor with its comment layer over a reviews hub whose host is in memory, and thread cards alone.
// The page drives it through `lineCommentsFixture`.
import { useEffect, useMemo, useState } from "react";
import { createRoot } from "react-dom/client";
import { CodeEditor } from "../monaco/CodeEditor";
import { monaco } from "../monaco/monacoEnvironment";
import { createReviewsHub } from "./reviewsHub";
import { createReviewServer } from "./reviewServer.mount";
import { ReviewThreadCard, type ReviewThreadActions } from "./ReviewThreadCard";
import { readThread, threadsOnLines, type ReviewThread } from "./reviewThreads";
import { useLineCommentLayer } from "./useLineCommentLayer";
import { useReviewActions, useReviewList } from "./useReviews";
import type { ReviewThreadDto } from "#neoastra";

const path = "docs/file.md";
const scope = { projectId: "p", checkout: null };
const target = { kind: "file", path } as const;
const initial = Array.from({ length: 60 }, (_, index) => `line ${index + 1}`).join("\n");
const server = createReviewServer(initial);
const hub = createReviewsHub(server.api);
// What the cards alone are asked to do.
const calls: string[] = [];
const cardActions: ReviewThreadActions = {
  update: async (id, body) => { calls.push(`update:${id}:${body}`); return true; },
  remove: async id => { calls.push(`remove:${id}`); return true; },
  resolve: async (id, resolved) => { calls.push(`resolve:${id}:${resolved}`); return true; },
  reply: async (id, body) => { calls.push(`reply:${id}:${body}`); return true; },
};

const state = {
  editor: null as monaco.editor.ICodeEditor | null,
  setText: (_: string) => { },
  setCards: (_: readonly { thread: ReviewThread; original: string }[]) => { },
};

function Fixture() {
  const [text, setText] = useState(initial);
  const [editor, setEditor] = useState<monaco.editor.ICodeEditor | null>(null);
  const [cards, setCards] = useState<readonly { thread: ReviewThread; original: string }[]>([]);
  state.setText = setText;
  state.setCards = setCards;
  state.editor = editor;
  useEffect(() => hub.connect("epoch"), []);
  const list = useReviewList(hub, scope, target);
  const actions = useReviewActions(hub, scope);
  const drawn = useMemo(() => threadsOnLines(list.threads, "new", path), [list.threads]);
  const layer = useLineCommentLayer({ editor, path, threads: drawn, actions,
    onAdd: async (lines, body, suggestion) => (await hub.add(scope, target, { side: "new", startLine: lines.startLine, endLine: lines.endLine, body, suggestion })).ok });
  return <>
    <div id="editor" style={{ width: 900, height: 1000 }}>
      <CodeEditor value={text} onChange={setText} language="plaintext" label={path} comments onEditor={setEditor} />
    </div>
    {layer.portals}
    <div id="cards" style={{ width: 700, display: "grid", gap: 8 }}>
      {cards.map(card => <ReviewThreadCard key={card.thread.id} thread={card.thread} original={card.original} actions={cardActions} />)}
    </div>
  </>;
}

const editor = () => state.editor!;
Object.assign(window, { lineCommentsFixture: {
  state, server, calls, monaco,
  /** The top of a line in the page, or null when it is not drawn. */
  lineTop(line: number) {
    const position = editor().getScrolledVisiblePosition({ lineNumber: line, column: 1 });
    const visible = editor().getVisibleRanges().some(range => range.startLineNumber <= line && line <= range.endLineNumber);
    return position && visible ? Math.round(editor().getDomNode()!.getBoundingClientRect().top + position.top) : null;
  },
  /** Page coordinates of the middle of the glyph margin of a line. */
  glyph(line: number) {
    const position = editor().getScrolledVisiblePosition({ lineNumber: line, column: 1 })!;
    const layout = editor().getLayoutInfo(), box = editor().getDomNode()!.getBoundingClientRect();
    return { x: Math.round(box.left + layout.glyphMarginLeft + layout.glyphMarginWidth / 2), y: Math.round(box.top + position.top + position.height / 2) };
  },
  /** The zone of a thread's card, or of the composer: its top in the page, or null when there is none. */
  zoneTop(id: string | null) {
    const inner = document.querySelector(id === null ? "#editor .review-zone .review-editor" : `#editor .review-zone [data-thread-id="${id}"]`);
    const zone = inner?.closest(".review-zone");
    return zone ? Math.round(zone.getBoundingClientRect().top) : null;
  },
  zones: () => document.querySelectorAll("#editor .review-zone").length,
  thread: (id: string) => server.threads.find(thread => thread.id === id) ?? null,
  setText(value: string) { state.setText(value); server.setText(value); },
  showCards(threads: readonly (Partial<ReviewThreadDto> & { original: string })[]) {
    state.setCards(threads.map((value, index) => ({ original: value.original, thread: readThread({ id: `k${index}`, target: "file", path, side: "new", startLine: 2, endLine: 3,
      anchor: value.original, body: "Body", suggestion: null, state: "draft", outdated: false, replies: [], round: null, createdAt: "", updatedAt: "", ...value })! })));
  },
} });

document.documentElement.classList.add("bp6-dark");
createRoot(document.getElementById("root")!).render(<Fixture />);
