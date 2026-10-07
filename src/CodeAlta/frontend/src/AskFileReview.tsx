import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent as ReactKeyboardEvent, type RefObject } from "react";
import { createPortal } from "react-dom";
import { Button, Callout, Classes, NonIdealState, SegmentedControl, TextArea } from "@blueprintjs/core";
import { projectFiles } from "#neoastra";
import { ActivitySpinner } from "./ActivitySpinner";
import { AppIcon } from "./AppIcon";
import { addComment, commentsFit, editComment, finishComment, moveComments, neighbourComment, orderedComments, reviewSnapshot, type ReviewComment } from "./askReview";
import { fileAppearance } from "./fileAppearance";
import { MarkdownContent } from "./MarkdownContent";
import { canSaveFile, fileConflictDismissed, fileEdited, fileLoaded, fileLoading, fileSaved, fileSaveUnknown, fileSaving, initialFileEditorState,
  maximumFileLength } from "./editor/fileEditorState";
import { fileLanguage } from "./monaco/fileLanguage";
import { followShellTheme, monaco } from "./monaco/monacoEnvironment";
import { ensureMonacoLanguage } from "./monaco/monacoLanguages";
import { maximumFileCommentLength, type AskFileReview as AskFileReviewAnswer } from "./sessionAsks";
import { useShellLanguage } from "./shellLanguage";

/** What the ask around the review asks of it when the answer is submitted or the ask is left. */
export type AskFileReviewHandle = Readonly<{
  /** The line comments and whether the file was edited and saved, as sent with the answer. */
  review: () => AskFileReviewAnswer;
  /** The file has edits that were not saved. */
  dirty: () => boolean;
  /** The comments are too long to be sent in one answer. */
  fits: () => boolean;
  /** Saves the edits; false when nothing was written. */
  save: () => Promise<boolean>;
  focus: () => void;
}>;

type Zone = { zoneId: string | null; zone: monaco.editor.IViewZone; decoration: string | null };

/**
 * The file an ask gives for review (a plan, usually), in place of the timeline while the ask is open. A
 * Markdown file is first shown as a reader sees it; its source is one click away. In the source the user
 * comments on lines (Ctrl+K, or the margin beside a line), each comment being a box under its line, and can
 * edit and save the file (Ctrl+S). Comments stay on their lines while the file is edited.
 */
export function AskFileReview({ epoch, projectId, path, disabled = false, handle, onLeave, api = projectFiles }: {
  epoch: string; projectId: string | null;
  /** The file, relative to the session's folder. */
  path: string;
  /** The ask cannot be answered right now: nothing is editable. */
  disabled?: boolean;
  handle: RefObject<AskFileReviewHandle | null>;
  /** Escape in the editor: back to the questions. */
  onLeave?: () => void;
  api?: Pick<typeof projectFiles, "read" | "write">;
}) {
  const { t } = useShellLanguage();
  const [state, setState] = useState(initialFileEditorState);
  const [generation, setGeneration] = useState(0);
  const [comments, setComments] = useState<readonly ReviewComment[]>([]);
  const [saved, setSaved] = useState(false);
  const [instance, setInstance] = useState<monaco.editor.IStandaloneCodeEditor | null>(null);
  // A Markdown file is read before it is commented: the source is where the comments go.
  const readable = /\.(md|markdown)$/i.test(path);
  const [view, setView] = useState<"read" | "source">(readable ? "read" : "source");
  const reading = readable && view === "read";
  const reader = useRef<HTMLDivElement>(null);
  const host = useRef<HTMLDivElement>(null);
  const root = useRef<HTMLElement>(null);
  const nextId = useRef(0);
  const nodes = useRef(new Map<number, HTMLDivElement>());
  const zones = useRef(new Map<number, Zone>());
  const boxes = useRef(new Map<number, HTMLTextAreaElement>());
  const writing = useRef(false);
  const alive = useRef(true);
  const latest = useRef({ state, comments, saved, epoch, disabled, onLeave });
  latest.current = { state, comments, saved, epoch, disabled, onLeave };
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);

  // Read when the review opens and on an explicit reload.
  useEffect(() => {
    if (projectId === null) { setState(fileLoaded({ status: "unknown_project", content: null, revision: null, readOnly: false })); return; }
    const controller = new AbortController();
    setState(fileLoading);
    void api.read({ expectedEpoch: epoch, projectId, path, reload: false }, { signal: controller.signal, timeoutMilliseconds: 15000 }).then(value => {
      if (!controller.signal.aborted) setState(fileLoaded(value));
    }).catch(() => { if (!controller.signal.aborted) setState(fileLoaded({ status: "read_failed", content: null, revision: null, readOnly: false })); });
    return () => controller.abort();
  }, [api, epoch, projectId, path, generation]);

  async function save(overwrite = false): Promise<boolean> {
    const { state: current, epoch: hostEpoch } = latest.current;
    if (writing.current || projectId === null || !current.baseline) return false;
    if (current.content.length > maximumFileLength) { setState(value => fileSaved(value, "", { status: "too_large", revision: null })); return false; }
    if (!canSaveFile(current, overwrite)) return false;
    const submitted = current.content;
    writing.current = true;
    setState(fileSaving);
    try {
      const result = await api.write({ expectedEpoch: hostEpoch, projectId, path, content: submitted, expectedRevision: current.baseline.revision, overwrite },
        { timeoutMilliseconds: 30000 });
      if (!alive.current) return false;
      setState(value => fileSaved(value, submitted, result));
      const written = result.status === "ok" && !!result.revision;
      if (written) setSaved(true);
      return written;
    } catch {
      if (alive.current) setState(fileSaveUnknown);
      return false;
    } finally { writing.current = false; }
  }
  const saveLatest = useRef(save); saveLatest.current = save;

  const ready = state.phase === "ready";
  const language = fileLanguage(path);
  // The editor exists while the file is shown; its text is the state's, its comments are view zones.
  useLayoutEffect(() => {
    const node = host.current;
    if (!ready || !node) return;
    const model = monaco.editor.createModel(latest.current.state.content,
      monaco.languages.getLanguages().some(known => known.id === language) ? language : "plaintext");
    const editor = monaco.editor.create(node, { model, automaticLayout: true, ariaLabel: path, readOnly: latest.current.disabled,
      fontFamily: getComputedStyle(node).fontFamily, fontSize: 13, lineHeight: 20, minimap: { enabled: false }, glyphMargin: true,
      scrollBeyondLastLine: false, wordWrap: "on", renderLineHighlight: "line", stickyScroll: { enabled: false },
      padding: { top: 8, bottom: 8 }, quickSuggestions: false, suggestOnTriggerCharacters: false, links: false, tabSize: 2,
      folding: false, occurrencesHighlight: "off", matchBrackets: "never" });
    let disposed = false;
    void ensureMonacoLanguage(language).then(registered => { if (registered && !disposed) monaco.editor.setModelLanguage(model, language); });
    const unfollowTheme = followShellTheme();
    const changed = model.onDidChangeContent(() => {
      const text = model.getValue();
      setState(value => fileEdited(value, text));
      setComments(current => moveComments(current, id => {
        const decoration = zones.current.get(id)?.decoration;
        return decoration ? model.getDecorationRange(decoration)?.startLineNumber ?? null : null;
      }));
    });
    // Handled on this instance: a command binding is shared by every editor on the page.
    const keys = editor.onKeyDown(event => {
      const plain = (event.ctrlKey || event.metaKey) && !event.altKey && !event.shiftKey;
      const line = editor.getPosition()?.lineNumber ?? 1;
      if (plain && event.keyCode === monaco.KeyCode.KeyS) void saveLatest.current();
      else if (plain && event.keyCode === monaco.KeyCode.KeyK) commentOn(line);
      else if (plain && (event.keyCode === monaco.KeyCode.KeyN || event.keyCode === monaco.KeyCode.KeyP))
        visit(neighbourComment(latest.current.comments, { line }, event.keyCode === monaco.KeyCode.KeyN ? 1 : -1));
      else if (event.keyCode === monaco.KeyCode.Escape && !event.ctrlKey && !event.altKey && !event.shiftKey && latest.current.onLeave
        // Escape first closes what the editor has open (find, a selection of several cursors).
        && !node.querySelector(".find-widget.visible") && (editor.getSelections()?.length ?? 1) === 1) latest.current.onLeave();
      else return;
      event.preventDefault(); event.stopPropagation();
    });
    // The margin beside a line adds a comment on it, or goes to the one it has.
    const mouse = editor.onMouseDown(event => {
      if (event.target.type !== monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN || !event.target.position || !event.event.leftButton) return;
      commentOn(event.target.position.lineNumber);
    });
    let hover: string[] = [];
    const moved = editor.onMouseMove(event => {
      const line = event.target.type === monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN && !latest.current.disabled ? event.target.position?.lineNumber : undefined;
      hover = model.deltaDecorations(hover, line && !latest.current.comments.some(comment => comment.line === line)
        ? [{ range: new monaco.Range(line, 1, line, 1), options: { glyphMarginClassName: "ask-comment-add" } }] : []);
    });
    const left = editor.onMouseLeave(() => { hover = model.deltaDecorations(hover, []); });
    setInstance(editor);
    return () => {
      disposed = true;
      setInstance(null);
      zones.current.clear();
      unfollowTheme(); changed.dispose(); keys.dispose(); mouse.dispose(); moved.dispose(); left.dispose(); editor.dispose(); model.dispose();
    };
  }, [ready, language]);

  // A reload replaces the text; ordinary typing already matches.
  useLayoutEffect(() => {
    if (!instance) return;
    instance.updateOptions({ readOnly: disabled || state.readOnly || state.saving || state.loading });
    if (instance.getValue() !== state.content) instance.getModel()!.setValue(state.content);
  }, [instance, state.content, state.readOnly, state.saving, state.loading, disabled]);

  // One view zone and one margin marker per comment, at the line the comment is on.
  useLayoutEffect(() => {
    if (!instance) return;
    const model = instance.getModel()!;
    instance.changeViewZones(accessor => {
      for (const [id, zone] of zones.current) {
        if (comments.some(comment => comment.id === id)) continue;
        if (zone.zoneId) accessor.removeZone(zone.zoneId);
        if (zone.decoration) model.deltaDecorations([zone.decoration], []);
        zones.current.delete(id); nodes.current.delete(id); boxes.current.delete(id);
      }
      for (const comment of comments) {
        const node = nodes.current.get(comment.id);
        if (!node) continue;
        const line = Math.min(Math.max(comment.line, 1), model.getLineCount());
        let zone = zones.current.get(comment.id);
        if (!zone) {
          zone = { zoneId: null, decoration: null, zone: { afterLineNumber: line, heightInPx: Math.max(node.offsetHeight, 96), domNode: node } };
          zone.decoration = model.deltaDecorations([], [{ range: new monaco.Range(line, 1, line, 1), options: { isWholeLine: true,
            glyphMarginClassName: "ask-comment-glyph", className: "ask-comment-line",
            stickiness: monaco.editor.TrackedRangeStickiness.NeverGrowsWhenTypingAtEdges } }])[0];
          zone.zoneId = accessor.addZone(zone.zone);
          zones.current.set(comment.id, zone);
        } else if (zone.zone.afterLineNumber !== line && zone.zoneId) {
          zone.zone.afterLineNumber = line;
          accessor.layoutZone(zone.zoneId);
        }
      }
    });
  }, [instance, comments]);

  // A comment box grows with its text; its zone follows.
  function sized(id: number, height: number) {
    const zone = zones.current.get(id);
    if (!instance || !zone?.zoneId || zone.zone.heightInPx === height) return;
    zone.zone.heightInPx = height;
    instance.changeViewZones(accessor => accessor.layoutZone(zone.zoneId!));
  }

  function commentOn(line: number) {
    if (latest.current.disabled) return;
    setView("source");
    const id = ++nextId.current;
    const added = addComment(latest.current.comments, line, id);
    if (!added) return;
    if (added.id === id) {
      const node = document.createElement("div");
      node.className = "ask-comment-zone";
      // Above the text layer, so the box takes the pointer; the editor leaves a zone's mouse events alone.
      node.style.zIndex = "10";
      nodes.current.set(id, node);
      setComments(added.comments);
    }
    focusComment(added.id);
  }
  // A new comment's box is in the page only once the editor has laid out its zone, a frame or two later.
  const wanted = useRef<number | null>(null);
  function focusComment(id: number, attempts = 12) {
    wanted.current = id;
    const box = boxes.current.get(id);
    if (box && box.offsetParent !== null) {
      wanted.current = null;
      const comment = latest.current.comments.find(item => item.id === id);
      if (comment) instance?.revealLineInCenterIfOutsideViewport(comment.line);
      box.focus({ preventScroll: true });
    } else if (attempts > 0) requestAnimationFrame(() => { if (alive.current && wanted.current === id) focusComment(id, attempts - 1); });
  }
  function visit(comment: ReviewComment | null) { if (comment) focusComment(comment.id); }
  function leaveComment(comment: ReviewComment, remove: boolean) {
    setComments(current => remove ? current.filter(item => item.id !== comment.id) : finishComment(current, comment.id));
    if (!instance) return;
    const line = Math.min(comment.line + (remove ? 0 : 1), instance.getModel()!.getLineCount());
    instance.setPosition({ lineNumber: line, column: 1 });
    instance.focus();
  }
  function commentKey(event: ReactKeyboardEvent<HTMLTextAreaElement>, comment: ReviewComment) {
    if (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229) return;
    const plain = event.ctrlKey && !event.altKey && !event.shiftKey && !event.metaKey;
    const key = event.key.toLowerCase();
    if (event.key === "Escape" && !event.ctrlKey && !event.altKey && !event.shiftKey) leaveComment(comment, false);
    else if (plain && key === "d") leaveComment(comment, true);
    else if (plain && (key === "n" || key === "p")) visit(neighbourComment(latest.current.comments, { line: comment.line, id: comment.id }, key === "n" ? 1 : -1));
    else if (plain && key === "s") void save();
    else return;
    event.preventDefault(); event.stopPropagation();
  }

  if (handle) handle.current = {
    review: () => reviewSnapshot(latest.current.comments, latest.current.saved),
    dirty: () => latest.current.state.dirty,
    fits: () => commentsFit(latest.current.comments),
    save: () => saveLatest.current(),
    focus: () => { if (reading) reader.current?.focus(); else instance?.focus(); },
  };
  useEffect(() => () => { if (handle) handle.current = null; }, [handle]);
  // "Go to Ask File" (Ctrl+G Ctrl+E) reaches the editor through its element.
  useEffect(() => {
    const node = root.current;
    if (!node) return;
    const focus = () => { if (reading) reader.current?.focus(); else instance?.focus(); };
    node.addEventListener("codealta-ask-file-focus", focus);
    return () => node.removeEventListener("codealta-ask-file-focus", focus);
  }, [instance, reading]);
  // The editor was hidden while the file was read: it takes its place again, and the keyboard.
  useEffect(() => { if (!reading && instance) { instance.layout(); instance.focus(); } }, [reading, instance]);

  const look = fileAppearance(path, false);
  const written = comments.filter(comment => comment.text.trim()).length;
  const busy = state.saving || state.loading;
  return <section className="ask-file-review" ref={root} aria-label={t("File context: {path}", { path })} data-ask-keys="">
    <header className="ask-file-header">
      <span className="file-tab-icon" data-file-tone={look.tone}><AppIcon name={look.icon} size={14} /></span>
      <strong title={path}>{t("File context: {path}", { path })}{state.dirty ? " *" : ""}</strong>
      {busy && <ActivitySpinner size={12} />}
      {readable && <SegmentedControl className="ask-file-view" size="small" value={view} onValueChange={value => setView(value as "read" | "source")}
        options={[{ value: "read", label: t("Read") }, { value: "source", label: t("Source and comments") }]} />}
      <span className="ask-file-count">{t(written === 1 ? "{count} comment" : "{count} comments", { count: written })}</span>
      <Button size="small" variant="minimal" icon={<AppIcon name="notes" size={14} />} disabled={!instance || disabled} title={`${t("Add line comment")} (Ctrl+K)`}
        onClick={() => commentOn(instance?.getPosition()?.lineNumber ?? 1)}>{t("Comment")}</Button>
      <Button size="small" variant="minimal" icon={<AppIcon name="trash" size={14} />} disabled={comments.length === 0 || disabled}
        onClick={() => { setComments([]); instance?.focus(); }}>{t("Clear comments")}</Button>
      <Button size="small" variant="minimal" icon={<AppIcon name="save" size={14} />} disabled={disabled || !canSaveFile(state)}
        title={`${t("Save")} (Ctrl+S)`} onClick={() => void save()}>{t("Save")}</Button>
    </header>
    {state.conflict && <Callout className="file-editor-notice" intent="warning" compact role="alert">
      <span>{t("The file changed on disk since it was opened.")}</span>
      <span className="file-editor-notice-actions">
        <Button size="small" disabled={busy} onClick={() => { setComments([]); setGeneration(value => value + 1); }}>{t("Reload")}</Button>
        <Button size="small" intent="warning" disabled={!canSaveFile(state, true)} onClick={() => void save(true)}>{t("Overwrite")}</Button>
        <Button size="small" variant="minimal" disabled={busy} onClick={() => setState(fileConflictDismissed)}>{t("Cancel")}</Button>
      </span>
    </Callout>}
    {!state.conflict && state.notice && <Callout className="file-editor-notice" intent={state.notice.intent} compact role="alert">{t(state.notice.key)}</Callout>}
    {!commentsFit(comments) && <Callout className="file-editor-notice" intent="warning" compact role="alert">{t("The comments are too long to send in one answer.")}</Callout>}
    {ready && reading && <div className="ask-file-reader" ref={reader} tabIndex={0} onKeyDown={event => {
      if (event.key === "Escape" && !event.ctrlKey && !event.altKey && !event.shiftKey && onLeave) { event.preventDefault(); onLeave(); }
    }}><div className="markdown-content"><MarkdownContent source={state.content} document /></div></div>}
    {ready ? <div className={`ask-file-surface code-editor ${Classes.MONOSPACE_TEXT}`} ref={host} hidden={reading} />
      : state.phase === "loading" ? <NonIdealState className="file-editor-empty" icon={<ActivitySpinner size={28} />} title={t("Loading…")} />
      : <NonIdealState className="file-editor-empty" icon={<AppIcon name={look.icon} size={36} />} title={path}
        description={t(state.failure ?? "The file could not be read.")}
        action={<Button icon={<AppIcon name="refresh" size={15} />} onClick={() => setGeneration(value => value + 1)}>{t("Reload")}</Button>} />}
    {orderedComments(comments).map(comment => {
      const node = nodes.current.get(comment.id);
      return node ? createPortal(<CommentBox key={comment.id} comment={comment} disabled={disabled}
        register={box => { if (box) boxes.current.set(comment.id, box); else boxes.current.delete(comment.id); }}
        onSize={height => sized(comment.id, height)} onChange={text => setComments(current => editComment(current, comment.id, text))}
        onKeyDown={event => commentKey(event, comment)} onDelete={() => leaveComment(comment, true)} />, node, String(comment.id)) : null;
    })}
  </section>;
}

function CommentBox({ comment, disabled, register, onSize, onChange, onKeyDown, onDelete }: {
  comment: ReviewComment; disabled: boolean; register: (box: HTMLTextAreaElement | null) => void; onSize: (height: number) => void;
  onChange: (text: string) => void; onKeyDown: (event: ReactKeyboardEvent<HTMLTextAreaElement>) => void; onDelete: () => void;
}) {
  const { t } = useShellLanguage();
  const box = useRef<HTMLDivElement>(null);
  const size = useRef(onSize); size.current = onSize;
  useLayoutEffect(() => {
    const node = box.current;
    if (!node) return;
    const measure = () => size.current(node.offsetHeight + 8);
    const observer = new ResizeObserver(measure);
    observer.observe(node);
    measure();
    return () => observer.disconnect();
  }, []);
  return <div className="ask-comment" ref={box} data-done={comment.done}>
    <header>
      <AppIcon name="user" size={13} />
      <strong>{t("User Comment")}</strong><span>{t("line {line}", { line: comment.line })}</span>
      {comment.done && <span className="ask-comment-done" title={t("Done")}><AppIcon name="check" size={13} /></span>}
      <Button size="small" variant="minimal" icon={<AppIcon name="trash" size={13} />} disabled={disabled} title={`${t("Delete")} (Ctrl+D)`} aria-label={t("Delete")} onClick={onDelete} />
    </header>
    <TextArea fill autoResize inputRef={register} maxLength={maximumFileCommentLength} disabled={disabled} value={comment.text}
      aria-label={t("Comment on line {line}", { line: comment.line })} placeholder={t("Enter a comment… Esc done · Ctrl+D delete")}
      onChange={event => onChange(event.target.value)} onKeyDown={onKeyDown} />
  </div>;
}
