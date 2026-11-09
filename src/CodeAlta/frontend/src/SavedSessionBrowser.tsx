import { AppWindowSurface } from "./AppWindow";
import { Button, HTMLSelect, InputGroup, PortalProvider } from "@blueprintjs/core";
import { Cell, Column, ColumnHeaderCell, Regions, RenderMode, SelectionModes, Table2 } from "@blueprintjs/table";
import "@blueprintjs/table/lib/css/table.css";
import { useEffect, useRef, useState, useSyncExternalStore, type MouseEvent } from "react";
import type { WorkspaceSession, WorkspaceSnapshot } from "#neoastra";
import { ConfirmPopover } from "./ConfirmPopover";
import { ActivitySpinner } from "./ActivitySpinner";
import { AppIcon } from "./AppIcon";
import { ProviderIcon } from "./ProviderIcon";
import type { SessionTab } from "./sessionTabs";
import { browseSessions } from "./sessionBrowser";
import type { RuntimeObservationControls } from "./RuntimeObservation";
import { sessionRunning } from "./runtimeObservations";
import { tabKey } from "./sessionTabs";
import { checkedRange, sessionDeletionPlan, sessionTreeRows, treeCheckState, withChecked, withCheckedTree } from "./sessionBrowserSelection";
import { sessionTime } from "./sessionTime";
import { plainTitle } from "./sessionTitle";
import { useShellLanguage } from "./shellLanguage";
import type { MessageKey } from "./localization";
import { DeletedWorktreesChoice } from "./worktrees/DeletedWorktreesChoice";
import { deletedSessionsWorktrees, type DeletedWorktree } from "./worktrees/worktrees";

const noSubscribe = () => () => {};
const noSnapshot = () => null;

type SortColumn = "title" | "provider" | "updated" | "messages";
const columns: readonly [SortColumn, MessageKey][] = [["title", "Session"], ["provider", "Provider"], ["updated", "Updated"], ["messages", "Messages"]];
// The archived sessions are sorted by when they were archived, in the column of the time.
const archivedColumns: readonly [SortColumn, MessageKey][] = [["title", "Session"], ["provider", "Provider"], ["updated", "Archived"], ["messages", "Messages"]];

/** The archived sessions of the scopes the browser shows, and what it does with one. */
export type ArchivedSessionControls = Readonly<{
  /** The archived sessions of the shown space, the one archived last first. */
  sessions: readonly WorkspaceSession[];
  /** An archive, a restore or a deletion is under way. */
  busy: boolean;
  /** Whether a deletion is asked about first. */
  deleteAsks: boolean;
  /** Takes the session out of the archive and shows it; the browser closes. */
  restore: (session: WorkspaceSession) => void;
  /** Shows what is recorded of the session, with when it was archived. */
  info: (session: WorkspaceSession) => void;
  /** Deletes the session; the answer is why it was not deleted, or null. `remember` says not to ask again. */
  remove: (session: WorkspaceSession, remember: boolean) => Promise<string | null>;
}>;

/** What the browser does with the sessions checked in it, several at once. */
export type CheckedSessionControls = Readonly<{
  /** Every session of the catalog, archived ones included: the sessions below the checked ones, and who works in their worktrees. */
  catalog: readonly WorkspaceSession[];
  /** An archive, a restore or a deletion is under way. */
  busy: boolean;
  /** Archives the sessions, each alone; the answer is the ones archived. */
  archive: (sessions: readonly WorkspaceSession[]) => Promise<readonly string[]>;
  /** Takes the sessions out of the archive; the answer is the ones restored. */
  restore: (sessions: readonly WorkspaceSession[]) => Promise<readonly string[]>;
  /**
   * Deletes the sessions in their order, then removes the worktrees given that only deleted sessions worked in. The
   * answer is the ones deleted, and why the others were not.
   */
  remove: (sessions: readonly WorkspaceSession[], worktrees: readonly DeletedWorktree[]) => Promise<Readonly<{ deleted: readonly string[]; error: string | null }>>;
  /** Asks the host who works in the worktrees: it knows of sessions the window does not list. */
  checkWorktrees?: (worktrees: readonly DeletedWorktree[], deleted: readonly string[]) => Promise<readonly DeletedWorktree[]>;
  /** The name of a project, for worktrees of several projects. */
  projectName: (projectId: string) => string;
}>;

/**
 * The saved sessions of a project (or the global ones) as a sortable table: title, provider, last update and
 * message count. Typing filters by title or id, the arrows move the selection and Enter opens it. **Archived** shows
 * the archived sessions instead, where Enter restores the selected one. Sessions checked in the first column are
 * archived, restored or deleted together; a session at work is not checked.
 */
export function SessionBrowser({ snapshot, projectId, stale, open, close, observations, archived, checking }: {
  snapshot: WorkspaceSnapshot; projectId: string | null; stale: boolean;
  open: (tab: SessionTab) => boolean; close: () => void;
  observations?: RuntimeObservationControls;
  recentCount?: number; activeSessionId?: string | null;
  archived?: ArchivedSessionControls;
  checking?: CheckedSessionControls;
}) {
  const { t, locale } = useShellLanguage();
  const dialog = useRef<HTMLDialogElement>(null);
  // A question asked in the window opens in it: outside, it would be under the modal window.
  const [layer, setLayer] = useState<HTMLDialogElement | null>(null);
  const search = useRef<HTMLInputElement>(null);
  const [scope, setScope] = useState(projectId);
  const [query, setQuery] = useState("");
  const [selectedKey, setSelectedKey] = useState<string | null>(null);
  const [sort, setSort] = useState<{ column: SortColumn; descending: boolean }>({ column: "updated", descending: true });
  const observed = useSyncExternalStore(observations?.store.subscribe ?? noSubscribe, observations?.store.getSnapshot ?? noSnapshot);
  const [refused, setRefused] = useState(false);
  const [now] = useState(Date.now);
  const [showArchived, setShowArchived] = useState(false);
  const [deleting, setDeleting] = useState<Readonly<{ id: string; error: string | null }> | null>(null);
  // The sessions checked, by id: they stay checked while the search or the order change, not when the scope or the view do.
  const [checked, setChecked] = useState<ReadonlySet<string>>(new Set());
  const anchor = useRef<string | null>(null);
  // The table of the sessions: its columns fill the width of the window, the title taking what the others leave.
  const table = useRef<Table2>(null);
  const results = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState(0);
  // The question of the deletion of the checked sessions, with the worktrees as the host checked them.
  const [deletingChecked, setDeletingChecked] = useState<Readonly<{ error: string | null }> | null>(null);
  const [removingWorktrees, setRemovingWorktrees] = useState(false);
  const [checkedWorktrees, setCheckedWorktrees] = useState<readonly DeletedWorktree[] | null>(null);
  const [checkingWorktrees, setCheckingWorktrees] = useState(false);
  const worktreeChecks = useRef(0);
  const archive = showArchived && archived ? archived : null;
  const archivedSnapshot = archived ? { ...snapshot, sessions: archived.sessions } : null;
  const source = archive && archivedSnapshot ? archivedSnapshot : snapshot;
  const page = browseSessions(source, scope, query);
  // All the sessions of the scope: a search shows what it finds with the sessions they come from and the ones below them.
  const scoped = query.trim() ? browseSessions(source, scope, "") : page;
  const archivedHere = archivedSnapshot ? browseSessions(archivedSnapshot, scope, "").matched : 0;
  const time = (row: WorkspaceSession) => archive ? row.archivedAt ?? row.updatedAt : row.updatedAt;
  const order = (a: WorkspaceSession, b: WorkspaceSession) => {
    const value = sort.column === "title" ? plainTitle(a.title).localeCompare(plainTitle(b.title), locale)
      : sort.column === "provider" ? (a.providerKey ?? "").localeCompare(b.providerKey ?? "")
      : sort.column === "messages" ? (a.messageCount ?? -1) - (b.messageCount ?? -1)
      : (Date.parse(time(a)) || 0) - (Date.parse(time(b)) || 0);
    return sort.descending ? -value : value;
  };
  // The sessions as a tree, each under the one it comes from, sorted within each level; with a search, the sessions
  // shown with a match are not matches themselves.
  const scopeSessions = scoped.rows.map(item => item.row);
  const items = new Map(scoped.rows.map(item => [item.row.id, item]));
  const matched = query.trim() ? new Set(page.rows.map(item => item.row.id)) : null;
  const rows = sessionTreeRows(scopeSessions, matched, order)
    .flatMap(node => { const item = items.get(node.session.id); return item ? [{ ...item, depth: node.depth, match: node.match }] : []; });
  const selected = Math.max(0, rows.findIndex(item => tabKey(item.tab) === selectedKey));
  function setSelected(index: number) { setSelectedKey(rows[index] ? tabKey(rows[index].tab) : null); }
  // A session at work, or one whose identity is not sure, is not checked.
  const runningIn = (tab: SessionTab) => !archive && !stale && !!observations?.enabled && !!observed && observations.canObserve(tab) && sessionRunning(observed, tab);
  // The sessions of an archived project are read-only: none is checked.
  const readOnly = scope !== null && !!snapshot.projects.find(row => row.id === scope)?.archived;
  const checkable = (item: { valid: boolean; tab: SessionTab }) => !stale && !readOnly && item.valid && !runningIn(item.tab);
  const shownCheckable = rows.filter(checkable).map(item => item.row.id);
  // The box of the header checks the matches shown, and with them the sessions below them.
  const shownMatches = rows.filter(item => item.match && checkable(item)).map(item => item.row.id);
  const notCheckable = new Set(scoped.rows.filter(item => !checkable(item)).map(item => item.row.id));
  // A session is checked with the sessions below it, those that can be.
  const checkTree = (current: ReadonlySet<string>, ids: readonly string[], value: boolean) => {
    const next = new Set(withCheckedTree(current, scopeSessions, ids, value));
    for (const id of notCheckable) next.delete(id);
    return next;
  };
  // The checked sessions of the scope and the view, also those the search hides now.
  const inScope = (row: WorkspaceSession) => scope === null ? row.scopeKind === "global" && row.projectId === null : row.scopeKind === "project" && row.projectId === scope;
  const checkedRows = (archive ? archive.sessions : snapshot.sessions).filter(row => checked.has(row.id) && inScope(row));
  const plan = checking && deletingChecked ? sessionDeletionPlan(checkedRows, checking.catalog) : null;
  const foundWorktrees = plan && checking ? deletedSessionsWorktrees(checking.catalog, plan.order) : [];
  const worktrees = checkedWorktrees ?? foundWorktrees;
  // The sessions below the kept ones that the table can check: Select their child sessions checks them.
  const missingShown = plan ? plan.missing.filter(row => inScope(row) && rows.some(item => item.row.id === row.id && checkable(item))) : [];
  function check(id: string, event: MouseEvent) {
    const value = treeCheckState(checked, scopeSessions.filter(row => !notCheckable.has(row.id)), id) !== "checked";
    const ids = event.shiftKey ? checkedRange(shownCheckable, anchor.current, id) : [id];
    anchor.current = id;
    setChecked(current => checkTree(current, ids, value));
  }
  function clearChecked() { setChecked(new Set()); anchor.current = null; setDeletingChecked(null); }
  // `sessions` are the checked ones, also those checked in the same click.
  async function askDeleteChecked(sessions = checkedRows) {
    if (!checking || checking.busy || sessions.length === 0) return;
    setRemovingWorktrees(false);
    setCheckedWorktrees(null);
    setDeletingChecked({ error: null });
    const order = sessionDeletionPlan(sessions, checking.catalog).order;
    const found = deletedSessionsWorktrees(checking.catalog, order);
    if (!checking.checkWorktrees || found.length === 0) return;
    const sequence = ++worktreeChecks.current;
    setCheckingWorktrees(true);
    const result = await checking.checkWorktrees(found, order.map(row => row.id)).catch(() => null);
    if (sequence !== worktreeChecks.current) return;
    setCheckingWorktrees(false);
    if (result) setCheckedWorktrees(result);
  }
  // A question asked of a list that changed is not answered: it closes.
  useEffect(() => { if (stale) { worktreeChecks.current++; setCheckingWorktrees(false); setDeletingChecked(null); } }, [stale]);
  async function deleteChecked() {
    if (!checking || !plan || checking.busy || stale) return;
    if (plan.order.length === 0) { setDeletingChecked(null); return; }
    const result = await checking.remove(plan.order, removingWorktrees ? worktrees.filter(worktree => !worktree.shared) : []);
    setChecked(current => withChecked(current, result.deleted, false));
    setDeletingChecked(result.error ? { error: result.error } : null);
  }
  async function archiveChecked() {
    if (!checking || checking.busy || stale || checkedRows.length === 0) return;
    const done = await (archive ? checking.restore(checkedRows) : checking.archive(checkedRows));
    setChecked(current => withChecked(current, done, false));
  }
  const titleOf = (row: WorkspaceSession) => plainTitle(row.fullTitle || row.title) || t("Untitled session");
  // The check column, then the provider, the time and the messages at their width; the title takes the rest.
  const fixed = (checking ? 40 : 0) + 150 + 130 + 96;
  const columnWidths = [...(checking ? [40] : []), Math.max(220, width - fixed - 2), 150, 130, 96];
  const cellClass = (index: number, name: string) => [name, index === selected ? "session-browser-selected" : "",
    stale || !rows[index].valid ? "session-browser-unavailable" : "", rows[index].match ? "" : "session-browser-context"].filter(Boolean).join(" ");
  function cellContent(index: number, column: SortColumn) {
    const { row, valid, tab, depth } = rows[index];
    if (column === "title") return <span className="session-browser-title-cell" style={{ paddingInlineStart: depth * 18 }}>
      {depth > 0 && <span className="session-browser-child" aria-hidden="true">↳</span>}
      {runningIn(tab) ? <ActivitySpinner size={12} label={t("Running")} /> : <AppIcon name={archive ? "archive" : "assistant"} size={13} />}
      <strong className="session-browser-name">{titleOf(row)}</strong>
      {!valid && <small>{t("Ambiguous or unverified identity — unavailable")}</small>}</span>;
    if (column === "provider") return row.providerKey ? <span className="with-logo"><ProviderIcon providerKey={row.providerKey} size={13} />{row.providerKey}</span> : "—";
    if (column === "messages") return row.messageCount ?? "—";
    // A time after the browser opened (an archive since) is said as now, not as to come.
    const shownTime = sessionTime(time(row), locale, Math.max(now, Date.parse(time(row)) || 0));
    return <time dateTime={shownTime.dateTime} title={shownTime.title}>{shownTime.label || t("unknown")}</time>;
  }
  const project = snapshot.projects.filter(row => row.id === projectId);
  const scopeName = scope === null ? t("Chats") : project.length === 1 ? project[0].name : t("ambiguous or missing");
  useEffect(() => { const element = dialog.current!; element.showModal(); search.current?.focus(); return () => element.close(); }, []);
  useEffect(() => { if (rows[selected]) table.current?.scrollToRegion(Regions.row(selected)); }, [selected, query, scope]);
  useEffect(() => {
    const element = results.current;
    if (!element) return;
    const observer = new ResizeObserver(() => setWidth(element.clientWidth));
    observer.observe(element);
    setWidth(element.clientWidth);
    return () => observer.disconnect();
  }, []);
  function activate(index: number) {
    const item = rows[index];
    if (archive) { if (item && !stale && !archive.busy) archive.restore(item.row); return; }
    if (stale || !item?.valid || !open(item.tab)) setRefused(true);
  }
  async function remove(row: WorkspaceSession, remember: boolean) {
    if (!archive || archive.busy) return;
    const error = await archive.remove(row, remember);
    setDeleting(error ? { id: row.id, error } : null);
  }
  function askRemove(row: WorkspaceSession) {
    if (!archive || archive.busy) return;
    if (archive.deleteAsks) setDeleting({ id: row.id, error: null }); else void remove(row, false);
  }
  const selectedRow = rows[selected]?.row;
  return <dialog ref={element => { dialog.current = element; setLayer(element); }} className="session-browser" aria-labelledby="session-browser-title"
    onCancel={event => { event.preventDefault(); close(); }} onKeyDown={event => {
      event.stopPropagation();
      if (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229 || event.repeat || event.defaultPrevented || event.ctrlKey || event.altKey || event.metaKey || event.shiftKey) {
        if (event.key === "Enter" || event.key === "Escape") event.preventDefault();
        return;
      }
      if (event.key === "Escape") { event.preventDefault(); close(); return; }
      if (event.target instanceof HTMLElement && event.target.closest("select, button")) return;
      if (event.target instanceof HTMLInputElement && event.target !== search.current) return;
      if (event.key === "ArrowDown" || event.key === "ArrowUp") {
        event.preventDefault(); setSelected(Math.max(0, Math.min(rows.length - 1, selected + (event.key === "ArrowDown" ? 1 : -1))));
      } else if (event.key === "Enter") { event.preventDefault(); activate(selected); }
    }}>
    <AppWindowSurface storageKey="codealta.desktop.window.session-browser.v2" title={t("Sessions · {name}", { name: scopeName })} titleId="session-browser-title"
      preferredSize={viewport => ({ width: Math.min(980, viewport.width - 40), height: Math.min(680, viewport.height - 40) })}
      onClose={close} closeLabel={t("Close")}>
    <div className="session-browser-toolbar">
      <InputGroup inputRef={search} className="session-browser-filter" type="search" size="small" leftIcon={<AppIcon name="search" size={14} className="bp6-icon" />}
        placeholder={t("Title or ID")} aria-label={t("Find saved sessions")} maxLength={256} value={query}
        onChange={event => { setQuery(event.target.value); setSelectedKey(null); }} role="combobox" aria-autocomplete="list" aria-expanded="true" aria-controls="session-browser-results" />
      <HTMLSelect aria-label={t("Session browser scope")} value={scope === null ? "global" : "project"}
        onChange={event => { setScope(event.target.value === "global" ? null : projectId); setSelectedKey(null); clearChecked(); }}>
        {projectId !== null && <option value="project">{project.length === 1 ? project[0].name : t("ambiguous or missing")}{project[0]?.archived ? t(" (archived, read-only)") : ""}</option>}
        <option value="global">{t("Chats")}</option>
      </HTMLSelect>
      {archived && <Button className="session-browser-archived" size="small" variant={showArchived ? "solid" : "outlined"} active={showArchived}
        aria-pressed={showArchived} icon={<AppIcon name="archive" size={14} className="bp6-icon" />}
        onClick={() => { setShowArchived(value => !value); setSelectedKey(null); setDeleting(null); clearChecked(); }}>
        {t("Archived")}{archivedHere > 0 && <span className="session-browser-count">{archivedHere}</span>}</Button>}
      <span className="settings-editor-spacer" />
      <span className="bp6-text-muted" role="status">{t("{shown} of {matching} sessions", { shown: rows.filter(item => item.match).length, matching: page.matched })}{page.hidden > 0 && <>{" · "}{t("{count} matches omitted by the 200-row display limit.", { count: page.hidden })}</>}</span>
    </div>
    {(stale || refused) && <p role="alert">{t("Catalog, host or selection changed, or identity is ambiguous. Close and reopen the browser; nothing was opened.")}</p>}
    <div ref={results} id="session-browser-results" className="session-browser-results" role="region" aria-label={t(archive ? "Archived sessions" : "Saved sessions")}>
      {/* Drawn once the window is open and measured: the table sees its own resizes only when it starts visible. */}
      {rows.length > 0 && width > 0 && <Table2 ref={table} numRows={rows.length} enableRowHeader={false} enableMultipleSelection={false} enableColumnInteractionBar={false}
        enableGhostCells={false} defaultRowHeight={30} columnWidths={columnWidths} selectionModes={SelectionModes.ROWS_AND_CELLS}
        // At most 200 rows: drawn at once. Drawn in batches, the table starts again with each change of what it shows.
        renderMode={RenderMode.NONE}
        selectedRegionTransform={region => region.rows ? Regions.row(region.rows[0]) : region} selectedRegions={[Regions.row(selected)]}
        onSelection={regions => { const index = regions[0]?.rows?.[0]; if (index !== undefined && rows[index]) setSelected(index); }}
        // What the cells show, as values that stay the same while it does: a new list of the same rows draws nothing again.
        cellRendererDependencies={[rows.map(item => `${tabKey(item.tab)}:${item.depth}:${item.match}`).join("\n"), [...checked].join("\n"), selected, stale, observed, !!archive, locale, sort.column, sort.descending]}>
        {[...(checking ? [<Column key="check" columnHeaderCellRenderer={() => <ColumnHeaderCell className="session-browser-check" nameRenderer={() =>
          <input type="checkbox" aria-label={t("Select every session shown")} disabled={shownCheckable.length === 0}
            checked={shownMatches.length > 0 && shownMatches.every(id => checked.has(id))}
            ref={element => { if (element) element.indeterminate = shownCheckable.some(id => checked.has(id)) && !shownMatches.every(id => checked.has(id)); }}
            onChange={() => { const all = shownMatches.every(id => checked.has(id)); anchor.current = null; setChecked(current => checkTree(current, shownMatches, !all)); }} />} />}
          cellRenderer={index => <Cell interactive className={cellClass(index, "session-browser-check")}>
            <input type="checkbox" aria-label={t("Select {title}", { title: titleOf(rows[index].row) })}
              checked={checked.has(rows[index].row.id)} disabled={!checkable(rows[index])}
              ref={element => { if (element) element.indeterminate = !checked.has(rows[index].row.id) && treeCheckState(checked, scopeSessions, rows[index].row.id) === "partial"; }}
              onClick={event => check(rows[index].row.id, event)} onChange={() => { }} /></Cell>} />] : []),
          ...(archive ? archivedColumns : columns).map(([column, label]) => <Column key={column} columnHeaderCellRenderer={() => <ColumnHeaderCell
            nameRenderer={() => <button type="button" className="session-browser-sort" data-column={column}
              aria-sort={sort.column === column ? sort.descending ? "descending" : "ascending" : "none"}
              onClick={() => setSort(current => ({ column, descending: current.column === column ? !current.descending : column === "updated" || column === "messages" }))}>
              {t(label)}{sort.column === column && <AppIcon name="chevronDown" size={12} className={sort.descending ? undefined : "sort-ascending"} />}</button>} />}
            cellRenderer={index => <Cell className={cellClass(index, `session-browser-${column}`)} tooltip={column === "title" ? `${rows[index].row.fullTitle || rows[index].row.title}\n${rows[index].row.id}` : undefined}>
              <div className="session-browser-cell" onDoubleClick={() => activate(index)}>{cellContent(index, column)}</div></Cell>} />)]}
      </Table2>}
      {!rows.length && <p className="bp6-text-muted">{t(archive && !query.trim() ? "No archived sessions in this scope." : "No matching saved sessions in the loaded scope.")}</p>}
    </div>
    {checking && checkedRows.length > 0 ? <footer className="session-browser-footer checked-footer">
      <span className="bp6-text-muted" role="status">{t("{count} selected", { count: checkedRows.length })}</span>
      <Button size="small" variant="minimal" text={t("Clear selection")} onClick={clearChecked} />
      <span className="settings-editor-spacer" />
      <Button size="small" variant="outlined" icon={archive ? undefined : <AppIcon name="archive" size={14} className="bp6-icon" />} disabled={stale || checking.busy}
        text={archive ? t("Restore {count}", { count: checkedRows.length }) : t("Archive {count}", { count: checkedRows.length })} onClick={() => void archiveChecked()} />
      <span className="session-browser-delete">
        <Button size="small" variant="outlined" intent="danger" icon={<AppIcon name="trash" size={14} className="bp6-icon" />} disabled={stale || checking.busy}
          text={t("Delete {count}…", { count: checkedRows.length })} onClick={() => void askDeleteChecked()} />
        {deletingChecked && plan && layer && <PortalProvider portalContainer={layer}><ConfirmPopover always placement="top-end" intent="danger" busy={checking.busy} error={deletingChecked.error}
          title={plan.order.length === 0 ? t("Nothing can be deleted.") : plan.order.length === 1 ? t("Delete this session?") : t("Delete {count} sessions?", { count: plan.order.length })}
          subject={plan.order.length === 0 ? "" : plan.order.length === 1 ? plainTitle(plan.order[0].title)
            : t("{title} and {count} more", { title: plainTitle(plan.order[plan.order.length - 1].title), count: plan.order.length - 1 })}
          detail={plan.order.length === 1 ? t("Its history is deleted and cannot be restored. The files of the project are kept.")
            : t("Their history is deleted and cannot be restored. The files of the projects are kept.")}
          confirmLabel={t("Delete")} onConfirm={() => void deleteChecked()} onCancel={() => { worktreeChecks.current++; setCheckingWorktrees(false); setDeletingChecked(null); }}>
          {plan.kept.length > 0 && <div className="confirm-kept">
            <p>{plan.kept.length === 1 ? t("One of them has child sessions you did not select: it is kept.")
              : t("{count} of them have child sessions you did not select: they are kept.", { count: plan.kept.length })}</p>
            {missingShown.length > 0 && <Button size="small" variant="minimal" text={t("Select their child sessions")}
              onClick={() => { setChecked(current => withChecked(current, missingShown.map(row => row.id), true)); void askDeleteChecked([...checkedRows, ...missingShown]); }} />}
          </div>}
          <DeletedWorktreesChoice worktrees={worktrees} checking={checkingWorktrees} removing={removingWorktrees} setRemoving={setRemovingWorktrees}
            busy={checking.busy} projectName={checking.projectName} />
        </ConfirmPopover></PortalProvider>}
      </span>
    </footer> : archive ? <footer className="session-browser-footer archived-footer">
      <span className="bp6-text-muted">{t("Enter or double-click restores the selected session.")}</span>
      <Button size="small" variant="minimal" text={t("Info…")} disabled={!selectedRow} onClick={() => { if (selectedRow) archive.info(selectedRow); }} />
      <span className="session-browser-delete">
        <Button size="small" variant="minimal" intent="danger" text={archive.deleteAsks ? `${t("Delete")}…` : t("Delete")} disabled={stale || !selectedRow || archive.busy}
          onClick={() => { if (selectedRow) askRemove(selectedRow); }} />
        {deleting && selectedRow?.id === deleting.id && layer && <PortalProvider portalContainer={layer}><ConfirmPopover placement="top-end" title={t("Delete this session?")} subject={plainTitle(selectedRow.title)}
          detail={t("Its history is deleted and cannot be restored. The files of the project are kept.")}
          confirmLabel={t("Delete")} intent="danger" busy={archive.busy} error={deleting.error}
          onConfirm={remember => void remove(selectedRow, remember)} onCancel={() => setDeleting(null)} /></PortalProvider>}
      </span>
      <Button size="small" intent="primary" text={t("Restore")} disabled={stale || !selectedRow || archive.busy} onClick={() => activate(selected)} />
    </footer> : <footer className="session-browser-footer">
      <span className="bp6-text-muted">{t("Enter or double-click opens the selected session.")}</span>
      <button type="button" className="bp6-button bp6-small bp6-intent-primary" disabled={stale || !rows[selected]?.valid} onClick={() => activate(selected)}>{t("Open")}</button>
    </footer>}
  </AppWindowSurface></dialog>;
}