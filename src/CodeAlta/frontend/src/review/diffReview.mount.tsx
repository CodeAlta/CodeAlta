// The review of the changes in real diffs, over a reviews hub whose host is in memory: one diff with its comments
// (`useDiffComments` under a `ChangesReviewContext`), the view of all files (`AllChanges`), or the whole Changes tab
// (`ProjectChangesPanel`) over a played git host. The page shows one of them at a time through `diffReviewFixture.show`.
import { StrictMode, useMemo, useRef, useState } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import type { ReviewViewedDto } from "#neoastra";
import { AllChanges } from "../changes/AllChanges";
import { ProjectChangesPanel } from "../changes/ProjectChangesPanel";
import type { ChangeContent, ChangedFile } from "../changes/projectChanges";
import type { FileTab } from "../fileTabs";
import { DiffEditor, type DiffEditorHandle, type DiffEditors, type DiffSide } from "../monaco/DiffEditor";
import { monaco } from "../monaco/monacoEnvironment";
import type { Worktree } from "../worktrees/worktrees";
import { createReviewsHub, type ReviewsApi } from "./reviewsHub";
import { createReviewServer } from "./reviewServer.mount";
import type { ReviewThread } from "./reviewThreads";
import { ChangesReviewContext, useDiffComments, type ChangesReviewValue } from "./useDiffComments";
import { ReviewsHubContext, useReviewActions, useReviewList } from "./useReviews";

const lines = (count: number, name: (line: number) => string) => Array.from({ length: count }, (_, index) => name(index + 1)).join("\n") + "\n";
// Four hundred old lines. The new text changes 50-52, removes 100-104 and adds three lines after 300 (new 296-298).
const original = lines(400, line => `line ${line}`);
const modified = lines(400, line => line >= 50 && line <= 52 ? `line ${line} changed` : `line ${line}`)
  .replace(/line 10[0-4]\n/g, "").replace("line 300\n", "line 300\nadded 1\nadded 2\nadded 3\n");
const path = "src/file.txt";
const scope = { projectId: "p", checkout: null };
const changes = { kind: "changes" } as const;

// The host: the threads of the review in memory, and the files marked viewed, which it records.
const server = createReviewServer(modified);
const viewed: ReviewViewedDto[] = [];
const viewedAsked: { target: string; path: string; revision: string | null }[] = [];
const api: ReviewsApi = {
  ...server.api,
  async list(request, options) { const reply = await server.api.list(request, options); return { ...reply, viewed: [...viewed] }; },
  async viewed(request) {
    viewedAsked.push({ target: request.target, path: request.path, revision: request.revision });
    const index = viewed.findIndex(entry => entry.target === request.target && entry.path === request.path);
    if (index >= 0) viewed.splice(index, 1);
    if (request.revision !== null) viewed.push({ target: request.target, path: request.path, revision: request.revision });
    return { status: "ok", message: null };
  },
};
const hub = createReviewsHub(api);
hub.connect("epoch");

const state = {
  mode: null as "diff" | "all" | "panel" | null,
  editors: null as DiffEditors | null,
  handle: { current: null } as { current: DiffEditorHandle | null },
  inline: [] as boolean[],
  /** What the review value of the diff fixture was asked: `shown:<id>`, `sideBySide`, `register:<path>`. */
  calls: [] as string[],
  /** The threads the review value gives the diffs. */
  threads: [] as readonly ReviewThread[],
  set: (_: Partial<DiffLook>) => { },
  want: (_: ReviewThread) => { },
};

// One diff and its comments, as the Changes tab shows the selected file.
type DiffLook = { sideBySide: boolean };
function DiffFixture() {
  const [look, setLook] = useState<DiffLook>({ sideBySide: true });
  const [wanted, setWanted] = useState<Readonly<{ thread: ReviewThread }> | null>(null);
  state.set = values => setLook(current => ({ ...current, ...values }));
  state.want = thread => setWanted({ thread });
  const list = useReviewList(hub, scope, changes);
  const actions = useReviewActions(hub, scope);
  state.threads = list.threads;
  const [register] = useState(() => (registered: string) => { state.calls.push(`register:${registered}`); return () => { }; });
  const value = useMemo<ChangesReviewValue>(() => ({
    threads: list.threads, actions, blocked: null, viewed: new Set(), setViewed: null, wanted, register, commentFile: () => { },
    // Written as the Changes tab writes it: through the hub, on the file of the diff.
    add: async (written, side, lines, body, suggestion) =>
      (await hub.add(scope, changes, { path: written, side, startLine: lines.startLine, endLine: lines.endLine, body, suggestion })).ok,
    shown: done => { state.calls.push(`shown:${done.thread.id}`); setWanted(current => current === done ? null : current); },
    sideBySide: () => { state.calls.push("sideBySide"); setLook(current => ({ ...current, sideBySide: true })); },
  }), [list.threads, actions, wanted, register]);
  return <ChangesReviewContext.Provider value={value}><DiffWithComments sideBySide={look.sideBySide} /></ChangesReviewContext.Provider>;
}

function DiffWithComments({ sideBySide }: { sideBySide: boolean }) {
  const [editors, setEditors] = useState<DiffEditors | null>(null);
  const [inline, setInline] = useState(false);
  const handle = useRef<DiffEditorHandle | null>(null);
  state.editors = editors; state.handle = handle;
  const comments = useDiffComments({ editors, inline: inline || !sideBySide, path, unfold: (side, line) => handle.current?.reveal(side, line) });
  return <div style={{ width: 1100 }}>
    <div id="notes">{comments.notes}</div>
    <div id="diff" style={{ width: 1100, height: 800 }}>
      <DiffEditor documentKey="document" original={original} modified={modified} language="plaintext" label={path} sideBySide={sideBySide} wrap={false}
        ignoreWhitespace={false} collapseUnchanged comments onEditors={setEditors} onInline={value => { state.inline.push(value); setInline(value); }} handle={handle} />
    </div>
    {comments.portals}
  </div>;
}

// The view of all files: two files changed alike, whose reads wait until the page lets them go.
const allText = (name: string) => lines(400, line => `${name} line ${line}`);
const allFiles: readonly ChangedFile[] = ["a.txt", "b.txt"].map(name => ({ path: name, originalPath: null, status: "modified", insertions: 3, deletions: 3, binary: false, revision: `r-${name}` }));
const reads = { open: false, waiting: [] as (() => void)[], count: 0 };
const readAll = (file: ChangedFile, signal: AbortSignal) => new Promise<ChangeContent | string>((resolve, reject) => {
  const go = () => {
    if (signal.aborted) { reject(new DOMException("Aborted", "AbortError")); return; }
    reads.count++;
    const before = allText(file.path);
    resolve({ path: file.path, revision: file.revision, original: before, originalState: "text",
      modified: before.replace(/ line 20[123]\n/g, match => match.replace(" line ", " changed ")), modifiedState: "text" });
  };
  if (reads.open) setTimeout(go, 10); else reads.waiting.push(go);
});
const allLook = { sideBySide: true, wrap: false, ignoreWhitespace: false, collapseUnchanged: true };
const nothing = () => { };
const noFolds: ReadonlySet<string> = new Set();
function AllFixture() {
  const list = useReviewList(hub, scope, changes);
  const actions = useReviewActions(hub, scope);
  state.threads = list.threads;
  const [register] = useState(() => () => () => { });
  const value = useMemo<ChangesReviewValue>(() => ({ threads: list.threads, actions, blocked: null, viewed: new Set(), setViewed: null, wanted: null, register,
    commentFile: nothing, add: null, shown: nothing, sideBySide: null }), [list.threads, actions, register]);
  return <ChangesReviewContext.Provider value={value}>
    <div className="changes-diff-surface" style={{ width: 1100, height: 1000 }}>
      <AllChanges files={allFiles} scope="all" visible look={allLook} current="a.txt" reveal={null} collapsed={noFolds} truncated={false} read={readAll}
        openable={() => null} onCurrent={nothing} onToggle={nothing} onOpenFile={nothing} onStale={nothing} />
    </div>
  </ChangesReviewContext.Provider>;
}

// The Changes tab over a played git host: two changed files, a.txt and b.txt, with the texts of the single diff but
// for their first line, which names them.
const panelText = (text: string, file: string) => text.replace("line 1\n", `${file} line 1\n`);
const panelFiles: readonly ChangedFile[] = ["a.txt", "b.txt"].map(name => ({ path: name, originalPath: null, status: "modified", insertions: 4, deletions: 8, binary: false, revision: `rev-${name}` }));
const gitApi = {
  changes: async (request: { comparison?: string }) => ({ status: "ok", projectId: "p", revision: "r1", root: "/repo", prefix: "", branch: "main", detached: false,
    comparison: request.comparison ?? "head", baseReference: null, baseAhead: null, files: panelFiles, insertions: 8, deletions: 16, truncated: false }),
  commits: async () => ({ status: "ok", projectId: "p", revision: "c", commits: [], more: false }),
  file: async (request: { path: string }) => ({ status: "ok", projectId: "p", path: request.path, revision: `rev-${request.path}`, originalState: "text", modifiedState: "text",
    original: panelText(original, request.path), modified: panelText(modified, request.path) }),
};
const checkout: Worktree = { path: "/repo", name: "repo", branch: "main", head: "1234567", main: true, project: true, locked: false, missing: false, folder: "/repo", busy: false };
const trees = {
  list: async () => ({ status: "ok", projectId: "p", worktrees: [checkout], newFolder: null }),
  remove: async () => ({ status: "failed" }), branches: async () => ({ status: "failed", branches: [], busy: false }), switch: async () => ({ status: "failed" }),
};
const tab: FileTab = { projectId: "p", projectPath: "/repo", view: "changes" };
function PanelFixture() {
  return <ReviewsHubContext.Provider value={hub}>
    <div style={{ width: 1180, height: 1000 }}>
      <ProjectChangesPanel tab={tab} projectName="App" epoch="epoch" visible active onActivate={nothing} onOpenFile={nothing} sessions={[]}
        api={gitApi as never} trees={trees as never} />
    </div>
  </ReviewsHubContext.Provider>;
}

const root = createRoot(document.getElementById("root")!);
const editorOf = (side: DiffSide) => side === "old" ? state.editors!.original : state.editors!.modified;
Object.assign(window, { diffReviewFixture: {
  state, server, hub, viewed, viewedAsked, reads, monaco, original, modified,
  /** Shows one fixture, with no thread and nothing viewed; under StrictMode, as the application, unless `strict` is false. */
  async show(mode: "diff" | "all" | "panel", strict = true) {
    flushSync(() => root.render(null));
    state.mode = null; state.editors = null; state.calls.splice(0); state.inline.splice(0); state.threads = [];
    server.threads.splice(0); server.asked.splice(0); viewed.splice(0); viewedAsked.splice(0);
    reads.open = false; reads.waiting.splice(0); reads.count = 0;
    localStorage.clear();
    await hub.refresh();
    const shown = mode === "diff" ? <DiffFixture /> : mode === "all" ? <AllFixture /> : <PanelFixture />;
    flushSync(() => root.render(strict ? <StrictMode>{shown}</StrictMode> : shown));
    state.mode = mode;
  },
  /** A thread written elsewhere (an agent, another window), on lines of a side of a file of the changes; its id. */
  put(thread: { path?: string; side: DiffSide; startLine: number; endLine: number; body?: string }) {
    server.put({ target: "changes", path: thread.path ?? path, side: thread.side, startLine: thread.startLine, endLine: thread.endLine, body: thread.body ?? "" });
    return server.threads.at(-1)!.id;
  },
  /** Asks the diff to show a thread, as a click in the list of comments does. */
  want(id: string) { state.want(state.threads.find(thread => thread.id === id)!); },
  /** Lets the reads of the view of all files go. */
  release() { reads.open = true; for (const go of reads.waiting.splice(0)) go(); },
  /** Whether the diff of the Changes tab shows a file and has compared its texts: a file switched before would log a comparison it lost. */
  compared(file: string) {
    const diff = monaco.editor.getDiffEditors().find(editor => !!document.querySelector(".changes-diff")?.contains(editor.getContainerDomNode()));
    const model = diff?.getModel();
    return !!model && model.modified.getLineContent(1) === `${file} line 1` && diff!.getLineChanges() !== null;
  },
  /** The top of a line of a side in the page, or null when the line is not drawn (hidden, or out of the view). */
  lineTop(side: DiffSide, line: number) {
    const editor = editorOf(side), position = editor.getScrolledVisiblePosition({ lineNumber: line, column: 1 });
    const visible = editor.getVisibleRanges().some(range => range.startLineNumber <= line && line <= range.endLineNumber);
    return position && visible ? Math.round(editor.getDomNode()!.getBoundingClientRect().top + position.top) : null;
  },
  /** Page coordinates of the middle of the glyph margin of a line of a side. */
  glyph(side: DiffSide, line: number) {
    const editor = editorOf(side), position = editor.getScrolledVisiblePosition({ lineNumber: line, column: 1 })!;
    const layout = editor.getLayoutInfo(), box = editor.getDomNode()!.getBoundingClientRect();
    return { x: Math.round(box.left + layout.glyphMarginLeft + layout.glyphMarginWidth / 2), y: Math.round(box.top + position.top + position.height / 2) };
  },
  /** The zone of a thread's card (or of the composer, for null) in a side: its top and height in the page, or null. */
  zone(side: DiffSide, id: string | null) {
    const host = editorOf(side).getDomNode()!;
    const inner = host.querySelector(id === null ? ".review-zone .review-editor" : `.review-zone [data-thread-id="${id}"]`);
    const zone = inner?.closest<HTMLElement>(".review-zone");
    if (!zone || !zone.offsetParent) return null;
    const box = zone.getBoundingClientRect();
    return { top: Math.round(box.top), height: Math.round(box.height) };
  },
  /** The view zones of a side whose node is a comment's: Monaco keeps a zone until it is removed, whatever React shows. */
  commentZones(side: DiffSide) {
    const host = editorOf(side).getDomNode()!;
    return Array.from(host.querySelectorAll<HTMLElement>(".view-zones > .review-zone")).map(node => ({ height: Math.round(node.getBoundingClientRect().height), filled: node.childElementCount > 0 }));
  },
  /** How far apart two lines of a side are, in the content (zones between them included), whether drawn or not. */
  span(side: DiffSide, from: number, to: number) { const editor = editorOf(side); return editor.getTopForLineNumber(to) - editor.getTopForLineNumber(from); },
} });
document.documentElement.classList.add("bp6-dark");
