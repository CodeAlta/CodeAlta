import { memo, useEffect, useLayoutEffect, useMemo, useRef, useState, type KeyboardEvent, type PointerEvent } from "react";
import { Button, ButtonGroup, InputGroup, Menu, MenuDivider, MenuItem, NonIdealState, PopoverNext, Switch } from "@blueprintjs/core";
import { projectGit } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { CodeEditor } from "../monaco/CodeEditor";
import { DiffEditor, type DiffEditorChanges, type DiffEditorHandle } from "../monaco/DiffEditor";
import { fileAppearance } from "../fileAppearance";
import { fileLanguage } from "../monaco/fileLanguage";
import type { FileTab } from "../fileTabs";
import { changeBar, changeCommitsReply, changeContent, changeContentNotice, changeFileName, changeFolder, changeHistoryHeight, changeLabel, changeLetter,
  changeListReply, changeListRows, changeListWidth, changeScopeKey, changesPreferencesKey, changeTreeRows, commitLimitMaximum, commitPageSize, filterChanges,
  orderChanges, persistChangesPreferences, projectRelativePath, restoreChangesPreferences, selectedChange, type ChangeCommit, type ChangeCommits,
  type ChangeContent, type ChangedFile, type ChangeList, type ChangeRow, type ChangeScope, type ChangesPreferences } from "./projectChanges";
import { sessionTime } from "../sessionTime";
import { useShellLanguage } from "../shellLanguage";

const autoRefreshMilliseconds = 5000;
const ignore = () => { };
const modalOpen = () => !!document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]');
// A refusal that will not go away by asking again: the list it replaces is no longer shown.
const lasting = new Set(["not_repository", "unknown_project", "project_unavailable", "unavailable", "invalid"]);

const failures = {
  not_repository: "This folder is not in a git repository.",
  git_failed: "Git could not list the changes. Check that git is installed and that it trusts this folder.",
  unknown_project: "The project is no longer available.",
  project_unavailable: "The project is no longer available.",
  unavailable: "Changes cannot be read in this window.",
} as const;
const failureText = (status: string) => (failures as Record<string, typeof failures[keyof typeof failures] | undefined>)[status] ?? "The changes could not be read.";

function Counts({ insertions, deletions }: { insertions: number | null; deletions: number | null }) {
  const { locale } = useShellLanguage();
  if (insertions === null && deletions === null) return null;
  return <span className="changes-counts"><b>+{(insertions ?? 0).toLocaleString(locale)}</b><em>−{(deletions ?? 0).toLocaleString(locale)}</em></span>;
}

function ChangeBar({ insertions, deletions }: { insertions: number | null; deletions: number | null }) {
  const bar = changeBar(insertions, deletions);
  return <span className="changes-bar" aria-hidden="true">{[0, 1, 2, 3, 4].map(index =>
    <i key={index} data-change={index < bar.added ? "added" : index < bar.added + bar.removed ? "removed" : undefined} />)}</span>;
}

const FileRow = memo(function FileRow({ row, selected, flat, onSelect }: {
  row: Extract<ChangeRow, { kind: "file" }>; selected: boolean; flat: boolean; onSelect: (path: string) => void;
}) {
  const { t } = useShellLanguage();
  const look = fileAppearance(row.file.path, false);
  return <button type="button" role="option" className="changes-row changes-file" data-status={row.file.status} data-path={row.file.path}
    aria-selected={selected} tabIndex={-1} style={{ paddingLeft: 8 + row.depth * 14 }}
    title={`${row.file.path}\n${t(changeLabel(row.file.status))}${row.file.originalPath ? `\n${t("Renamed from {path}", { path: row.file.originalPath })}` : ""}`}
    onClick={() => onSelect(row.file.path)}>
    <span className="file-tab-icon" data-file-tone={look.tone}><AppIcon name={look.icon} size={14} /></span>
    <span className="changes-row-name"><strong>{row.name}</strong>{flat && row.folder && <small>{row.folder}</small>}</span>
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
 */
export function ProjectChangesPanel({ tab, projectName, epoch, visible, active, onActivate, onOpenFile, request, api = projectGit }: {
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
  /** The file an `alta diff show` asked for; a new object for each request. */
  request?: Readonly<{ path: string | null }> | null;
  api?: Pick<typeof projectGit, "changes" | "file" | "commits">;
}) {
  const { t, locale } = useShellLanguage();
  const [preferences, setPreferences] = useState(() => restoreChangesPreferences(() => localStorage.getItem(changesPreferencesKey)));
  const [list, setList] = useState<ChangeList | null>(null);
  const [failure, setFailure] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [scope, setScope] = useState<ChangeScope>({ kind: "head" });
  const [history, setHistory] = useState<ChangeCommits | null>(null);
  const [historyLimit, setHistoryLimit] = useState(commitPageSize);
  const [selected, setSelected] = useState<string | null>(null);
  const [filter, setFilter] = useState("");
  const [collapsed, setCollapsed] = useState<ReadonlySet<string>>(() => new Set());
  const [content, setContent] = useState<{ key: string; listed: string; value: ChangeContent | null; failure: string | null } | null>(null);
  const [changes, setChanges] = useState<DiffEditorChanges>({ count: 0, current: 0 });
  const [copied, setCopied] = useState(false);
  const diff = useRef<DiffEditorHandle | null>(null);
  const rows = useRef<HTMLDivElement>(null);
  const root = useRef<HTMLElement>(null);
  const reading = useRef(false), again = useRef(false);
  const scopeKey = changeScopeKey(scope);
  // What the last list was read for: another comparison never reuses its revision.
  const latest = useRef({ list, scope, listed: scopeKey, selected, wanted: null as string | null, history, historyLimit, historyRead: 0 });
  latest.current.list = list; latest.current.scope = scope; latest.current.selected = selected;
  latest.current.history = history; latest.current.historyLimit = historyLimit;

  function update(change: Partial<ChangesPreferences>) {
    setPreferences(current => {
      const next = { ...current, ...change };
      persistChangesPreferences(value => localStorage.setItem(changesPreferencesKey, value), next);
      return next;
    });
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
      void api.commits({ expectedEpoch: epoch, projectId: tab.projectId, limit, knownRevision: knownHistory }, { signal: abort.signal, timeoutMilliseconds: 30_000 })
        .then(reply => changeCommitsReply(reply, tab.projectId, knownHistory), () => null)
        .then(value => {
          if (abort.signal.aborted || !value || value === "unchanged") return;
          latest.current.history = value; latest.current.historyRead = limit;
          setHistory(value);
        });
      void api.changes({ expectedEpoch: epoch, projectId: tab.projectId, comparison: scope.kind, commit: scope.kind === "commit" ? scope.id : null, knownRevision: known },
        { signal: abort.signal, timeoutMilliseconds: 30_000 })
        .then(reply => changeListReply(reply, tab.projectId, known), () => ({ kind: "failed" as const, status: "read_failed" }))
        .then(reply => {
          if (abort.signal.aborted || changeScopeKey(latest.current.scope) !== asked) return;
          if (reply.kind === "list") {
            // Kept in the order they are shown, so that the first file and the next one are the ones on screen.
            const read = { ...reply.list, files: orderChanges(reply.list.files) };
            const wanted = latest.current.wanted;
            const next = wanted !== null && read.files.some(file => file.path === wanted) ? wanted
              : selectedChange(latest.current.list?.files ?? [], read.files, latest.current.selected);
            latest.current.wanted = null; latest.current.listed = asked; latest.current.list = read;
            setList(read); setFailure(null); setSelected(next);
            // The host fell back to the uncommitted changes: the branch has no commit of its own any more.
            if (read.comparison !== scope.kind) setScope({ kind: "head" });
          } else if (reply.kind === "failed" && (lasting.has(reply.status) || !latest.current.list || latest.current.listed !== asked)) {
            // Nothing to keep: no list yet, or the one on screen is that of another comparison.
            latest.current.list = null;
            setList(null); setFailure(reply.status);
          }
        })
        .finally(() => {
          reading.current = false;
          if (abort.signal.aborted) return;
          setBusy(false);
          if (again.current) { again.current = false; refresh.current(); }
        });
    };
    return () => { abort.abort(); reading.current = false; refresh.current = () => { }; };
  }, [api, epoch, tab.projectId]);

  useEffect(() => {
    if (!visible || !epoch) return;
    refresh.current();
    const focused = () => refresh.current();
    window.addEventListener("focus", focused);
    const timer = preferences.autoRefresh
      ? window.setInterval(() => { if (document.visibilityState !== "hidden") refresh.current(); }, autoRefreshMilliseconds) : undefined;
    return () => { window.removeEventListener("focus", focused); window.clearInterval(timer); };
  }, [visible, epoch, scopeKey, historyLimit, preferences.autoRefresh, tab.projectId]);

  // A file an agent asked for is selected now, or once a list names it.
  useEffect(() => {
    const path = request?.path ?? null;
    if (path === null) return;
    if (latest.current.list?.files.some(file => file.path === path)) setSelected(path);
    else latest.current.wanted = path;
    refresh.current();
  }, [request]);

  const file = useMemo(() => list?.files.find(value => value.path === selected) ?? null, [list, selected]);
  const contentKey = file ? JSON.stringify([scopeKey, file.path]) : null;
  // Both sides of the selected file, read again when the list says that one of them changed.
  useEffect(() => {
    if (!file || !contentKey || !epoch || !visible) return;
    if (content?.key === contentKey && (content.listed === file.revision || content.value?.revision === file.revision)) return;
    const abort = new AbortController();
    void api.file({ expectedEpoch: epoch, projectId: tab.projectId, comparison: scope.kind, commit: scope.kind === "commit" ? scope.id : null, path: file.path },
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
  }, [api, epoch, visible, tab.projectId, scopeKey, contentKey, file?.revision]);

  const shown = useMemo(() => list ? filterChanges(list.files, filter) : [], [list, filter]);
  const layout = preferences.layout;
  const visibleRows = useMemo(() => layout === "tree" ? changeTreeRows(shown, filter.trim() ? new Set() : collapsed) : changeListRows(shown),
    [shown, layout, collapsed, filter]);
  const fileRows = useMemo(() => visibleRows.filter((row): row is Extract<ChangeRow, { kind: "file" }> => row.kind === "file"), [visibleRows]);

  const select = useRef((path: string) => { setSelected(path); });
  const selectCommit = useRef((id: string) => setScope({ kind: "commit", id }));
  const shownCommit = scope.kind === "commit" ? history?.commits.find(commit => commit.id === scope.id) ?? null : null;
  const toggle = useRef((path: string) => setCollapsed(current => {
    const next = new Set(current);
    if (!next.delete(path)) next.add(path);
    return next;
  }));
  useLayoutEffect(() => {
    if (selected !== null) rows.current?.querySelector<HTMLElement>('[aria-selected="true"]')?.scrollIntoView({ block: "nearest" });
  }, [selected, layout]);
  // The active tab takes the keyboard in its list of files, unless a window is open over the workspace.
  useEffect(() => { if (active && !modalOpen() && !root.current?.contains(document.activeElement)) rows.current?.focus(); }, [active]);

  function move(delta: number) {
    if (!fileRows.length) return;
    const index = fileRows.findIndex(row => row.file.path === selected);
    const next = index < 0 ? (delta > 0 ? 0 : fileRows.length - 1) : Math.min(fileRows.length - 1, Math.max(0, index + delta));
    setSelected(fileRows[next].file.path);
  }
  function listKeyDown(event: KeyboardEvent<HTMLElement>) {
    if (event.defaultPrevented || event.ctrlKey || event.metaKey || event.altKey || event.nativeEvent.isComposing) return;
    if (event.key === "ArrowDown") move(1);
    else if (event.key === "ArrowUp") move(-1);
    else if (event.key === "Home") move(-fileRows.length);
    else if (event.key === "End") move(fileRows.length);
    else if (event.key === "PageDown") move(10);
    else if (event.key === "PageUp") move(-10);
    else if (event.key === "Enter" || event.key === "ArrowRight") diff.current?.focus();
    else return;
    event.preventDefault();
  }
  // Alt+Down and Alt+Up go through the changes of the file, from the list as well as from the diff: taken before
  // the editor, where they would move a line.
  function panelKeyDown(event: KeyboardEvent<HTMLElement>) {
    if (event.defaultPrevented || !event.altKey || event.ctrlKey || event.metaKey || event.shiftKey || (event.key !== "ArrowDown" && event.key !== "ArrowUp")) return;
    event.preventDefault(); event.stopPropagation();
    diff.current?.go(event.key === "ArrowDown" ? "next" : "previous");
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
  const openable = file && list && file.status !== "deleted" ? projectRelativePath(list.prefix, file.path) : null;
  const look = file ? fileAppearance(file.path, false) : null;
  // A file that exists on one side only has nothing to put beside it.
  const oneSided = !!compared && (compared.originalState === "absent" || compared.modifiedState === "absent");
  const folders = layout === "tree" ? visibleRows.filter(row => row.kind === "folder") : [];
  const allCollapsed = folders.length > 0 && folders.every(row => row.kind === "folder" && row.collapsed);

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

  return <section ref={root} className="changes-panel" data-active={active} aria-label={`${t("Changes")} · ${projectName ?? tab.projectPath}`}
    onFocusCapture={onActivate} onPointerDownCapture={onActivate} onKeyDownCapture={panelKeyDown}>
    <header className="changes-header">
      <span className="changes-project" title={list?.root ?? tab.projectPath}>
        <span className="changes-project-icon"><AppIcon name="changes" size={15} /></span>
        <strong>{projectName ?? t("Unavailable project")}</strong>
        <span className="changes-project-path">{list?.root ?? tab.projectPath}</span>
      </span>
      {list && <span className="changes-branch" title={t(list.detached ? "Detached at {branch}" : "Branch {branch}", { branch: list.branch })}>
        <AppIcon name="branch" size={13} /><span>{list.branch}</span></span>}
      {scope.kind !== "head" && <span className="changes-scope" title={shownCommit ? `${shownCommit.shortId} · ${shownCommit.subject}` : undefined}>
        {scope.kind === "branch" ? t("Since {reference}", { reference: list?.baseReference ?? "" })
          : <><code>{shownCommit?.shortId ?? scope.id.slice(0, 7)}</code>{shownCommit && <span>{shownCommit.subject}</span>}</>}</span>}
      <span className="changes-spacer" />
      {list && <span className="changes-totals" role="status">
        <span>{t(list.truncated ? "{count}+ files" : list.files.length === 1 ? "{count} file" : "{count} files", { count: list.files.length.toLocaleString(locale) })}</span>
        <Counts insertions={list.insertions} deletions={list.deletions} />
      </span>}
      <Switch className="changes-auto" checked={preferences.autoRefresh} label={t("Auto-refresh")}
        onChange={event => update({ autoRefresh: event.currentTarget.checked })} />
      <Button variant="minimal" size="small" className="changes-refresh" icon={busy ? <ActivitySpinner size={14} /> : <AppIcon name="refresh" size={14} />}
        aria-label={t("Refresh")} title={t("Refresh")} disabled={!epoch} onClick={() => refresh.current(true)} />
    </header>
    {!list ? <NonIdealState className="changes-empty" icon={failure ? <AppIcon name="changes" size={36} /> : <ActivitySpinner size={28} />}
        title={failure ? t("No changes to show") : t("Loading…")} description={failure ? t(failureText(failure)) : undefined}
        action={failure ? <Button icon={<AppIcon name="refresh" size={15} />} disabled={!epoch} onClick={() => refresh.current(true)}>{t("Refresh")}</Button> : undefined} />
      : list.files.length === 0 ? <div className="changes-body">
        {historyRows && <><aside className="changes-files" style={{ width: preferences.listWidth }}><div className="changes-rows" />{historyRows}</aside>
          <div className="changes-splitter" role="separator" aria-orientation="vertical" aria-label={t("Resize the list of files")} onPointerDown={event => resize(event)}
            onDoubleClick={() => update({ listWidth: 280 })} /></>}
        <NonIdealState className="changes-empty" icon={<AppIcon name="checked" size={36} />} title={t("No changes")}
          description={t(list.comparison === "commit" ? "This commit changed no file." : list.comparison === "branch" ? "The work tree matches the base of the branch."
            : "The work tree matches the last commit.")} /></div>
      : <div className="changes-body">
        <aside className="changes-files" style={{ width: preferences.listWidth }}>
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
              : <FileRow key={row.key} row={row} flat={layout === "list"} selected={row.file.path === selected} onSelect={select.current} />)}
            {!visibleRows.length && <p className="changes-no-match">{t("No file matches the filter.")}</p>}
            {list.truncated && <p className="changes-no-match">{t("More files changed than this list shows.")}</p>}
          </div>
          {historyRows}
        </aside>
        <div className="changes-splitter" role="separator" aria-orientation="vertical" aria-label={t("Resize the list of files")} onPointerDown={event => resize(event)}
          onDoubleClick={() => update({ listWidth: 280 })} />
        <section className="changes-diff" aria-label={file?.path ?? t("Changes")}>
          {file && look && <div className="changes-diff-header">
            <span className="changes-status" data-status={file.status} title={t(changeLabel(file.status))}>{changeLetter(file.status)}</span>
            <span className="file-tab-icon" data-file-tone={look.tone}><AppIcon name={look.icon} size={14} /></span>
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
            {!oneSided && <ButtonGroup className="changes-view">
              <Button variant="minimal" size="small" active={preferences.sideBySide} icon={<AppIcon name="columns" size={14} />} aria-pressed={preferences.sideBySide}
                aria-label={t("Side by side")} title={t("Side by side")} onClick={() => update({ sideBySide: true })} />
              <Button variant="minimal" size="small" active={!preferences.sideBySide} icon={<AppIcon name="rows" size={14} />} aria-pressed={!preferences.sideBySide}
                aria-label={t("Inline")} title={t("Inline")} onClick={() => update({ sideBySide: false })} />
            </ButtonGroup>}
            <Button variant="minimal" size="small" icon={<AppIcon name="openExternal" size={14} />} disabled={openable === null}
              aria-label={t("Open file")} title={t("Open file")} onClick={() => { if (openable !== null) onOpenFile(openable); }} />
            {/* What is set once and left alone stays out of the header. */}
            <PopoverNext placement="bottom-end" content={<Menu className="changes-options">
              <MenuItem roleStructure="listoption" selected={preferences.collapseUnchanged} shouldDismissPopover={false} icon={<AppIcon name="fold" size={15} />}
                text={t("Hide unchanged lines")} onClick={() => update({ collapseUnchanged: !preferences.collapseUnchanged })} />
              <MenuItem roleStructure="listoption" selected={preferences.ignoreWhitespace} shouldDismissPopover={false} icon={<AppIcon name="whitespace" size={15} />}
                text={t("Ignore whitespace changes")} onClick={() => update({ ignoreWhitespace: !preferences.ignoreWhitespace })} />
              <MenuItem roleStructure="listoption" selected={preferences.wrap} shouldDismissPopover={false} icon={<AppIcon name="wrap" size={15} />}
                text={t("Wrap lines")} onClick={() => update({ wrap: !preferences.wrap })} />
              <MenuDivider />
              <MenuItem icon={<AppIcon name={copied ? "check" : "copy"} size={15} />} text={t("Copy path")} shouldDismissPopover={false}
                onClick={() => void navigator.clipboard.writeText(file.path).then(() => { setCopied(true); window.setTimeout(() => setCopied(false), 1400); }, () => { })} />
            </Menu>}>
              <Button variant="minimal" size="small" icon={<AppIcon name="ellipsis" size={15} />} aria-label={t("Diff options")} title={t("Diff options")} />
            </PopoverNext>
          </div>}
          <div className="changes-diff-surface">
            {!file ? <NonIdealState className="changes-empty" icon={<AppIcon name="changes" size={36} />} title={t("Select a file to see its changes")} />
              : compared && oneSided && (!current || current.value === compared)
                // A file that is new or gone has nothing to compare with: it is shown whole, tinted as added or removed.
                ? <div className="changes-diff-editor changes-one-sided" data-pending={!current} data-side={compared.modifiedState === "absent" ? "removed" : "added"}>
                  <CodeEditor key={content!.key} value={compared.modifiedState === "absent" ? compared.original : compared.modified} onChange={ignore}
                    language={fileLanguage(compared.path)} label={compared.path} readOnly wrap={preferences.wrap} /></div>
              : compared && (!current || current.value === compared) ? <div className="changes-diff-editor" data-pending={!current}>
                <DiffEditor documentKey={content!.key} original={compared.original} modified={compared.modified} language={fileLanguage(compared.path)}
                  label={compared.path} sideBySide={preferences.sideBySide && !oneSided} wrap={preferences.wrap} ignoreWhitespace={preferences.ignoreWhitespace}
                  collapseUnchanged={preferences.collapseUnchanged && !oneSided} onChanges={setChanges} handle={diff} /></div>
              : !current ? <NonIdealState className="changes-empty" icon={<ActivitySpinner size={28} />} title={t("Loading…")} />
              : !current.value ? <NonIdealState className="changes-empty" icon={<AppIcon name="error" size={36} />} title={changeFileName(file.path)}
                description={t(current.failure === "not_changed" ? "The file has no changes any more." : "The file could not be read.")} />
              : <NonIdealState className="changes-empty" icon={<AppIcon name={look!.icon} size={36} />} title={changeFileName(file.path)}
                description={t(notice === "binary" ? "Binary file: there is no text to compare." : notice === "too_large" ? "The file is too large to compare here."
                  : "The file could not be read.")} />}
          </div>
        </section>
      </div>}
  </section>;
}
