import { memo, useContext, useEffect, useLayoutEffect, useRef, useState, type RefObject } from "react";
import { Button } from "@blueprintjs/core";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { CodeEditor } from "../monaco/CodeEditor";
import { DiffEditor, type DiffEditorHandle, type DiffEditors } from "../monaco/DiffEditor";
import type { monaco } from "../monaco/monacoEnvironment";
import { reviewHeightEstimate } from "../review/changesReview";
import { ChangesReviewContext, FileCommentButton, useDiffComments, ViewedCheck } from "../review/useDiffComments";
import { fileAppearance } from "../fileAppearance";
import { fileLanguage } from "../monaco/fileLanguage";
import { useShellLanguage } from "../shellLanguage";
import { ExternalEditorIcon, type ExternalEditorInfo } from "../externalEditors/externalEditors";
import { ChangeBar, Counts } from "./ChangeCounts";
import { changeContentNotice, changeFileName, changeFolder, changeLabel, changeLetter, estimatedDiffHeight, fittedDiffHeight, sectionAt,
  type ChangeContent, type ChangedFile, type ChangesPreferences } from "./projectChanges";

const ignore = () => { };
// The files whose diff is read at the same time: the others wait for one of them.
const readsAtOnce = 4;
// How long the view holds the file it was asked to show while the diffs around it take their size.
const holdMilliseconds = 2000;
const sectionHeaderHeight = 34;

/** What the panel asks of the view of all files. */
export type AllChangesHandle = Readonly<{ focus: () => void; show: (element: HTMLElement) => void }>;

/** Reads both sides of a file: the content, or the status of a failed read. */
export type ChangeReader = (file: ChangedFile, signal: AbortSignal) => Promise<ChangeContent | string>;
type DiffLook = Pick<ChangesPreferences, "sideBySide" | "wrap" | "ignoreWhitespace" | "collapseUnchanged">;
/** The external editor, and what opens a file of the changes in it, by the path the code editor opens. */
export type ExternalFileOpener = Readonly<{ editor: ExternalEditorInfo; open: (path: string) => void }>;

const Section = memo(function Section({ file, scope, near, visible, collapsed, viewport, look, openable, external, read, onToggle, onOpenFile, onStale, onResized }: {
  file: ChangedFile; scope: string;
  /** The section is in the view or close to it: its diff is read and shown. */
  near: boolean; visible: boolean; collapsed: boolean;
  /** The height the view shows at once, in pixels. */
  viewport: number; look: DiffLook;
  /** The path that opens the file in the code editor, or null when it cannot be opened there. */
  openable: string | null;
  /** The external editor, and what opens a file in it; null where there is none. */
  external: ExternalFileOpener | null;
  read: ChangeReader; onToggle: (path: string) => void; onOpenFile: (path: string) => void; onStale: () => void;
  /** The section took another height: `before` is the one it had. */
  onResized: (section: HTMLElement, before: number) => void;
}) {
  const { t } = useShellLanguage();
  const [content, setContent] = useState<{ listed: string; value: ChangeContent | null; failure: string | null } | null>(null);
  // The height of what the editor shows, and the height the diff last had: what stands for it while it is not shown.
  const [measured, setMeasured] = useState<number | null>(null);
  const root = useRef<HTMLElement>(null), body = useRef<HTMLDivElement>(null);
  const kept = useRef<number | null>(null), height = useRef<number | null>(null);
  const [fit] = useState(() => (value: number) => { if (value > 0) setMeasured(Math.ceil(value)); });
  const shown = near && !collapsed;
  // In review mode: the comments of the file, on both sides of its diff or on the one side of a file that is new or gone.
  const review = useContext(ChangesReviewContext);
  const [editors, setEditors] = useState<DiffEditors | null>(null);
  const [inline, setInline] = useState(false);
  const [single, setSingle] = useState<monaco.editor.ICodeEditor | null>(null);
  const diff = useRef<DiffEditorHandle | null>(null);

  // A diff that is far from the view or folded is let go: only its height is kept.
  useEffect(() => { if (!shown) { setContent(null); setMeasured(null); } }, [shown]);
  useEffect(() => {
    if (!shown || !visible) return;
    if (content && (content.listed === file.revision || content.value?.revision === file.revision)) return;
    const abort = new AbortController();
    void read(file, abort.signal).then(value => value, () => "read_failed").then(value => {
      if (abort.signal.aborted) return;
      setContent(typeof value === "string" ? { listed: file.revision, value: null, failure: value } : { listed: file.revision, value, failure: null });
      // The list is older than the file: it is read again rather than left naming a file that is gone.
      if (value === "not_changed") onStale();
    });
    return () => abort.abort();
  }, [shown, visible, read, file.path, file.revision]);

  useLayoutEffect(() => {
    const section = root.current, now = section?.offsetHeight ?? 0;
    // A view that is not shown has no size: what was measured while it was shown stays.
    if (!section || !now) return;
    if (content && body.current) kept.current = body.current.offsetHeight;
    const before = height.current;
    height.current = now;
    if (before !== null && before !== now) onResized(section, before);
  });

  const appearance = fileAppearance(file.path, false);
  const value = content?.value ?? null;
  const notice = value ? changeContentNotice(value) : null;
  const oneSided = !!value && !notice && (value.originalState === "absent" || value.modifiedState === "absent");
  const comments = useDiffComments({ editors: value && !notice && !oneSided ? editors : null, inline: inline || !look.sideBySide, path: file.path,
    single: value && oneSided ? { editor: single, side: value.modifiedState === "absent" ? "old" : "new" } : null, unfold: (side, line) => diff.current?.reveal(side, line) });
  // Before the diff is read, its threads count in the room it is given: the view does not jump once they are drawn.
  const threads = review ? review.threads.filter(thread => thread.path === file.path && thread.state !== "resolved" && thread.startLine > 0).length : 0;
  const placeholder = kept.current ?? estimatedDiffHeight(file, viewport - sectionHeaderHeight) + reviewHeightEstimate(threads);
  const fitted = measured === null ? placeholder : fittedDiffHeight(measured, viewport - sectionHeaderHeight);
  const label = t(collapsed ? "Show the changes of the file" : "Hide the changes of the file");
  return <section ref={root} className="changes-section" data-path={file.path} data-status={file.status} data-collapsed={collapsed} aria-label={file.path}>
    <header className="changes-section-header">
      <button type="button" className="changes-section-toggle" aria-expanded={!collapsed} aria-label={`${label}: ${file.path}`}
        title={`${file.originalPath ? `${file.originalPath} → ${file.path}` : file.path}\n${t(changeLabel(file.status))}`} onClick={() => onToggle(file.path)}>
        <AppIcon name={collapsed ? "chevronRight" : "chevronDown"} size={14} />
        <span className="changes-status" data-status={file.status}>{changeLetter(file.status)}</span>
        <span className="file-tab-icon" data-file-tone={appearance.tone}><AppIcon name={appearance.icon} size={14} /></span>
        <span className="changes-diff-path">
          <strong>{changeFileName(file.path)}</strong>{changeFolder(file.path) && <small>{changeFolder(file.path)}</small>}
          {file.originalPath && <small className="changes-renamed">{t("Renamed from {path}", { path: file.originalPath })}</small>}
        </span>
      </button>
      <Counts insertions={file.insertions} deletions={file.deletions} />
      <ChangeBar insertions={file.insertions} deletions={file.deletions} />
      <span className="changes-spacer" />
      {review && <><ViewedCheck path={file.path} revision={file.revision} /><FileCommentButton path={file.path} /></>}
      <Button variant="minimal" size="small" icon={<AppIcon name="code" size={14} />} disabled={openable === null}
        aria-label={t("Open file")} title={t("Open file")} onClick={() => { if (openable !== null) onOpenFile(openable); }} />
      {external && <Button variant="minimal" size="small" icon={<ExternalEditorIcon id={external.editor.id} size={14} />} disabled={openable === null}
        aria-label={t("Open {name} in {editor}", { name: changeFileName(file.path), editor: external.editor.name })} title={t("Open in {editor}", { editor: external.editor.name })}
        onClick={() => { if (openable !== null) external.open(openable); }} />}
    </header>
    {!collapsed && comments.notes}
    {collapsed ? null
      : !content ? <div ref={body} className="changes-section-body" data-pending="true" style={{ height: placeholder }}>{near && <ActivitySpinner size={16} />}</div>
      : !value || notice ? <div ref={body} className="changes-section-body changes-section-notice">
        {t(!value ? content.failure === "not_changed" ? "The file has no changes any more." : "The file could not be read."
          : notice === "binary" ? "Binary file: there is no text to compare." : notice === "too_large" ? "The file is too large to compare here."
            : "The file could not be read.")}</div>
      // A file that is new or gone has nothing to compare with: it is shown whole, tinted as added or removed.
      : oneSided ? <div ref={body} className="changes-section-body changes-one-sided" data-side={value.modifiedState === "absent" ? "removed" : "added"} style={{ height: fitted }}>
        <CodeEditor value={value.modifiedState === "absent" ? value.original : value.modified} onChange={ignore} language={fileLanguage(value.path)}
          label={value.path} readOnly wrap={look.wrap} onContentHeight={fit} comments={!!review} onEditor={setSingle} /></div>
      : <div ref={body} className="changes-section-body" style={{ height: fitted }}>
        <DiffEditor documentKey={`${scope}\n${file.path}`} original={value.original} modified={value.modified} language={fileLanguage(value.path)} label={value.path}
          sideBySide={look.sideBySide} wrap={look.wrap} ignoreWhitespace={look.ignoreWhitespace} collapseUnchanged={look.collapseUnchanged}
          onContentHeight={fit} comments={!!review} onEditors={setEditors} onInline={setInline} handle={diff} /></div>}
    {comments.portals}
  </section>;
});

/**
 * The diffs of all the changed files, one under the other, in one view that scrolls: each file under a header
 * that stays at the top while its diff is crossed. A diff is read and built when it comes close to the view
 * and let go when it is far, so that a long list costs what is on screen. A diff of more than a few hundred
 * lines takes the height of the view and scrolls by itself.
 */
export function AllChanges({ files, scope, visible, look, current, reveal, collapsed, truncated, read, openable, external = null, onCurrent, onToggle, onOpenFile, onStale, handle }: {
  /** The files, in the order of the list. */
  files: readonly ChangedFile[];
  /** Identifies what is compared: the diffs of another comparison are other documents. */
  scope: string;
  /** The tab is shown: nothing is read while it is not. */
  visible: boolean; look: DiffLook;
  /** The file that is selected in the list. */
  current: string | null;
  /** The file to bring to the top; a new object for each request. */
  reveal: Readonly<{ path: string }> | null;
  collapsed: ReadonlySet<string>;
  /** The list holds only the first of the changed files. */
  truncated: boolean;
  read: ChangeReader; openable: (file: ChangedFile) => string | null;
  /** The external editor, and what opens a file in it; null where there is none. */
  external?: ExternalFileOpener | null;
  /** Scrolling brought another file to the top of the view. */
  onCurrent: (path: string) => void; onToggle: (path: string) => void; onOpenFile: (path: string) => void; onStale: () => void;
  /** Gives the keyboard to the view; brings an element of a section (the card of a comment) into it. */
  handle?: RefObject<AllChangesHandle | null>;
}) {
  const { t } = useShellLanguage();
  const scroller = useRef<HTMLDivElement>(null);
  const [near, setNear] = useState<ReadonlySet<string>>(() => new Set());
  const [viewport, setViewport] = useState(0);
  const latest = useRef({ current, onCurrent, read }); latest.current = { current, onCurrent, read };
  // The file the view was asked to show: it stays at the top while what is around it takes its size, until the user scrolls.
  const held = useRef<{ path: string; until: number } | null>(null);
  const sections = () => Array.from(scroller.current?.querySelectorAll<HTMLElement>(":scope > .changes-section") ?? []);
  const sectionOf = (path: string) => sections().find(section => section.dataset.path === path) ?? null;

  // A few files are read at a time: the others wait, and one that left the view meanwhile is not read at all.
  const [queued] = useState(() => {
    const waiting: (() => void)[] = [];
    let running = 0;
    const next = () => { while (running < readsAtOnce && waiting.length) waiting.shift()!(); };
    return (file: ChangedFile, signal: AbortSignal): Promise<ChangeContent | string> => new Promise((resolve, reject) => {
      waiting.push(() => {
        if (signal.aborted) { reject(new DOMException("Aborted", "AbortError")); return; }
        running++;
        void latest.current.read(file, signal).then(resolve, reject).finally(() => { running--; next(); });
      });
      next();
    });
  });

  // Which sections are in the view or within a view of it.
  useLayoutEffect(() => {
    const view = scroller.current;
    if (!view || typeof IntersectionObserver !== "function") return;
    const observer = new IntersectionObserver(entries => setNear(known => {
      // A view that is not shown sees nothing: what it held stays for when it is shown again.
      if (!view.clientHeight) return known;
      let next: Set<string> | null = null;
      for (const entry of entries) {
        const path = (entry.target as HTMLElement).dataset.path;
        if (path === undefined || known.has(path) === entry.isIntersecting) continue;
        next ??= new Set(known);
        if (entry.isIntersecting) next.add(path); else next.delete(path);
      }
      return next ?? known;
    }), { root: view, rootMargin: "100% 0px" });
    for (const section of sections()) observer.observe(section);
    return () => observer.disconnect();
  }, [files]);

  useLayoutEffect(() => {
    if (!handle) return;
    handle.current = { focus: () => scroller.current?.focus(), show: element => {
      const view = scroller.current;
      if (!view || !view.contains(element)) return;
      // What the user asked to see wins over the file the view held at its top.
      held.current = null;
      const box = element.getBoundingClientRect(), area = view.getBoundingClientRect();
      if (box.top < area.top + sectionHeaderHeight || box.bottom > area.bottom) view.scrollTop += box.top - area.top - Math.max(sectionHeaderHeight, (area.height - box.height) / 3);
    } };
    return () => { handle.current = null; };
  }, [handle]);

  useLayoutEffect(() => {
    const view = scroller.current;
    if (!view) return;
    // The height of a view that is not shown is not one: the last one it had stays.
    const measure = () => { if (view.clientHeight) setViewport(view.clientHeight); };
    measure();
    if (typeof ResizeObserver !== "function") return;
    const observer = new ResizeObserver(measure);
    observer.observe(view);
    return () => observer.disconnect();
  }, []);

  const show = (path: string) => {
    const view = scroller.current, section = sectionOf(path);
    if (view && section) view.scrollTop = section.offsetTop;
  };
  useLayoutEffect(() => {
    if (!reveal) return;
    held.current = { path: reveal.path, until: performance.now() + holdMilliseconds };
    show(reveal.path);
  }, [reveal]);

  // A diff that takes its size above the view would push what is read down or up: the view follows it.
  const [resized] = useState(() => (section: HTMLElement, before: number) => {
    const view = scroller.current;
    if (!view) return;
    if (held.current && performance.now() < held.current.until) { show(held.current.path); return; }
    held.current = null;
    if (section.offsetTop + before <= view.scrollTop) view.scrollTop += section.offsetHeight - before;
  });

  // The file at the top of the view is the selected one of the list.
  useEffect(() => {
    const view = scroller.current;
    if (!view) return;
    let frame = 0;
    const follow = () => {
      frame = 0;
      if (held.current) { if (performance.now() < held.current.until) return; held.current = null; }
      const all = sections();
      // At the end of the view the last files cannot reach its top: the selected one stays while it is in sight.
      const selected = latest.current.current !== null ? sectionOf(latest.current.current) : null;
      if (selected && view.scrollTop + view.clientHeight >= view.scrollHeight - 1 && selected.offsetTop >= view.scrollTop) return;
      const index = sectionAt(all.length, at => all[at].offsetTop, view.scrollTop + 1);
      const path = index < 0 ? undefined : all[index].dataset.path;
      if (path !== undefined && path !== latest.current.current) latest.current.onCurrent(path);
    };
    const scrolled = () => { frame ||= requestAnimationFrame(follow); };
    const released = () => { held.current = null; };
    view.addEventListener("scroll", scrolled, { passive: true });
    for (const type of ["wheel", "touchmove", "pointerdown", "keydown"] as const) view.addEventListener(type, released, { passive: true, capture: true });
    return () => {
      cancelAnimationFrame(frame);
      view.removeEventListener("scroll", scrolled);
      for (const type of ["wheel", "touchmove", "pointerdown", "keydown"] as const) view.removeEventListener(type, released, { capture: true });
    };
  }, []);

  return <div ref={scroller} className="changes-all" role="region" tabIndex={0} aria-label={t("Changes")}>
    {files.map(file => <Section key={file.path} file={file} scope={scope} near={near.has(file.path)} visible={visible} collapsed={collapsed.has(file.path)}
      viewport={viewport} look={look} openable={openable(file)} external={external} read={queued} onToggle={onToggle} onOpenFile={onOpenFile} onStale={onStale} onResized={resized} />)}
    {truncated && <p className="changes-no-match">{t("More files changed than this list shows.")}</p>}
  </div>;
}
