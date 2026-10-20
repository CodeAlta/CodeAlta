import { useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode, type RefObject } from "react";
import { Button, Callout, NonIdealState, SegmentedControl } from "@blueprintjs/core";
import { projectFiles, projectGit } from "#neoastra";
import { ActivitySpinner } from "./ActivitySpinner";
import { AppIcon } from "./AppIcon";
import { showToast } from "./appToaster";
import { fileAppearance } from "./fileAppearance";
import { MarkdownContent } from "./MarkdownContent";
import { canSaveFile, fileConflictDismissed, fileEdited, fileLoaded, fileLoading, fileSaved, fileSaveUnknown, fileSaving, initialFileEditorState,
  maximumFileLength } from "./editor/fileEditorState";
import { CodeEditor } from "./monaco/CodeEditor";
import { fileLanguage } from "./monaco/fileLanguage";
import { monaco } from "./monaco/monacoEnvironment";
import { useShellLanguage } from "./shellLanguage";
import { ReviewBar } from "./review/ReviewBar";
import { ReviewThreadCard } from "./review/ReviewThreadCard";
import { textLines, threadsOnLines, wholeFile, type ReviewThread } from "./review/reviewThreads";
import type { ReviewScope, ReviewsHub } from "./review/reviewsHub";
import { useLineCommentLayer } from "./review/useLineCommentLayer";
import { useMarkdownBlockComments } from "./review/useMarkdownBlockComments";
import { useReviewActions, useReviewList, useReviewsHub } from "./review/useReviews";

/** What the request around the review asks of it when the answer is submitted or the request is left. */
export type FileReviewHandle = Readonly<{
  /** The file has edits that were not saved. */
  dirty: () => boolean;
  /** Saves the edits; false when nothing was written. */
  save: () => Promise<boolean>;
  focus: () => void;
}>;

/** Whether a file is shown rendered first: a Markdown document. */
export const readableFile = (path: string) => /\.(md|markdown)$/iu.test(path);

/**
 * The review of one file of a checkout: a Markdown file rendered, with comments on its blocks, and its source one
 * click away; any other text file in the source, with comments on its lines. Both views show the same threads,
 * those of the review store, which follow their text when the file changes. The file of the project folder can be
 * edited and saved (Ctrl+S); while it has unsaved edits nothing is commented, since a comment is placed on the
 * text on disk. A file of a worktree is shown as git reads it, without editing.
 */
export function FileReview({ epoch, scope, path, disabled = false, handle, onLeave, submit, api = projectFiles, git = projectGit, hub: given }: {
  epoch: string;
  /** The checkout of the file; null where the session has no project. */
  scope: ReviewScope | null;
  /** The file, relative to the checkout. */
  path: string;
  /** The review cannot be changed right now: nothing is editable. */
  disabled?: boolean;
  handle?: RefObject<FileReviewHandle | null>;
  /** Escape in the source or the document: back to where the user came from. */
  onLeave?: () => void;
  /** **Submit review ▾**, or nothing when the review is submitted elsewhere (the form of a request). */
  submit?: ReactNode;
  api?: Pick<typeof projectFiles, "read" | "write">;
  git?: Pick<typeof projectGit, "file">;
  /** The reviews hub; the page's by default. */
  hub?: ReviewsHub | null;
}) {
  const { t } = useShellLanguage();
  const pageHub = useReviewsHub();
  const hub = given === undefined ? pageHub : given;
  const [state, setState] = useState(initialFileEditorState);
  const [generation, setGeneration] = useState(0);
  const readable = readableFile(path);
  const [view, setView] = useState<"read" | "source">(readable ? "read" : "source");
  const reading = readable && view === "read";
  const [editor, setEditor] = useState<monaco.editor.ICodeEditor | null>(null);
  const [document, setDocument] = useState<HTMLDivElement | null>(null);
  const root = useRef<HTMLElement>(null);
  const writing = useRef(false);
  const alive = useRef(true);
  const checkout = scope?.checkout ?? null;
  // A worktree's file is read through git: the files of the window name the project folder only.
  const fromGit = checkout !== null;
  const latest = useRef({ state, epoch, disabled, onLeave });
  latest.current = { state, epoch, disabled, onLeave };
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);

  const target = useMemo(() => ({ kind: "file" as const, path }), [path]);
  const list = useReviewList(hub, scope, target);
  const actions = useReviewActions(hub, scope, disabled || !!list.readOnly);
  const drawn = useMemo(() => threadsOnLines(list.threads, "new", path), [list.threads, path]);
  const apart = useMemo(() => list.threads.filter(thread => thread.state !== "resolved" && (thread.outdated || wholeFile(thread))), [list.threads]);

  // Read when the review opens and on an explicit reload.
  useEffect(() => {
    if (!scope) { setState(fileLoaded({ status: "unknown_project", content: null, revision: null, readOnly: false })); return; }
    const controller = new AbortController();
    setState(fileLoading);
    const read = fromGit
      ? git.file({ expectedEpoch: epoch, projectId: scope.projectId, worktree: checkout, path, comparison: null, commit: null }, { signal: controller.signal, timeoutMilliseconds: 15000 })
        .then(value => ({ status: value.status === "ok" && value.modified === null ? "not_found" : value.status, content: value.modified, revision: value.revision ?? "git", readOnly: true }))
      : api.read({ expectedEpoch: epoch, projectId: scope.projectId, path, reload: false }, { signal: controller.signal, timeoutMilliseconds: 15000 });
    void read.then(value => { if (!controller.signal.aborted) setState(fileLoaded(value)); })
      .catch(() => { if (!controller.signal.aborted) setState(fileLoaded({ status: "read_failed", content: null, revision: null, readOnly: false })); });
    return () => controller.abort();
  }, [api, git, epoch, scope?.projectId, checkout, path, generation, fromGit]);

  async function save(overwrite = false): Promise<boolean> {
    const { state: current, epoch: hostEpoch } = latest.current;
    if (writing.current || !scope || fromGit || !current.baseline) return false;
    if (current.content.length > maximumFileLength) { setState(value => fileSaved(value, "", { status: "too_large", revision: null })); return false; }
    if (!canSaveFile(current, overwrite)) return false;
    const submitted = current.content;
    writing.current = true;
    setState(fileSaving);
    try {
      const result = await api.write({ expectedEpoch: hostEpoch, projectId: scope.projectId, path, content: submitted, expectedRevision: current.baseline.revision, overwrite },
        { timeoutMilliseconds: 30000 });
      if (!alive.current) return false;
      setState(value => fileSaved(value, submitted, result));
      const written = result.status === "ok" && !!result.revision;
      // The threads are placed again on the text now on disk.
      if (written) void hub?.refresh();
      return written;
    } catch {
      if (alive.current) setState(fileSaveUnknown);
      return false;
    } finally { writing.current = false; }
  }
  const saveLatest = useRef(save); saveLatest.current = save;

  const ready = state.phase === "ready";
  const editable = !fromGit && !disabled && !state.readOnly && !state.saving && !state.loading;
  const blocked = !scope || !hub ? t("Comments need the review service of the host.") : list.readOnly ? list.readOnly
    : state.dirty ? t("Save the file to comment on it.") : disabled ? t("The review cannot be changed right now.") : null;
  async function add(lines: { startLine: number; endLine: number }, body: string, suggestion: string | null) {
    if (!hub || !scope) return false;
    const done = await hub.add(scope, target, { side: "new", startLine: lines.startLine, endLine: lines.endLine, body, suggestion });
    if (!done.ok && done.message) showToast({ message: done.message, intent: "warning", icon: "warning-sign", timeout: 6000 });
    return done.ok;
  }
  const onAdd = hub && scope ? add : null;
  const lineLayer = useLineCommentLayer({ editor: reading ? null : editor, path, threads: drawn, actions, onAdd, blocked, keys: !editable, modelKey: generation });
  const blockLayer = useMarkdownBlockComments({ container: reading ? document : null, source: state.content, path, threads: drawn, actions, onAdd, blocked });

  // The keys of the source: Ctrl+S saves, Ctrl+K comments the line of the caret, Ctrl+N and Ctrl+P go from thread to
  // thread, Escape goes back. Handled on this instance: a command binding is shared by every editor on the page.
  const layerLatest = useRef(lineLayer); layerLatest.current = lineLayer;
  useEffect(() => {
    if (!editor) return;
    const node = editor.getDomNode();
    const keys = editor.onKeyDown(event => {
      const plain = (event.ctrlKey || event.metaKey) && !event.altKey && !event.shiftKey;
      if (plain && event.keyCode === monaco.KeyCode.KeyK) layerLatest.current.comment();
      else if (plain && (event.keyCode === monaco.KeyCode.KeyN || event.keyCode === monaco.KeyCode.KeyP)) layerLatest.current.step(event.keyCode === monaco.KeyCode.KeyN ? 1 : -1);
      else if (event.keyCode === monaco.KeyCode.Escape && !event.ctrlKey && !event.altKey && !event.shiftKey && latest.current.onLeave
        // Escape first closes what the editor has open (find, a selection of several cursors).
        && !node?.querySelector(".find-widget.visible") && (editor.getSelections()?.length ?? 1) === 1) latest.current.onLeave();
      else return;
      event.preventDefault(); event.stopPropagation();
    });
    return () => keys.dispose();
  }, [editor]);

  if (handle) handle.current = {
    dirty: () => latest.current.state.dirty,
    save: () => saveLatest.current(),
    focus: () => { if (reading) document?.focus(); else editor?.focus(); },
  };
  useEffect(() => () => { if (handle) handle.current = null; }, [handle]);
  // "Go to Ask File" (Ctrl+G Ctrl+E) reaches the review through its element.
  useEffect(() => {
    const node = root.current;
    if (!node) return;
    const focus = () => { if (reading) document?.focus(); else editor?.focus(); };
    node.addEventListener("codealta-ask-file-focus", focus);
    return () => node.removeEventListener("codealta-ask-file-focus", focus);
  }, [editor, reading, document]);
  // The source was hidden while the document was read: it takes its place again, and the keyboard.
  useLayoutEffect(() => { if (!reading && editor) { editor.layout(); editor.focus(); } }, [reading, editor]);

  function reveal(thread: ReviewThread) {
    if (thread.outdated || wholeFile(thread)) {
      root.current?.querySelector<HTMLElement>(`.file-review-apart [data-thread-id="${CSS.escape(thread.id)}"]`)?.focus();
      return;
    }
    if (!(reading ? blockLayer.reveal(thread.id) : lineLayer.reveal(thread.id))) showToast({ message: t("This comment is not on a line of the file."), timeout: 4000 });
  }

  const look = fileAppearance(path, false);
  const busy = state.saving || state.loading;
  const name = path.slice(path.lastIndexOf("/") + 1);
  return <section className="ask-file-review file-review" ref={root} aria-label={t("File context: {path}", { path })} data-ask-keys="">
    <header className="ask-file-header">
      <span className="file-tab-icon" data-file-tone={look.tone}><AppIcon name={look.icon} size={14} /></span>
      <strong title={path}>{path}{state.dirty ? " *" : ""}</strong>
      {busy && <ActivitySpinner size={12} />}
      {readable && <SegmentedControl className="ask-file-view" size="small" value={view} onValueChange={value => setView(value as "read" | "source")}
        options={[{ value: "read", label: t("Rendered") }, { value: "source", label: t("Source") }]} />}
      <Button size="small" variant="minimal" icon={<AppIcon name="chat" size={14} />} disabled={!!blocked || !ready} title={reading ? t("Comment on the selected blocks (c)") : `${t("Add line comment")} (Ctrl+K)`}
        onClick={() => { if (reading) blockLayer.comment(); else lineLayer.comment(); }}>{t("Comment")}</Button>
      {!fromGit && <Button size="small" variant="minimal" icon={<AppIcon name="save" size={14} />} disabled={disabled || !canSaveFile(state)}
        title={`${t("Save")} (Ctrl+S)`} onClick={() => void save()}>{t("Save")}</Button>}
      <Button size="small" variant="minimal" icon={<AppIcon name="refresh" size={14} />} disabled={busy || state.dirty} aria-label={t("Reload")} title={t("Reload")}
        onClick={() => setGeneration(value => value + 1)} />
    </header>
    <ReviewBar projectId={scope?.projectId ?? null} title={<span className="review-bar-file" title={path}>{name}</span>} threads={list.threads} readOnly={list.readOnly}
      notice={state.dirty ? t("Save the file to comment on it.") : null}
      onResolveAddressed={hub && scope && !disabled ? async () => (await hub.resolveAddressed(scope, target)).ok : null} onReveal={reveal} submit={submit} />
    {state.conflict && <Callout className="file-editor-notice" intent="warning" compact role="alert">
      <span>{t("The file changed on disk since it was opened.")}</span>
      <span className="file-editor-notice-actions">
        <Button size="small" disabled={busy} onClick={() => setGeneration(value => value + 1)}>{t("Reload")}</Button>
        <Button size="small" intent="warning" disabled={!canSaveFile(state, true)} onClick={() => void save(true)}>{t("Overwrite")}</Button>
        <Button size="small" variant="minimal" disabled={busy} onClick={() => setState(fileConflictDismissed)}>{t("Cancel")}</Button>
      </span>
    </Callout>}
    {!state.conflict && state.notice && <Callout className="file-editor-notice" intent={state.notice.intent} compact role="alert">{t(state.notice.key)}</Callout>}
    {apart.length > 0 && <details className="file-review-apart" open>
      <summary>{t("Comments not on a line ({count})", { count: apart.length })}</summary>
      {apart.map(thread => <ReviewThreadCard key={thread.id} thread={thread} actions={actions} original={textLines(state.content, thread.startLine, thread.endLine)} />)}
    </details>}
    {ready && reading && <div className="ask-file-reader review-document" ref={setDocument} tabIndex={0} onKeyDown={event => {
      if (event.target === event.currentTarget && event.key === "Escape" && !event.ctrlKey && !event.altKey && !event.shiftKey && onLeave) { event.preventDefault(); onLeave(); }
    }}>{blockLayer.gutter}<MarkdownContent source={state.content} document sourceLines /></div>}
    {ready ? <div className="ask-file-surface" hidden={reading}>
        <CodeEditor value={state.content} language={fileLanguage(path)} label={path} readOnly={!editable} wrap comments onEditor={setEditor}
          onChange={text => setState(value => fileEdited(value, text))} onSave={() => void save()} />
      </div>
      : state.phase === "loading" ? <NonIdealState className="file-editor-empty" icon={<ActivitySpinner size={28} />} title={t("Loading…")} />
      : <NonIdealState className="file-editor-empty" icon={<AppIcon name={look.icon} size={36} />} title={path}
        description={t(state.failure ?? "The file could not be read.")}
        action={<Button icon={<AppIcon name="refresh" size={15} />} onClick={() => setGeneration(value => value + 1)}>{t("Reload")}</Button>} />}
    {lineLayer.portals}
    {blockLayer.portals}
  </section>;
}
