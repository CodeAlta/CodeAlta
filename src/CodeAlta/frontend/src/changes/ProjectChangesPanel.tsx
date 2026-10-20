import { memo, useCallback, useEffect, useId, useLayoutEffect, useMemo, useRef, useState, type KeyboardEvent, type PointerEvent } from "react";
import { Button, ButtonGroup, InputGroup, Menu, MenuDivider, MenuItem, NonIdealState, PopoverNext, Switch } from "@blueprintjs/core";
import { projectGit, worktrees as worktreesApi, type WorkspaceSession } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { CodeEditor } from "../monaco/CodeEditor";
import { DiffEditor, type DiffEditorChanges, type DiffEditorHandle } from "../monaco/DiffEditor";
import { fileAppearance } from "../fileAppearance";
import { fileLanguage } from "../monaco/fileLanguage";
import type { FileTab } from "../fileTabs";
import { AllChanges, type AllChangesHandle } from "./AllChanges";
import { ChangeBar, Counts } from "./ChangeCounts";
import { useChangesPreferences } from "./changesPreferences";
import { changeCommitsReply, changeContent, changeKeysAttribute, changeStep, changeContentNotice, changeFileName, changeFolder, changeHistoryHeight, changeLabel, changeLetter,
  changeListReply, changeListRows, changeListWidth, changeScopeKey, changesViewLabel, changesViews, changeTreeRows, commitLimitMaximum, commitPageSize, filterChanges,
  orderChanges, projectRelativePath, selectedChange, type ChangeCommit, type ChangeCommits,
  type ChangeContent, type ChangedFile, type ChangeList, type ChangeRow, type ChangeScope } from "./projectChanges";
import { sessionTime } from "../sessionTime";
import { useShellLanguage } from "../shellLanguage";
import { ExternalEditorIcon, useExternalOpener } from "../externalEditors/externalEditors";
import { BranchSwitcher } from "../worktrees/BranchSwitcher";
import { WorktreeList } from "../worktrees/WorktreeList";
import { checkoutFolder, sameFolder, shownCheckout, worktreeSessions, worktreesReply, type Worktree } from "../worktrees/worktrees";
import { modalDialogOpen } from "../modalDialogs";
import { showToast } from "../appToaster";
import type { DiffEditors } from "../monaco/DiffEditor";
import type { monaco } from "../monaco/monacoEnvironment";
import { ReviewBar } from "../review/ReviewBar";
import { ReviewCommentsList } from "../review/ReviewCommentsList";
import { nextReviewThread, reviewKeyAction, reviewOrder, reviewTargetOfScope, threadCountsByFile, typingTarget, viewedFiles, type FileThreadCounts } from "../review/changesReview";
import { threadCounts, type ReviewFilter, type ReviewThread } from "../review/reviewThreads";
import { reviewSessions } from "../review/reviewSessions";
import { reviewSubmitter, SubmitReviewButton } from "../review/SubmitReviewForm";
import { ChangesReviewContext, FileCommentButton, useDiffComments, ViewedCheck, type ChangesReviewValue, type DiffCommentsEntry } from "../review/useDiffComments";
import { useReviewActions, useReviewList, useReviewsHub } from "../review/useReviews";

const autoRefreshMilliseconds = 5000;
const ignore = () => { };
const noThreads: readonly ReviewThread[] = Object.freeze([]);
const noPaths: ReadonlySet<string> = new Set();
const modalOpen = () => !!modalDialogOpen();
// A refusal that will not go away by asking again: the list it replaces is no longer shown.
const lasting = new Set(["not_repository", "unknown_project", "project_unavailable", "unavailable", "invalid", "worktree_missing"]);
// The same checkouts again draw nothing.
const sameCheckouts = (a: readonly Worktree[] | null, b: readonly Worktree[] | null) => a === b || JSON.stringify(a) === JSON.stringify(b);

const failures = {
  not_repository: "This folder is not in a git repository.",
  git_failed: "Git could not list the changes. Check that git is installed and that it trusts this folder.",
  worktree_missing: "The worktree is no longer there.",
  unknown_project: "The project is no longer available.",
  project_unavailable: "The project is no longer available.",
  unavailable: "Changes cannot be read in this window.",
} as const;
const failureText = (status: string) => (failures as Record<string, typeof failures[keyof typeof failures] | undefined>)[status] ?? "The changes could not be read.";

const FileRow = memo(function FileRow({ row, selected, flat, onSelect, threads = null, viewed = false }: {
  row: Extract<ChangeRow, { kind: "file" }>; selected: boolean; flat: boolean; onSelect: (path: string) => void;
  /** In review mode: the comments of the file that are not resolved, and whether it was viewed at its revision. */
  threads?: FileThreadCounts | null; viewed?: boolean;
}) {
  const { t } = useShellLanguage();
  const look = fileAppearance(row.file.path, false);
  return <button type="button" role="option" className="changes-row changes-file" data-status={row.file.status} data-path={row.file.path}
    aria-selected={selected} tabIndex={-1} style={{ paddingLeft: 8 + row.depth * 14 }}
    title={`${row.file.path}\n${t(changeLabel(row.file.status))}${row.file.originalPath ? `\n${t("Renamed from {path}", { path: row.file.originalPath })}` : ""}`}
    onClick={() => onSelect(row.file.path)}>
    <span className="file-tab-icon" data-file-tone={look.tone}><AppIcon name={look.icon} size={14} /></span>
    <span className="changes-row-name"><strong>{row.name}</strong>{flat && row.folder && <small>{row.folder}</small>}</span>
    {threads && threads.active > 0 && <span className="changes-row-threads" data-drafts={threads.drafts > 0 || undefined}
      title={t(threads.active === 1 ? "{count} comment" : "{count} comments", { count: threads.active })}><AppIcon name="chat" size={11} />{threads.active}</span>}
    {viewed && <span className="changes-row-viewed" title={t("Viewed")} aria-label={t("Viewed")}><AppIcon name="check" size={12} /></span>}
    <Counts insertions={row.file.insertions} deletions={row.file.deletions} />
    <span className="changes-status" aria-label={t(changeLabel(row.file.status))}>{changeLetter(row.file.status)}</span>
  </button>;
});

const FolderRow = memo(function FolderRow({ row, onToggle }: { row: Extract<ChangeRow, { kind: "folder" }>; onToggle: (path: string) => void }) {
  return <button type="button" className="changes-row changes-folder" aria-expanded={!row.collapsed} tabIndex={-1}
    style={{ paddingLeft: 8 + row.depth * 14 }} title={row.path} onClick={() => onToggle(row.path)}>
    <AppIcon name={row.collapsed ? "chevronRight" : "chevronDown"} size={14} />
    <span className="changes-row-name"><span>{row.name}</span></span>
    <span className="changes-folder-count">{row.files}</span>
  </button>;
});

const CommitRow = memo(function CommitRow({ commit, selected, onSelect }: { commit: ChangeCommit; selected: boolean; onSelect: (id: string) => void }) {
  const { locale } = useShellLanguage();
  const time = sessionTime(commit.time, locale);
  return <button type="button" role="option" className="changes-history-row" aria-selected={selected} tabIndex={-1} data-commit={commit.id}
    title={`${commit.subject}\n${commit.shortId} · ${commit.author} · ${time.title}`} onClick={() => onSelect(commit.id)}>
    <span className="changes-history-mark" aria-hidden="true" />
    <span className="changes-history-text"><strong>{commit.subject || commit.shortId}</strong>
      <small><code>{commit.shortId}</code><span>{commit.author}</span><time dateTime={time.dateTime}>{time.label}</time></small></span>
  </button>;
});

/**
 * The changes of one project's repository in a tab: the changed files as a tree or a list with their added
 * and removed lines, and the diff of the selected file. Under the files, the history chooses what is compared:
 * the uncommitted changes, everything since the base of the branch, or one of the recent commits. The lists
 * are read when the tab is shown, on demand and, while auto-refresh is on, every five seconds; a list that did
 * not change redraws nothing.
 *
 * The diff side shows one file at a time or, when the user prefers it, all the files one under the other in a
 * view that scrolls, where the file at the top is the selected one of the list.
 *
 * A repository that has git worktrees lists its checkouts above the files: the folder of the project and each
 * worktree, and the main checkout of the repository when the project itself lives in a worktree. The tab shows
 * the changes and the commits of the one that is selected, a worktree is removed from its row, and the branch
 * in the header moves the checkout to another branch.
 */
export function ProjectChangesPanel({ tab, projectName, epoch, visible, active, onActivate, onOpenFile, request, sessions, onWorktreesChanged, onOpenSession, api = projectGit,
  trees = worktreesApi }: {
  tab: FileTab;
  /** The name of the project while it is still open; undefined once it is gone. */
  projectName: string | undefined;
  /** The owned host's epoch; null without an owned host, undefined until the host has answered. */
  epoch: string | null | undefined;
  /** The tab is the selected one of its pane. Nothing is read while it is not. */
  visible: boolean;
  /** This tab is the one commands and the keyboard act on. */
  active: boolean; onActivate: () => void;
  /** Opens a file of the project (a project-relative path) in an editor tab. */
  onOpenFile: (path: string) => void;
  /**
   * The file an `alta diff show` asked for, and the checkout to show when one is named (null is the folder of
   * the project); a new object for each request.
   */
  request?: Readonly<{ path: string | null; worktree?: string | null;
    /** Opens the review of the uncommitted changes (`changes`) or of the branch since its base (`branch`). */
    review?: "changes" | "branch" | null }> | null;
  /** Opens a session, the one a review was sent to. */
  onOpenSession?: (sessionId: string) => void;
  /** The sessions of the window: a worktree shows how many work in it. */
  sessions?: readonly WorkspaceSession[];
  /** A worktree was removed: what the sessions record is read again. */
  onWorktreesChanged?: () => void;
  api?: Pick<typeof projectGit, "changes" | "file" | "commits">;
  trees?: Pick<typeof worktreesApi, "list" | "remove" | "branches" | "switch">;
}) {
  const { t, locale } = useShellLanguage();
  const [preferences, update] = useChangesPreferences();
  const sidebarId = useId();
  const sidebarToggle = useRef<HTMLButtonElement>(null);
  const all = preferences.view === "all";
  const [list, setList] = useState<ChangeList | null>(null);
  // What the list on screen compares: while another comparison is read, the list is still the one of the previous.
  const [listed, setListed] = useState<string | null>(null);
  const [failure, setFailure] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [scope, setScope] = useState<ChangeScope>({ kind: "head" });
  const [history, setHistory] = useState<ChangeCommits | null>(null);
  const [historyLimit, setHistoryLimit] = useState(commitPageSize);
  const [selected, setSelected] = useState<string | null>(null);
  // In the view of all files: the file to bring to the top (a new object for each request), and the files that are folded.
  const [reveal, setReveal] = useState<Readonly<{ path: string }> | null>(null);
  const [folded, setFolded] = useState<ReadonlySet<string>>(() => new Set());
  const [filter, setFilter] = useState("");
  const [collapsed, setCollapsed] = useState<ReadonlySet<string>>(() => new Set());
  const [content, setContent] = useState<{ key: string; listed: string; value: ChangeContent | null; failure: string | null } | null>(null);
  const [changes, setChanges] = useState<DiffEditorChanges>({ count: 0, current: 0 });
  const [copied, setCopied] = useState(false);
  // The checkout that is shown: the folder of the project in one of its worktrees, or null for the folder of the project.
  const [checkout, setCheckout] = useState<string | null>(null);
  const [checkouts, setCheckouts] = useState<readonly Worktree[] | null>(null);
  const diff = useRef<DiffEditorHandle | null>(null);
  const allFiles = useRef<AllChangesHandle | null>(null);
  const rows = useRef<HTMLDivElement>(null);
  const root = useRef<HTMLElement>(null);
  const reading = useRef(false), again = useRef(false);
  const scopeKey = changeScopeKey(scope);
  // What the last list was read for: another comparison never reuses its revision.
  const latest = useRef({ list, scope, listed: scopeKey, selected, wanted: null as string | null, history, historyLimit, historyRead: 0, checkout });
  latest.current.list = list; latest.current.scope = scope; latest.current.selected = selected;
  latest.current.history = history; latest.current.historyLimit = historyLimit; latest.current.checkout = checkout;

  // Shows another checkout: nothing of the one that was shown is kept.
  function showCheckout(folder: string | null) {
    if (folder === latest.current.checkout || folder !== null && sameFolder(folder, latest.current.checkout)) return;
    latest.current.list = null; latest.current.history = null; latest.current.historyRead = 0; latest.current.wanted = null; latest.current.checkout = folder;
    setCheckout(folder); setList(null); setListed(null); setHistory(null); setFailure(null); setSelected(null); setReveal(null); setContent(null);
    setScope({ kind: "head" }); setFilter("");
  }
  const showCheckoutLatest = useRef(showCheckout); showCheckoutLatest.current = showCheckout;
  // The view of all files, when it is chosen, starts at the selected file.
  const [shownView, setShownView] = useState(preferences.view);
  if (shownView !== preferences.view) {
    setShownView(preferences.view);
    setReveal(all && selected !== null ? { path: selected } : null);
  }

  // Reads the list. Nothing is set when the host answers that it is the one already shown.
  const refresh = useRef<(manual?: boolean) => void>(() => { });
  useEffect(() => {
    if (!epoch) { if (epoch === null) setFailure("unavailable"); return; }
    const abort = new AbortController();
    refresh.current = (manual = false) => {
      // A read in flight is for what was shown when it started: what is asked meanwhile is read right after it.
      if (reading.current) { again.current = true; return; }
      reading.current = true;
      const scope = latest.current.scope, asked = changeScopeKey(scope);
      if (manual || !latest.current.list || latest.current.listed !== asked) setBusy(true);
      const known = latest.current.list && latest.current.listed === asked ? latest.current.list.revision : null;
      // The history is read beside the files: a commit made meanwhile shows up with the list it emptied.
      const limit = latest.current.historyLimit, knownHistory = latest.current.history && latest.current.historyRead === limit ? latest.current.history.revision : null;
      // The checkouts are read with the files: a worktree made or removed meanwhile shows up with them.
      void trees.list({ expectedEpoch: epoch, projectId: tab.projectId }, { signal: abort.signal, timeoutMilliseconds: 30_000 })
        .then(reply => worktreesReply(reply, tab.projectId), () => "read_failed")
        .then(value => {
          if (abort.signal.aborted) return;
          const next = typeof value === "string" ? null : value.worktrees;
          setCheckouts(current => sameCheckouts(current, next) ? current : next);
          // The checkout that was shown is gone: the folder of the project is shown again. The main checkout of
          // a repository is one that can be shown, when the project lives in a worktree.
          if (next && latest.current.checkout !== null && !shownCheckout(next, latest.current.checkout)) showCheckoutLatest.current(null);
        });
      void api.commits({ expectedEpoch: epoch, projectId: tab.projectId, limit, knownRevision: knownHistory, worktree: checkout }, { signal: abort.signal, timeoutMilliseconds: 30_000 })
        .then(reply => changeCommitsReply(reply, tab.projectId, knownHistory), () => null)
        .then(value => {
          if (abort.signal.aborted || !value || value === "unchanged") return;
          latest.current.history = value; latest.current.historyRead = limit;
          setHistory(value);
        });
      void api.changes({ expectedEpoch: epoch, projectId: tab.projectId, comparison: scope.kind, commit: scope.kind === "commit" ? scope.id : null, knownRevision: known,
        worktree: checkout },
        { signal: abort.signal, timeoutMilliseconds: 30_000 })
        .then(reply => changeListReply(reply, tab.projectId, known), () => ({ kind: "failed" as const, status: "read_failed" }))
        .then(reply => {
          if (abort.signal.aborted || changeScopeKey(latest.current.scope) !== asked) return;
          if (reply.kind === "list") {
            // Kept in the order they are shown, so that the first file and the next one are the ones on screen.
            const read = { ...reply.list, files: orderChanges(reply.list.files) };
            const wanted = latest.current.wanted;
            const asking = wanted !== null && read.files.some(file => file.path === wanted);
            const next = asking ? wanted : selectedChange(latest.current.list?.files ?? [], read.files, latest.current.selected);
            // The list of another comparison starts at its selected file, with every file unfolded.
            const other = !latest.current.list || latest.current.listed !== asked;
            latest.current.wanted = null; latest.current.listed = asked; latest.current.list = read;
            setList(read); setListed(asked); setFailure(null); setSelected(next);
            if (other) setFolded(new Set());
            if (other || asking) setReveal(next === null ? null : { path: next });
            // The host fell back to the uncommitted changes: the branch has no commit of its own any more.
            if (read.comparison !== scope.kind) setScope({ kind: "head" });
          } else if (reply.kind === "failed" && (lasting.has(reply.status) || !latest.current.list || latest.current.listed !== asked)) {
            // Nothing to keep: no list yet, or the one on screen is that of another comparison.
            latest.current.list = null;
            setList(null); setListed(null); setFailure(reply.status);
          }
        })
        .finally(() => {
          reading.current = false;
          if (abort.signal.aborted) return;
          setBusy(false);
          if (again.current) { again.current = false; refresh.current(); }
        });
    };
    return () => { abort.abort(); reading.current = false; again.current = false; refresh.current = () => { }; };
  }, [api, trees, epoch, tab.projectId, checkout]);

  useEffect(() => {
    if (!visible || !epoch) return;
    refresh.current();
    const focused = () => refresh.current();
    window.addEventListener("focus", focused);
    const timer = preferences.autoRefresh
      ? window.setInterval(() => { if (document.visibilityState !== "hidden") refresh.current(); }, autoRefreshMilliseconds) : undefined;
    return () => { window.removeEventListener("focus", focused); window.clearInterval(timer); };
  }, [visible, epoch, scopeKey, historyLimit, preferences.autoRefresh, tab.projectId, checkout]);

  // A file an agent asked for is selected now, or once a list names it; a checkout that was asked for is shown.
  useEffect(() => {
    if (request?.worktree !== undefined) showCheckoutLatest.current(request.worktree);
    // A review asked for: its scope, in review mode.
    if (request?.review) { setScope({ kind: request.review === "branch" ? "branch" : "head" }); setReviewChoice(true); }
    const path = request?.path ?? null;
    if (path === null) return;
    if (latest.current.list?.files.some(file => file.path === path)) { setSelected(path); setReveal({ path }); }
    else latest.current.wanted = path;
    refresh.current();
  }, [request]);

  const file = useMemo(() => list?.files.find(value => value.path === selected) ?? null, [list, selected]);
  const contentKey = file ? JSON.stringify([checkout, scopeKey, file.path]) : null;
  // Both sides of the selected file, read again when the list says that one of them changed. The view of all
  // files reads each file by itself.
  useEffect(() => {
    if (all || !file || !contentKey || !epoch || !visible) return;
    if (content?.key === contentKey && (content.listed === file.revision || content.value?.revision === file.revision)) return;
    const abort = new AbortController();
    void api.file({ expectedEpoch: epoch, projectId: tab.projectId, comparison: scope.kind, commit: scope.kind === "commit" ? scope.id : null, path: file.path,
      worktree: checkout },
      { signal: abort.signal, timeoutMilliseconds: 30_000 })
      .then(reply => changeContent(reply, tab.projectId, file.path), () => "read_failed")
      .then(value => {
        if (abort.signal.aborted) return;
        setContent(typeof value === "string" ? { key: contentKey, listed: file.revision, value: null, failure: value }
          : { key: contentKey, listed: file.revision, value, failure: null });
        // The list is older than the file: it is read again rather than left naming a file that is gone.
        if (value === "not_changed") refresh.current();
      });
    return () => abort.abort();
  }, [api, epoch, visible, all, tab.projectId, scopeKey, contentKey, file?.revision]);
  const readFile = useCallback((changed: ChangedFile, signal: AbortSignal) => api.file({ expectedEpoch: epoch ?? "", projectId: tab.projectId, comparison: scope.kind,
    commit: scope.kind === "commit" ? scope.id : null, path: changed.path, worktree: checkout }, { signal, timeoutMilliseconds: 30_000 })
    .then(reply => changeContent(reply, tab.projectId, changed.path), () => "read_failed"), [api, epoch, tab.projectId, scopeKey, checkout]);

  const shown = useMemo(() => list ? filterChanges(list.files, filter) : [], [list, filter]);
  const layout = preferences.layout;
  const visibleRows = useMemo(() => layout === "tree" ? changeTreeRows(shown, filter.trim() ? new Set() : collapsed) : changeListRows(shown),
    [shown, layout, collapsed, filter]);
  const fileRows = useMemo(() => visibleRows.filter((row): row is Extract<ChangeRow, { kind: "file" }> => row.kind === "file"), [visibleRows]);

  // The review of what the tab compares: the uncommitted changes or the branch since its base, of the checkout shown.
  // Review mode gives the diffs comments; it is on while the review has comments that are not resolved or an agent
  // asked for it, and the user can turn it on or off.
  const reviews = useReviewsHub();
  const reviewTarget = useMemo(() => reviewTargetOfScope(scope), [scope.kind]);
  const reviewScope = useMemo(() => reviews && epoch ? { projectId: tab.projectId, checkout } : null, [reviews, epoch, tab.projectId, checkout]);
  const reviewList = useReviewList(reviews, reviewTarget ? reviewScope : null, reviewTarget);
  const [reviewChoice, setReviewChoice] = useState<boolean | null>(null);
  const reviewWaits = reviewList.threads.some(thread => thread.state !== "resolved") || !!reviewList.request;
  const reviewing = !!reviewTarget && !!reviewScope && (reviewChoice ?? reviewWaits);
  const reviewActions = useReviewActions(reviews, reviewing ? reviewScope : null, !!reviewList.readOnly);
  const reviewThreads = reviewing ? reviewList.threads : noThreads;
  const fileThreads = useMemo(() => threadCountsByFile(reviewThreads), [reviewThreads]);
  const viewed = useMemo(() => reviewing && list ? viewedFiles(reviewList.viewed, reviewTarget, list.files) : noPaths, [reviewing, list, reviewList.viewed, reviewTarget]);
  const [sideView, setSideView] = useState<"files" | "comments">("files");
  const [commentsFilter, setCommentsFilter] = useState<ReviewFilter>("active");
  const [wanted, setWanted] = useState<Readonly<{ thread: ReviewThread }> | null>(null);
  const entries = useRef(new Map<string, DiffCommentsEntry>());
  const reviewLatest = useRef({ reviewScope, reviewTarget, t, sideBySide: preferences.sideBySide });
  reviewLatest.current = { reviewScope, reviewTarget, t, sideBySide: preferences.sideBySide };
  const register = useCallback((path: string, entry: DiffCommentsEntry) => {
    entries.current.set(path, entry);
    return () => { if (entries.current.get(path) === entry) entries.current.delete(path); };
  }, []);
  const reviewValue = useMemo<ChangesReviewValue | null>(() => !reviewing || !reviews || !reviewScope || !reviewTarget ? null : {
    threads: reviewThreads, actions: reviewActions, blocked: reviewList.readOnly, viewed, wanted, register,
    add: reviewList.readOnly ? null : async (path, side, lines, body, suggestion) => {
      const done = await reviews.add(reviewScope, reviewTarget, { side, startLine: lines.startLine, endLine: lines.endLine, body, suggestion });
      if (!done.ok && done.message) showToast({ message: done.message, intent: "warning", icon: "warning-sign", timeout: 6000 });
      return done.ok;
    },
    setViewed: reviewTarget.kind === "file" || reviewList.readOnly ? null
      : (path, revision, value) => void reviews.viewed(reviewScope, reviewTarget.kind as "changes" | "branch", path, value ? revision : null),
    shown: done => setWanted(current => current === done ? null : current),
    sideBySide: () => update({ sideBySide: true }),
    commentFile: path => entries.current.get(path)?.commentFile(),
    bringIntoView: element => allFiles.current?.show(element),
  }, [reviewing, reviews, reviewScope, reviewTarget, reviewThreads, reviewActions, reviewList.readOnly, viewed, wanted, register, update]);

  const select = useRef((path: string) => { setSelected(path); setReveal({ path }); });
  const follow = useRef((path: string) => setSelected(path));
  const fold = useRef((path: string) => setFolded(current => {
    const next = new Set(current);
    if (!next.delete(path)) next.add(path);
    return next;
  }));
  const stale = useRef(() => refresh.current());
  const openLatest = useRef(onOpenFile); openLatest.current = onOpenFile;
  const open = useRef((path: string) => openLatest.current(path));
  const look = useMemo(() => ({ sideBySide: preferences.sideBySide, wrap: preferences.wrap, ignoreWhitespace: preferences.ignoreWhitespace,
    collapseUnchanged: preferences.collapseUnchanged }), [preferences.sideBySide, preferences.wrap, preferences.ignoreWhitespace, preferences.collapseUnchanged]);
  const selectCommit = useRef((id: string) => setScope({ kind: "commit", id }));
  const shownCommit = scope.kind === "commit" ? history?.commits.find(commit => commit.id === scope.id) ?? null : null;
  const toggle = useRef((path: string) => setCollapsed(current => {
    const next = new Set(current);
    if (!next.delete(path)) next.add(path);
    return next;
  }));
  useLayoutEffect(() => {
    if (preferences.sidebarVisible && selected !== null) rows.current?.querySelector<HTMLElement>('[aria-selected="true"]')?.scrollIntoView({ block: "nearest" });
  }, [selected, layout, preferences.sidebarVisible]);
  // A hidden sidebar never takes the keyboard: the header's toggle stays within reach, also in a restored tab.
  useEffect(() => {
    const hiddenFocus = !preferences.sidebarVisible && root.current?.querySelector(".changes-files")?.contains(document.activeElement);
    if (active && !modalOpen() && (hiddenFocus || !root.current?.contains(document.activeElement)))
      (preferences.sidebarVisible ? rows.current ?? sidebarToggle.current : sidebarToggle.current)?.focus();
  }, [active, preferences.sidebarVisible]);

  // The files the keyboard goes through: the rows of the list, and every file where they are all shown.
  const reachable = useMemo(() => all ? shown : fileRows.map(row => row.file), [all, shown, fileRows]);
  function move(delta: number) {
    if (!reachable.length) return;
    const index = reachable.findIndex(value => value.path === selected);
    const next = index < 0 ? (delta > 0 ? 0 : reachable.length - 1) : Math.min(reachable.length - 1, Math.max(0, index + delta));
    select.current(reachable[next].path);
  }
  function listKeyDown(event: KeyboardEvent<HTMLElement>) {
    if (event.defaultPrevented || event.ctrlKey || event.metaKey || event.altKey || event.nativeEvent.isComposing) return;
    if (event.key === "ArrowDown") move(1);
    else if (event.key === "ArrowUp") move(-1);
    else if (event.key === "Home") move(-reachable.length);
    else if (event.key === "End") move(reachable.length);
    else if (event.key === "PageDown") move(10);
    else if (event.key === "PageUp") move(-10);
    else if (event.key === "Enter" || event.key === "ArrowRight") (all ? allFiles : diff).current?.focus();
    else return;
    event.preventDefault();
  }
  // A thread of the review, where it is drawn: its file selected (brought to the top where all files are shown), its
  // lines unfolded, its card given the keyboard by the diff once it shows it.
  function revealThread(thread: ReviewThread) {
    if (!list?.files.some(value => value.path === thread.path)) {
      showToast({ message: t("This file has no changes in what the tab compares."), timeout: 5000 });
      return;
    }
    if (filter && !shown.some(value => value.path === thread.path)) setFilter("");
    if (folded.has(thread.path)) setFolded(current => { const next = new Set(current); next.delete(thread.path); return next; });
    setSelected(thread.path);
    setReveal({ path: thread.path });
    setWanted({ thread });
  }
  // The keys of the review: comment, go from thread to thread or from file to file, mark the file viewed.
  function reviewKey(action: NonNullable<ReturnType<typeof reviewKeyAction>>): boolean {
    switch (action) {
      case "comment": {
        const focused = [...entries.current.values()].find(entry => entry.focused());
        if (focused) return focused.comment(true);
        return selected !== null && !!entries.current.get(selected)?.comment(true);
      }
      case "nextThread": case "previousThread": {
        const order = reviewOrder(reviewThreads, shown);
        const card = document.activeElement?.closest<HTMLElement>("[data-thread-id]");
        const next = nextReviewThread(order, { id: card?.dataset.threadId ?? null, path: selected }, action === "nextThread" ? 1 : -1);
        if (next) revealThread(next);
        return !!next;
      }
      case "nextFile": move(1); return true;
      case "previousFile": move(-1); return true;
      case "viewed": {
        const current = list?.files.find(value => value.path === selected);
        if (!current || !reviewValue?.setViewed) return false;
        reviewValue.setViewed(current.path, current.revision, !viewed.has(current.path));
        return true;
      }
    }
  }
  // Alt+Down and Alt+Up go through the changes of the file, from the list as well as from the diff: taken before
  // the editor, where they would move a line. Where all the files are shown, they go through the files. The window
  // leaves these keys to the tab (`changeKeysAttribute`): outside it they select another session.
  function panelKeyDown(event: KeyboardEvent<HTMLElement>) {
    if (reviewing && !event.defaultPrevented && !event.nativeEvent.isComposing && !modalOpen()) {
      const action = reviewKeyAction(event, typingTarget(event.target instanceof Element ? event.target : null));
      if (action && reviewKey(action)) { event.preventDefault(); event.stopPropagation(); return; }
    }
    const step = event.defaultPrevented ? null : changeStep(event);
    if (!step) return;
    event.preventDefault(); event.stopPropagation();
    if (all) move(step === "next" ? 1 : -1);
    else diff.current?.go(step);
  }
  // Drags a splitter: the one beside the files changes their width, the one above the history its height.
  function resize(event: PointerEvent<HTMLDivElement>, history = false) {
    if (event.button !== 0) return;
    event.preventDefault();
    const handle = event.currentTarget, start = history ? event.clientY : event.clientX, size = history ? preferences.historyHeight : preferences.listWidth;
    handle.setPointerCapture(event.pointerId);
    const moved = (value: globalThis.PointerEvent) => update(history ? { historyHeight: changeHistoryHeight(size - (value.clientY - start)) }
      : { listWidth: changeListWidth(size + value.clientX - start) });
    const done = () => { handle.removeEventListener("pointermove", moved); handle.removeEventListener("pointerup", done); handle.removeEventListener("pointercancel", done); };
    handle.addEventListener("pointermove", moved); handle.addEventListener("pointerup", done); handle.addEventListener("pointercancel", done);
  }

  const current = content?.key === contentKey ? content : null;
  const notice = current?.value ? changeContentNotice(current.value) : null;
  // While the next file is read the editor keeps the previous one, dimmed: it is not built again for each file.
  const compared = content?.value && !changeContentNotice(content.value) && (current || file) ? content.value : null;
  // The code editor shows the folder of the project: a file of a worktree is not opened there under the same name.
  const prefix = list?.prefix ?? null;
  const openableOf = useCallback((changed: ChangedFile) => prefix !== null && checkout === null && changed.status !== "deleted" ? projectRelativePath(prefix, changed.path) : null,
    [prefix, checkout]);
  const openable = file ? openableOf(file) : null;
  // Beside the code editor, the editor the user chose: asked for once the tab is shown.
  const external = useExternalOpener(visible);
  const externalOpener = external.opener, externalEditor = external.editor, projectId = tab.projectId;
  const externalFiles = useMemo(() => externalOpener && externalEditor
    ? { editor: externalEditor, open: (path: string) => void externalOpener.openEntry("editor", projectId, path) } : null, [externalOpener, externalEditor, projectId]);
  const appearance = file ? fileAppearance(file.path, false) : null;
  // A file that exists on one side only has nothing to put beside it.
  const oneSided = !!compared && (compared.originalState === "absent" || compared.modifiedState === "absent");
  // The comments of the file shown alone: on both sides of its diff, or on the one side of a file that is new or gone.
  const [diffEditors, setDiffEditors] = useState<DiffEditors | null>(null);
  const [diffInline, setDiffInline] = useState(false);
  const [singleEditor, setSingleEditor] = useState<monaco.editor.ICodeEditor | null>(null);
  const shownCompared = !all && compared && (!current || current.value === compared) ? compared : null;
  const fileComments = useDiffComments({ editors: shownCompared && !oneSided ? diffEditors : null, inline: diffInline || !preferences.sideBySide,
    single: shownCompared && oneSided ? { editor: singleEditor, side: shownCompared.modifiedState === "absent" ? "old" : "new" } : null,
    path: shownCompared?.path ?? file?.path ?? "", unfold: (side, line) => diff.current?.reveal(side, line), review: reviewValue });
  const folders = layout === "tree" ? visibleRows.filter(row => row.kind === "folder") : [];
  const allCollapsed = folders.length > 0 && folders.every(row => row.kind === "folder" && row.collapsed);

  // The checkouts of the repository, once it has a worktree: a repository without one shows nothing more than before.
  const shownWorktree = checkouts ? shownCheckout(checkouts, checkout) : null;
  const projectSessions = useMemo(() => (sessions ?? []).filter(session => session.projectId === tab.projectId), [sessions, tab.projectId]);
  const worktreeRows = epoch && checkouts && checkouts.length > 1 && <WorktreeList epoch={epoch} projectId={tab.projectId} projectName={projectName ?? tab.projectPath}
    worktrees={checkouts} selected={checkout} api={trees}
    sessions={worktree => worktree.project ? 0 : worktreeSessions(projectSessions, worktree).length}
    onSelect={worktree => showCheckout(checkoutFolder(worktree))}
    onRemoved={worktree => {
      if (sameFolder(worktree.folder, checkout)) showCheckout(null);
      refresh.current(true);
      onWorktreesChanged?.();
    }} />;
  const splitter = preferences.sidebarVisible && <div className="changes-splitter" role="separator" aria-orientation="vertical" aria-label={t("Resize the list of files")} onPointerDown={event => resize(event)}
    onDoubleClick={() => update({ listWidth: 280 })} />;
  const branchTitle = list ? t(list.detached ? "Detached at {branch}" : "Branch {branch}", { branch: list.branch }) : "";

  // What is compared: the uncommitted changes first, then the whole branch where it has a base, then the commits.
  const historyRows = history && history.commits.length > 0 && <>
    <div className="changes-history-splitter" role="separator" aria-orientation="horizontal" aria-label={t("Resize the history")}
      onPointerDown={event => resize(event, true)} onDoubleClick={() => update({ historyHeight: 220 })} />
    <section className="changes-history" style={{ height: preferences.historyHeight }} aria-label={t("History")}>
      <h3>{t("History")}</h3>
      <div className="changes-history-rows" role="listbox" aria-label={t("History")}>
        <button type="button" role="option" className="changes-history-row" data-scope="head" aria-selected={scope.kind === "head"} tabIndex={-1}
          onClick={() => setScope({ kind: "head" })}>
          <span className="changes-history-mark" aria-hidden="true" />
          <span className="changes-history-text"><strong>{t("Uncommitted changes")}</strong></span>
        </button>
        {list?.baseReference && !!list.baseAhead && <button type="button" role="option" className="changes-history-row" data-scope="branch"
          aria-selected={scope.kind === "branch"} tabIndex={-1} onClick={() => setScope({ kind: "branch" })}>
          <span className="changes-history-mark" aria-hidden="true" />
          <span className="changes-history-text"><strong>{t("Since {reference}", { reference: list.baseReference })}</strong>
            <small><span>{t(list.baseAhead === 1 ? "{count} commit" : "{count} commits", { count: list.baseAhead })}</span></small></span>
        </button>}
        {history.commits.map(commit => <CommitRow key={commit.id} commit={commit} selected={scope.kind === "commit" && scope.id === commit.id} onSelect={selectCommit.current} />)}
        {history.more && historyLimit < commitLimitMaximum && <button type="button" className="changes-history-more"
          onClick={() => setHistoryLimit(limit => Math.min(commitLimitMaximum, limit + commitPageSize))}>{t("Load more")}</button>}
      </div>
    </section>
  </>;

  // In the view of all files: what is compared (another comparison is another view), where the selected file is
  // among the files, and whether every file is folded.
  const comparison = JSON.stringify([checkout, scopeKey]);
  const position = all ? shown.findIndex(value => value.path === selected) + 1 : 0;
  const allFolded = shown.length > 0 && shown.every(value => folded.has(value.path));
  // One file at a time, or all of them in one view: chosen where the diff is, and in the settings.
  const viewChoice = <ButtonGroup className="changes-mode">
    {changesViews.map(view => <Button key={view} variant="minimal" size="small" active={preferences.view === view} aria-pressed={preferences.view === view}
      icon={<AppIcon name={view === "all" ? "files" : "fileGeneric"} size={14} />} aria-label={t(changesViewLabel(view))} title={t(changesViewLabel(view))}
      onClick={() => update({ view })} />)}
  </ButtonGroup>;
  const sideChoice = <ButtonGroup className="changes-view">
    <Button variant="minimal" size="small" active={preferences.sideBySide} icon={<AppIcon name="columns" size={14} />} aria-pressed={preferences.sideBySide}
      aria-label={t("Side by side")} title={t("Side by side")} onClick={() => update({ sideBySide: true })} />
    <Button variant="minimal" size="small" active={!preferences.sideBySide} icon={<AppIcon name="rows" size={14} />} aria-pressed={!preferences.sideBySide}
      aria-label={t("Inline")} title={t("Inline")} onClick={() => update({ sideBySide: false })} />
  </ButtonGroup>;
  // What is set once and left alone stays out of the header. `path` is the file whose path can be copied.
  const options = (path: string | null) => <PopoverNext placement="bottom-end" content={<Menu className="changes-options">
    <MenuItem roleStructure="listoption" selected={preferences.collapseUnchanged} shouldDismissPopover={false} icon={<AppIcon name="fold" size={15} />}
      text={t("Hide unchanged lines")} onClick={() => update({ collapseUnchanged: !preferences.collapseUnchanged })} />
    <MenuItem roleStructure="listoption" selected={preferences.ignoreWhitespace} shouldDismissPopover={false} icon={<AppIcon name="whitespace" size={15} />}
      text={t("Ignore whitespace changes")} onClick={() => update({ ignoreWhitespace: !preferences.ignoreWhitespace })} />
    <MenuItem roleStructure="listoption" selected={preferences.wrap} shouldDismissPopover={false} icon={<AppIcon name="wrap" size={15} />}
      text={t("Wrap lines")} onClick={() => update({ wrap: !preferences.wrap })} />
    {path !== null && <><MenuDivider />
      <MenuItem icon={<AppIcon name={copied ? "check" : "copy"} size={15} />} text={t("Copy path")} shouldDismissPopover={false}
        onClick={() => void navigator.clipboard.writeText(path).then(() => { setCopied(true); window.setTimeout(() => setCopied(false), 1400); }, () => { })} /></>}
  </Menu>}>
    <Button variant="minimal" size="small" icon={<AppIcon name="ellipsis" size={15} />} aria-label={t("Diff options")} title={t("Diff options")} />
  </PopoverNext>;

  // The review bar: what is reviewed, where, how far the user went, who asked for it; Submit goes to the one who asked.
  const requester = reviewList.request ? sessions?.find(session => session.id === reviewList.request!.sessionId) ?? null : null;
  const reviewBar = reviewing && reviews && reviewScope && reviewTarget && list && <ReviewBar showPaths threads={reviewList.threads} readOnly={reviewList.readOnly}
    title={<span className="review-bar-file">{reviewTarget.kind === "branch" ? t("Since {reference}", { reference: list.baseReference ?? "" }) : t("Uncommitted changes")}</span>}
    detail={<>{shownWorktree ? shownWorktree.name : projectName ?? ""}{list.files.length > 0 && ` · ${t("{viewed}/{count} files viewed", { viewed: viewed.size, count: list.files.length })}`}
      {reviewList.request && <span className="review-bar-request"> · {t("Review requested by {session}", { session: requester?.title ?? reviewList.request.title ?? t("an agent") })}</span>}</>}
    onReveal={revealThread} onResolveAddressed={async () => (await reviews.resolveAddressed(reviewScope, reviewTarget)).ok}
    submit={<SubmitReviewButton disabled={!!reviewList.readOnly} comments={threadCounts(reviewList.threads)}
      sessions={reviewSessions(sessions ?? [], tab.projectId, checkout, reviewList.request?.sessionId ?? null)}
      fixed={reviewList.request ? { sessionId: reviewList.request.sessionId, title: requester?.title ?? reviewList.request.title ?? reviewList.request.sessionId } : null}
      onSubmit={reviewSubmitter(reviews, reviewScope, reviewTarget, t, onOpenSession ?? null)} />} />;
  const reviewToggle = reviewTarget && reviews && epoch && <Button variant="minimal" size="small" className="changes-review-toggle" icon={<AppIcon name="chat" size={14} />}
    active={reviewing} aria-pressed={reviewing} title={t(reviewing ? "Stop reviewing: the diffs take no comments" : "Review: comment on the lines of the diffs")}
    onClick={() => setReviewChoice(!reviewing)}>{t("Review")}</Button>;
  const sideViews = reviewing && <div className="changes-side-views" role="tablist" aria-label={t("Changed files")}>
    <button type="button" role="tab" className="changes-side-view" aria-selected={sideView === "files"} onClick={() => setSideView("files")}>
      <AppIcon name="files" size={13} /><span>{t("Files")}</span></button>
    <button type="button" role="tab" className="changes-side-view" aria-selected={sideView === "comments"} onClick={() => setSideView("comments")}>
      <AppIcon name="chat" size={13} /><span>{t("Comments")}</span><span className="changes-side-count">{threadCounts(reviewList.threads).active}</span></button>
  </div>;
  const commentsView = <div className="changes-comments">
    <ReviewCommentsList threads={reviewList.threads} filter={commentsFilter} onFilter={setCommentsFilter} onReveal={revealThread} selected={wanted?.thread.id ?? null} />
  </div>;

  return <ChangesReviewContext.Provider value={reviewValue}><section ref={root} className="changes-panel" data-active={active} data-review={reviewing || undefined} aria-label={`${t("Changes")} · ${projectName ?? tab.projectPath}`}
    {...{ [changeKeysAttribute]: "" }} onFocusCapture={onActivate} onPointerDownCapture={onActivate} onKeyDownCapture={panelKeyDown}>
    <header className="changes-header">
      <Button ref={sidebarToggle} variant="minimal" size="small" className="changes-side-toggle" icon={<AppIcon name="sidebar" size={15} />}
        active={preferences.sidebarVisible} aria-expanded={preferences.sidebarVisible}
        aria-controls={worktreeRows || list && (list.files.length > 0 || historyRows) ? sidebarId : undefined}
        aria-label={t(preferences.sidebarVisible ? "Hide the files" : "Show the files")} title={t(preferences.sidebarVisible ? "Hide the files" : "Show the files")}
        onClick={() => update({ sidebarVisible: !preferences.sidebarVisible })} />
      <span className="changes-project" title={list?.root ?? tab.projectPath}>
        <span className="changes-project-icon"><AppIcon name="changes" size={15} /></span>
        <strong>{projectName ?? t("Unavailable project")}</strong>
        {shownWorktree && <span className="changes-worktree" title={shownWorktree.path}><AppIcon name="worktree" size={13} />{shownWorktree.name}</span>}
        <span className="changes-project-path">{list?.root ?? shownWorktree?.path ?? tab.projectPath}</span>
      </span>
      {list && (epoch
        ? <BranchSwitcher epoch={epoch} projectId={tab.projectId} worktree={checkout} className="changes-branch" placement="bottom-start" api={trees}
          title={`${branchTitle}\n${t("Switch branch")}`} onSwitched={() => refresh.current(true)}><AppIcon name="branch" size={13} /><span>{list.branch}</span></BranchSwitcher>
        : <span className="changes-branch" title={branchTitle}><AppIcon name="branch" size={13} /><span>{list.branch}</span></span>)}
      {scope.kind !== "head" && <span className="changes-scope" title={shownCommit ? `${shownCommit.shortId} · ${shownCommit.subject}` : undefined}>
        {scope.kind === "branch" ? t("Since {reference}", { reference: list?.baseReference ?? "" })
          : <><code>{shownCommit?.shortId ?? scope.id.slice(0, 7)}</code>{shownCommit && <span>{shownCommit.subject}</span>}</>}</span>}
      <span className="changes-spacer" />
      {list && <span className="changes-totals" role="status">
        <span>{t(list.truncated ? "{count}+ files" : list.files.length === 1 ? "{count} file" : "{count} files", { count: list.files.length.toLocaleString(locale) })}</span>
        <Counts insertions={list.insertions} deletions={list.deletions} />
      </span>}
      {reviewToggle}
      <Switch className="changes-auto" checked={preferences.autoRefresh} label={t("Auto-refresh")}
        onChange={event => update({ autoRefresh: event.currentTarget.checked })} />
      <Button variant="minimal" size="small" className="changes-refresh" icon={busy ? <ActivitySpinner size={14} /> : <AppIcon name="refresh" size={14} />}
        aria-label={t("Refresh")} title={t("Refresh")} disabled={!epoch} onClick={() => refresh.current(true)} />
    </header>
    {reviewBar}
    {!list ? (() => {
        const state = <NonIdealState className="changes-empty" icon={failure ? <AppIcon name="changes" size={36} /> : <ActivitySpinner size={28} />}
          title={failure ? t("No changes to show") : t("Loading…")} description={failure ? t(failureText(failure)) : undefined}
          action={failure ? <Button icon={<AppIcon name="refresh" size={15} />} disabled={!epoch} onClick={() => refresh.current(true)}>{t("Refresh")}</Button> : undefined} />;
        // The checkouts stay within reach while one of them has nothing to show.
        return worktreeRows ? <div className="changes-body"><aside id={sidebarId} className="changes-files" hidden={!preferences.sidebarVisible} style={{ width: preferences.listWidth }}>{worktreeRows}<div className="changes-rows" /></aside>
          {splitter}{state}</div> : state;
      })()
      : list.files.length === 0 ? <div className="changes-body">
        {(historyRows || worktreeRows) && <><aside id={sidebarId} className="changes-files" hidden={!preferences.sidebarVisible} style={{ width: preferences.listWidth }}>{worktreeRows}<div className="changes-rows" />{historyRows}</aside>
          {splitter}</>}
        <NonIdealState className="changes-empty" icon={<AppIcon name="checked" size={36} />} title={t("No changes")}
          description={t(list.comparison === "commit" ? "This commit changed no file." : list.comparison === "branch" ? "The work tree matches the base of the branch."
            : "The work tree matches the last commit.")} /></div>
      : <div className="changes-body">
        <aside id={sidebarId} className="changes-files" hidden={!preferences.sidebarVisible} style={{ width: preferences.listWidth }}>
          {worktreeRows}
          {sideViews}
          {reviewing && sideView === "comments" ? commentsView : <>
          <div className="changes-files-toolbar">
            <InputGroup className="changes-filter" size="small" type="search" leftIcon={<AppIcon name="search" size={14} className="bp6-icon" />} placeholder={t("Filter files")}
              aria-label={t("Filter files")} value={filter} onValueChange={setFilter}
              onKeyDown={event => { if (event.key === "ArrowDown") { event.preventDefault(); rows.current?.focus(); move(selected === null ? 1 : 0); } }} />
            {layout === "tree" && <Button variant="minimal" size="small" disabled={!folders.length || !!filter.trim()}
              icon={<AppIcon name={allCollapsed ? "unfold" : "fold"} size={14} />}
              aria-label={t(allCollapsed ? "Expand all folders" : "Collapse all folders")} title={t(allCollapsed ? "Expand all folders" : "Collapse all folders")}
              onClick={() => setCollapsed(allCollapsed ? new Set() : new Set(changeTreeRows(shown, new Set()).filter(row => row.kind === "folder").map(row => (row as { path: string }).path)))} />}
            <ButtonGroup className="changes-layout">
              <Button variant="minimal" size="small" active={layout === "tree"} icon={<AppIcon name="tree" size={14} />} aria-label={t("Show as a tree")}
                title={t("Show as a tree")} aria-pressed={layout === "tree"} onClick={() => update({ layout: "tree" })} />
              <Button variant="minimal" size="small" active={layout === "list"} icon={<AppIcon name="list" size={14} />} aria-label={t("Show as a list")}
                title={t("Show as a list")} aria-pressed={layout === "list"} onClick={() => update({ layout: "list" })} />
            </ButtonGroup>
          </div>
          <div ref={rows} className="changes-rows" role="listbox" tabIndex={0} aria-label={t("Changed files")} onKeyDown={listKeyDown}>
            {visibleRows.map(row => row.kind === "folder" ? <FolderRow key={row.key} row={row} onToggle={toggle.current} />
              : <FileRow key={row.key} row={row} flat={layout === "list"} selected={row.file.path === selected} onSelect={select.current}
                threads={reviewing ? fileThreads.get(row.file.path) ?? null : null} viewed={viewed.has(row.file.path)} />)}
            {!visibleRows.length && <p className="changes-no-match">{t("No file matches the filter.")}</p>}
            {list.truncated && <p className="changes-no-match">{t("More files changed than this list shows.")}</p>}
          </div></>}
          {historyRows}
        </aside>
        {splitter}
        <section className="changes-diff" data-view={preferences.view} aria-label={all ? t("Changes") : file?.path ?? t("Changes")}>
          {all ? <div className="changes-diff-header">
            <span className="changes-diff-path"><strong>{t("All files")}</strong></span>
            <span className="changes-spacer" />
            <span className="changes-navigation">
              <span className="changes-position">{position > 0 ? t("{current} of {count}", { current: position, count: shown.length })
                : t(shown.length === 1 ? "{count} file" : "{count} files", { count: shown.length.toLocaleString(locale) })}</span>
              <Button variant="minimal" size="small" icon={<AppIcon name="arrowUp" size={14} />} disabled={position <= 1}
                aria-label={t("Previous file")} title={`${t("Previous file")} (Alt+↑)`} onClick={() => move(-1)} />
              <Button variant="minimal" size="small" icon={<AppIcon name="arrowDown" size={14} />} disabled={!shown.length || position === shown.length}
                aria-label={t("Next file")} title={`${t("Next file")} (Alt+↓)`} onClick={() => move(1)} />
            </span>
            {viewChoice}{sideChoice}
            <Button variant="minimal" size="small" icon={<AppIcon name={allFolded ? "unfold" : "fold"} size={14} />} disabled={!shown.length}
              aria-label={t(allFolded ? "Expand all files" : "Collapse all files")} title={t(allFolded ? "Expand all files" : "Collapse all files")}
              onClick={() => setFolded(allFolded ? new Set() : new Set(shown.map(value => value.path)))} />
            {options(null)}
          </div>
          : file && appearance && <div className="changes-diff-header">
            <span className="changes-status" data-status={file.status} title={t(changeLabel(file.status))}>{changeLetter(file.status)}</span>
            <span className="file-tab-icon" data-file-tone={appearance.tone}><AppIcon name={appearance.icon} size={14} /></span>
            <span className="changes-diff-path" title={file.originalPath ? `${file.originalPath} → ${file.path}` : file.path}>
              <strong>{changeFileName(file.path)}</strong>{changeFolder(file.path) && <small>{changeFolder(file.path)}</small>}
              {file.originalPath && <small className="changes-renamed">{t("Renamed from {path}", { path: file.originalPath })}</small>}
            </span>
            <Counts insertions={file.insertions} deletions={file.deletions} />
            <ChangeBar insertions={file.insertions} deletions={file.deletions} />
            <span className="changes-spacer" />
            {!notice && current?.value && !oneSided && <span className="changes-navigation">
              <span className="changes-position">{changes.count === 0 ? t("No difference") : changes.current > 0
                ? t("{current} of {count}", { current: changes.current, count: changes.count })
                : t(changes.count === 1 ? "{count} change" : "{count} changes", { count: changes.count })}</span>
              <Button variant="minimal" size="small" icon={<AppIcon name="arrowUp" size={14} />} disabled={changes.count === 0}
                aria-label={t("Previous change")} title={`${t("Previous change")} (Alt+↑)`} onClick={() => diff.current?.go("previous")} />
              <Button variant="minimal" size="small" icon={<AppIcon name="arrowDown" size={14} />} disabled={changes.count === 0}
                aria-label={t("Next change")} title={`${t("Next change")} (Alt+↓)`} onClick={() => diff.current?.go("next")} />
            </span>}
            {reviewing && <><ViewedCheck path={file.path} revision={file.revision} /><FileCommentButton path={file.path} /></>}
            {viewChoice}{!oneSided && sideChoice}
            <Button variant="minimal" size="small" icon={<AppIcon name="code" size={14} />} disabled={openable === null}
              aria-label={t("Open file")} title={t("Open file")} onClick={() => { if (openable !== null) onOpenFile(openable); }} />
            {externalFiles && <Button variant="minimal" size="small" icon={<ExternalEditorIcon id={externalFiles.editor.id} size={14} />} disabled={openable === null}
              aria-label={t("Open {name} in {editor}", { name: changeFileName(file.path), editor: externalFiles.editor.name })} title={t("Open in {editor}", { editor: externalFiles.editor.name })}
              onClick={() => { if (openable !== null) externalFiles.open(openable); }} />}
            {options(file.path)}
          </div>}
          {!all && fileComments.notes}
          <div className="changes-diff-surface">
            {all ? listed !== scopeKey ? <NonIdealState className="changes-empty" icon={<ActivitySpinner size={28} />} title={t("Loading…")} />
              : !shown.length ? <NonIdealState className="changes-empty" icon={<AppIcon name="search" size={36} />} title={t("No file matches the filter.")} />
              : <AllChanges key={comparison} files={shown} scope={comparison} visible={visible && !!epoch} look={look}
                current={selected} reveal={reveal} collapsed={folded} truncated={list.truncated} read={readFile} openable={openableOf} external={externalFiles} handle={allFiles}
                onCurrent={follow.current} onToggle={fold.current} onOpenFile={open.current} onStale={stale.current} />
              : !file ? <NonIdealState className="changes-empty" icon={<AppIcon name="changes" size={36} />} title={t("Select a file to see its changes")} />
              : compared && oneSided && (!current || current.value === compared)
                // A file that is new or gone has nothing to compare with: it is shown whole, tinted as added or removed.
                ? <div className="changes-diff-editor changes-one-sided" data-pending={!current} data-side={compared.modifiedState === "absent" ? "removed" : "added"}>
                  <CodeEditor key={content!.key} value={compared.modifiedState === "absent" ? compared.original : compared.modified} onChange={ignore}
                    language={fileLanguage(compared.path)} label={compared.path} readOnly wrap={preferences.wrap} comments={reviewing} onEditor={setSingleEditor} /></div>
              : compared && (!current || current.value === compared) ? <div className="changes-diff-editor" data-pending={!current}>
                <DiffEditor documentKey={content!.key} original={compared.original} modified={compared.modified} language={fileLanguage(compared.path)}
                  label={compared.path} sideBySide={preferences.sideBySide && !oneSided} wrap={preferences.wrap} ignoreWhitespace={preferences.ignoreWhitespace}
                  collapseUnchanged={preferences.collapseUnchanged && !oneSided} onChanges={setChanges} handle={diff}
                  comments={reviewing} onEditors={setDiffEditors} onInline={setDiffInline} /></div>
              : !current ? <NonIdealState className="changes-empty" icon={<ActivitySpinner size={28} />} title={t("Loading…")} />
              : !current.value ? <NonIdealState className="changes-empty" icon={<AppIcon name="error" size={36} />} title={changeFileName(file.path)}
                description={t(current.failure === "not_changed" ? "The file has no changes any more." : "The file could not be read.")} />
              : <NonIdealState className="changes-empty" icon={<AppIcon name={appearance!.icon} size={36} />} title={changeFileName(file.path)}
                description={t(notice === "binary" ? "Binary file: there is no text to compare." : notice === "too_large" ? "The file is too large to compare here."
                  : "The file could not be read.")} />}
          </div>
        </section>
      </div>}
    {fileComments.portals}
  </section></ChangesReviewContext.Provider>;
}
