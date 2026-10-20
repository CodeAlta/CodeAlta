import { createContext, useContext, useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { Button, Checkbox } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import type { DiffEditors, DiffSide } from "../monaco/DiffEditor";
import type { monaco } from "../monaco/monacoEnvironment";
import { useShellLanguage } from "../shellLanguage";
import { orderThreads, threadsOnLines, wholeFile, type ReviewThread } from "./reviewThreads";
import { ReviewCommentEditor, ReviewThreadCard, threadLabel, type ReviewThreadActions } from "./ReviewThreadCard";
import { useLineCommentLayer } from "./useLineCommentLayer";

type Lines = Readonly<{ startLine: number; endLine: number }>;

/** What a file of a diff under review can be asked from the panel: comment at the caret or on the whole file, show a thread. */
export type DiffCommentsEntry = Readonly<{
  /** Opens the editor of a comment on the lines of the caret of the side that has the keyboard; false when no editor has it. */
  comment: (always?: boolean) => boolean;
  commentFile: () => void;
  /** Whether an editor of the file or one of its comments has the keyboard. */
  focused: () => boolean;
}>;

/**
 * The review of the changes the Changes tab shows, for its diffs: the threads of the target, what the cards and the
 * editors of new comments do, the files viewed, and the thread a click in the list asked to see. Null where the tab
 * is not in review mode: the diffs take no comments.
 */
export type ChangesReviewValue = Readonly<{
  threads: readonly ReviewThread[];
  actions: ReviewThreadActions | null;
  /** Writes a comment on lines of a side of a file (0 and 0: the whole file); null where none can be written. */
  add: ((path: string, side: DiffSide, lines: Lines, body: string, suggestion: string | null) => Promise<boolean>) | null;
  /** Why no comment can be written now (a read-only review), or null. */
  blocked: string | null;
  /** The files viewed at the revision they have now, and what marks a file viewed or not. */
  viewed: ReadonlySet<string>;
  setViewed: ((path: string, revision: string, viewed: boolean) => void) | null;
  /** The thread a list or a key asked to see, until a diff shows it; a new object for each request. */
  wanted: Readonly<{ thread: ReviewThread }> | null;
  /** The thread asked for is shown. */
  shown: (wanted: Readonly<{ thread: ReviewThread }>) => void;
  /** Makes the diff show two columns, for the threads of the old side that one column cannot draw. */
  sideBySide: (() => void) | null;
  /** A file's diff is on screen with its comments; the returned function says it left. */
  register: (path: string, entry: DiffCommentsEntry) => () => void;
  /** Opens the editor of a comment on a whole file, in the notes of its diff. */
  commentFile: (path: string) => void;
  /** Scrolls a view of several diffs to the card of a comment it shows; nothing where the diff scrolls by itself. */
  bringIntoView?: (element: HTMLElement) => void;
}>;

export const ChangesReviewContext = createContext<ChangesReviewValue | null>(null);

/**
 * The comments of one file of a diff: a line layer on each side (the old side only side by side, since in one column
 * Monaco gives removed lines no line of their own), or on the one side of a file that is new or gone, and the notes
 * above the diff for what no line holds: comments on the whole file, outdated ones, and those of the old side while
 * the diff is in one column. `unfold` shows a line of a side, unfolding a hidden region, before its thread is shown.
 */
export function useDiffComments({ editors, single = null, inline, path, unfold, review: given }: {
  /** The two editors of the diff; null while it has none or for a file shown alone. */
  editors: DiffEditors | null;
  /** The editor of a file that exists on one side only, and that side. */
  single?: Readonly<{ editor: monaco.editor.ICodeEditor | null; side: DiffSide }> | null;
  inline: boolean;
  path: string;
  unfold?: ((side: DiffSide, line: number) => void) | null;
  /** The review, for a host that provides the context itself; the context's otherwise. */
  review?: ChangesReviewValue | null;
}): Readonly<{ portals: ReactNode; notes: ReactNode; drawn: boolean }> {
  const { t } = useShellLanguage();
  const provided = useContext(ChangesReviewContext);
  const review = given !== undefined ? given : provided;
  const [composingFile, setComposingFile] = useState(false);
  const notesRoot = useRef<HTMLDivElement | null>(null);
  const threads = review?.threads ?? noThreads;
  const oldEditor = single ? single.side === "old" ? single.editor : null : inline ? null : editors?.original ?? null;
  const newEditor = single ? single.side === "new" ? single.editor : null : editors?.modified ?? null;
  const oldThreads = useMemo(() => threadsOnLines(threads, "old", path), [threads, path]);
  const newThreads = useMemo(() => threadsOnLines(threads, "new", path), [threads, path]);
  const add = review?.add ?? null;
  const blocked = review?.blocked ?? null;
  const oldLayer = useLineCommentLayer({ editor: review ? oldEditor : null, path, side: "old", threads: oldThreads, actions: review?.actions ?? null,
    onAdd: add && ((lines, body, suggestion) => add(path, "old", lines, body, suggestion)), blocked });
  const newLayer = useLineCommentLayer({ editor: review ? newEditor : null, path, side: "new", threads: newThreads, actions: review?.actions ?? null,
    onAdd: add && ((lines, body, suggestion) => add(path, "new", lines, body, suggestion)), blocked });
  // What no line of the diff holds: the whole file, the outdated threads, the old side in one column (or of a file shown alone that has only its new side).
  const apart = useMemo(() => orderThreads(threads).filter(thread => thread.path === path && thread.state !== "resolved"
    && (wholeFile(thread) || thread.outdated || thread.side === "old" && !oldEditor || thread.side === "new" && !newEditor && !!single)), [threads, path, oldEditor, newEditor, single]);
  const hiddenOld = apart.filter(thread => thread.side === "old" && !thread.outdated && !wholeFile(thread));

  // The panel reaches the file through its entry while its diff is on screen.
  const latest = useRef({ oldEditor, newEditor, oldLayer, newLayer });
  latest.current = { oldEditor, newEditor, oldLayer, newLayer };
  useEffect(() => {
    if (!review) return;
    return review.register(path, {
      comment: always => {
        const { oldEditor: left, newEditor: right, oldLayer: leftLayer, newLayer: rightLayer } = latest.current;
        if (left?.hasTextFocus()) { leftLayer.comment(); return true; }
        if (right && (always || right.hasTextFocus())) { rightLayer.comment(); return true; }
        if (left && always) { leftLayer.comment(); return true; }
        return false;
      },
      commentFile: () => setComposingFile(true),
      focused: () => !!latest.current.oldEditor?.hasWidgetFocus() || !!latest.current.newEditor?.hasWidgetFocus() || !!notesRoot.current?.contains(document.activeElement),
    });
  }, [review?.register, path]);

  // The thread a list asked for, once this file shows it: its line unfolded, then its card given the keyboard.
  const wanted = review?.wanted ?? null;
  useLayoutEffect(() => {
    if (!review || !wanted || wanted.thread.path !== path) return;
    const thread = wanted.thread;
    if (apart.some(item => item.id === thread.id)) {
      const card = notesRoot.current?.querySelector<HTMLElement>(`[data-thread-id="${CSS.escape(thread.id)}"]`);
      if (!card) return;
      card.scrollIntoView({ block: "nearest" }); card.focus({ preventScroll: true });
      review.shown(wanted);
      return;
    }
    const layer = thread.side === "old" ? oldLayer : newLayer;
    const editor = thread.side === "old" ? oldEditor : newEditor;
    if (!editor?.getModel()) return;
    unfold?.(thread.side, thread.endLine);
    if (!layer.reveal(thread.id)) return;
    review.shown(wanted);
    // The card shows once its zone is drawn: a view of several diffs scrolls to it then.
    const bring = review.bringIntoView;
    if (!bring) return;
    let attempts = 60;
    const find = () => {
      const card = document.querySelector<HTMLElement>(`.review-zone [data-thread-id="${CSS.escape(thread.id)}"]`);
      if (card?.offsetParent) bring(card); else if (--attempts > 0) requestAnimationFrame(find);
    };
    requestAnimationFrame(find);
  });

  const actions = review?.actions ?? null;
  // A comment on the whole file is on the side the file exists on: the old one for a file that is gone.
  const fileSide: DiffSide = single?.side ?? "new";
  const notes = review && (apart.length > 0 || composingFile) ? <div className="review-notes" ref={notesRoot}>
    {composingFile && add && <ReviewCommentEditor label={threadLabel(t, { startLine: 0, endLine: 0 }, path)} lines={{ startLine: 0, endLine: 0 }} original=""
      onCancel={() => setComposingFile(false)}
      onSave={async (body, suggestion) => { const done = await add(path, fileSide, { startLine: 0, endLine: 0 }, body, suggestion); if (done) setComposingFile(false); return done; }} />}
    {apart.filter(thread => !hiddenOld.includes(thread)).map(thread => <ReviewThreadCard key={thread.id} thread={thread} actions={actions} />)}
    {hiddenOld.length > 0 && <section className="review-notes-old" aria-label={t("Comments on the old side")}>
      <header><AppIcon name="columns" size={13} /><span>{t(hiddenOld.length === 1 ? "{count} comment on the old side, not drawn in one column" : "{count} comments on the old side, not drawn in one column",
        { count: hiddenOld.length })}</span>
        {review.sideBySide && !single && <Button size="small" variant="minimal" onClick={review.sideBySide}>{t("Show side by side")}</Button>}</header>
      {hiddenOld.map(thread => <ReviewThreadCard key={thread.id} thread={thread} actions={actions} />)}
    </section>}
  </div> : null;
  return { portals: <>{oldLayer.portals}{newLayer.portals}</>, notes, drawn: !!review };
}

const noThreads: readonly ReviewThread[] = [];

/** The **Viewed** check of a file under review: the revision it was viewed at clears it when the file changes. */
export function ViewedCheck({ path, revision }: { path: string; revision: string }) {
  const { t } = useShellLanguage();
  const review = useContext(ChangesReviewContext);
  if (!review?.setViewed) return null;
  const viewed = review.viewed.has(path);
  return <Checkbox className="review-viewed" checked={viewed} label={t("Viewed")} title={`${t("Viewed")} (v)`}
    onChange={() => review.setViewed?.(path, revision, !viewed)} />;
}

/** **Comment on the file**, in the header of a file under review. */
export function FileCommentButton({ path }: { path: string }) {
  const { t } = useShellLanguage();
  const review = useContext(ChangesReviewContext);
  if (!review?.add) return null;
  return <Button variant="minimal" size="small" icon={<AppIcon name="chat" size={14} />} aria-label={t("Comment on the whole file")} title={t("Comment on the whole file")}
    disabled={!!review.blocked} onClick={() => review.commentFile(path)} />;
}
