import { AppWindowSurface } from "./AppWindow";
import { HTMLSelect, InputGroup } from "@blueprintjs/core";
import { useEffect, useRef, useState, useSyncExternalStore } from "react";
import type { WorkspaceSnapshot } from "#neoastra";
import { ActivitySpinner } from "./ActivitySpinner";
import { AppIcon } from "./AppIcon";
import { ProviderIcon } from "./ProviderIcon";
import type { SessionTab } from "./sessionTabs";
import { browseSessions } from "./sessionBrowser";
import type { RuntimeObservationControls } from "./RuntimeObservation";
import { sessionRunning } from "./runtimeObservations";
import { tabKey } from "./sessionTabs";
import { batchDeleteCandidate } from "./sessionBatchDeletion";
import { SessionBatchDeletePanel, type BatchDeleteControls } from "./SessionBatchDeletePanel";
import { sessionTime } from "./sessionTime";
import { plainTitle } from "./sessionTitle";
import { useShellLanguage } from "./shellLanguage";
import type { MessageKey } from "./localization";

const noSubscribe = () => () => {};
const noSnapshot = () => null;

type SortColumn = "title" | "provider" | "updated" | "messages";
const columns: readonly [SortColumn, MessageKey][] = [["title", "Session"], ["provider", "Provider"], ["updated", "Updated"], ["messages", "Messages"]];

/**
 * The saved sessions of a project (or the global ones) as a sortable table: title, provider, last update and
 * message count. Typing filters by title or id, the arrows move the selection and Enter opens it.
 */
export function SessionBrowser({ snapshot, projectId, stale, open, close, observations, batch }: {
  snapshot: WorkspaceSnapshot; projectId: string | null; stale: boolean;
  open: (tab: SessionTab) => boolean; close: () => void;
  observations?: RuntimeObservationControls;
  recentCount?: number; activeSessionId?: string | null;
  batch?: BatchDeleteControls;
}) {
  const { t, locale } = useShellLanguage();
  const dialog = useRef<HTMLDialogElement>(null);
  const search = useRef<HTMLInputElement>(null);
  const [scope, setScope] = useState(projectId);
  const [query, setQuery] = useState("");
  const [selectedKey, setSelectedKey] = useState<string | null>(null);
  const [sort, setSort] = useState<{ column: SortColumn; descending: boolean }>({ column: "updated", descending: true });
  const observed = useSyncExternalStore(observations?.store.subscribe ?? noSubscribe, observations?.store.getSnapshot ?? noSnapshot);
  const [refused, setRefused] = useState(false);
  const [now] = useState(Date.now);
  const page = browseSessions(snapshot, scope, query);
  const order = (a: typeof page.rows[number], b: typeof page.rows[number]) => {
    const value = sort.column === "title" ? plainTitle(a.row.title).localeCompare(plainTitle(b.row.title), locale)
      : sort.column === "provider" ? (a.row.providerKey ?? "").localeCompare(b.row.providerKey ?? "")
      : sort.column === "messages" ? (a.row.messageCount ?? -1) - (b.row.messageCount ?? -1)
      : (Date.parse(a.row.updatedAt) || 0) - (Date.parse(b.row.updatedAt) || 0);
    return sort.descending ? -value : value;
  };
  const rows = [...page.rows].sort(order);
  const selected = Math.max(0, rows.findIndex(item => tabKey(item.tab) === selectedKey));
  function setSelected(index: number) { setSelectedKey(rows[index] ? tabKey(rows[index].tab) : null); }
  const project = snapshot.projects.filter(row => row.id === projectId);
  const scopeName = scope === null ? t("Chats") : project.length === 1 ? project[0].name : t("ambiguous or missing");
  useEffect(() => { const element = dialog.current!; element.showModal(); search.current?.focus(); return () => element.close(); }, []);
  useEffect(() => { dialog.current?.querySelector('[aria-selected="true"]')?.scrollIntoView({ block: "nearest" }); }, [selected, query, scope]);
  function activate(index: number) {
    const item = rows[index];
    if (stale || !item?.valid || !open(item.tab)) setRefused(true);
  }
  function change(action: () => void) { batch?.owner.invalidate(); action(); }
  return <dialog ref={dialog} className="session-browser" aria-labelledby="session-browser-title"
    onCancel={event => { event.preventDefault(); close(); }} onKeyDown={event => {
      event.stopPropagation();
      if (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229 || event.repeat || event.defaultPrevented || event.ctrlKey || event.altKey || event.metaKey || event.shiftKey) {
        if (event.key === "Enter" || event.key === "Escape") event.preventDefault();
        return;
      }
      if (event.key === "Escape") { event.preventDefault(); close(); return; }
      if (event.target instanceof HTMLElement && event.target.closest("select, button, .session-batch-delete")) return;
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
        onChange={event => change(() => { setQuery(event.target.value); setSelectedKey(null); })} role="combobox" aria-autocomplete="list" aria-expanded="true" aria-controls="session-browser-results"
        aria-activedescendant={rows[selected] ? `session-browser-row-${selected}` : undefined} />
      <HTMLSelect aria-label={t("Session browser scope")} value={scope === null ? "global" : "project"}
        onChange={event => change(() => { setScope(event.target.value === "global" ? null : projectId); setSelectedKey(null); })}>
        {projectId !== null && <option value="project">{project.length === 1 ? project[0].name : t("ambiguous or missing")}{project[0]?.archived ? t(" (archived, read-only)") : ""}</option>}
        <option value="global">{t("Chats")}</option>
      </HTMLSelect>
      <span className="settings-editor-spacer" />
      <span className="bp6-text-muted" role="status">{t("{shown} of {matching} sessions", { shown: rows.length, matching: page.matched })}{page.hidden > 0 && <>{" · "}{t("{count} matches omitted by the 200-row display limit.", { count: page.hidden })}</>}</span>
    </div>
    {(stale || refused) && <p role="alert">{t("Catalog, host or selection changed, or identity is ambiguous. Close and reopen the browser; nothing was opened.")}</p>}
    <div className="session-browser-results">
      <table id="session-browser-results" aria-label={t("Saved sessions")}>
        <thead><tr>{columns.map(([column, label]) => <th key={column} scope="col" data-column={column}
          aria-sort={sort.column === column ? sort.descending ? "descending" : "ascending" : "none"}>
          <button type="button" onClick={() => change(() => setSort(current => ({ column, descending: current.column === column ? !current.descending : column === "updated" || column === "messages" })))}>
            {t(label)}{sort.column === column && <AppIcon name="chevronDown" size={12} className={sort.descending ? undefined : "sort-ascending"} />}</button></th>)}</tr></thead>
        <tbody>{rows.map(({ row, valid, tab }, index) => {
          const time = sessionTime(row.updatedAt, locale, now);
          const running = !stale && !!observations?.enabled && !!observed && observations.canObserve(tab) && sessionRunning(observed, tab);
          return <tr id={`session-browser-row-${index}`} key={valid ? tabKey(tab) : `${tabKey(tab)}:${index}`} aria-selected={index === selected}
            aria-disabled={stale || !valid || undefined} title={`${row.fullTitle || row.title}\n${row.id}`}
            onClick={() => setSelected(index)} onDoubleClick={() => activate(index)}>
            <td data-column="title"><span>{running ? <ActivitySpinner size={12} label={t("Running")} /> : <AppIcon name="assistant" size={13} />}
              <strong>{plainTitle(row.fullTitle || row.title) || t("Untitled session")}</strong>
              {!valid && <small>{t("Ambiguous or unverified identity — unavailable")}</small>}</span></td>
            <td data-column="provider">{row.providerKey ? <span className="with-logo"><ProviderIcon providerKey={row.providerKey} size={13} />{row.providerKey}</span> : "—"}</td>
            <td data-column="updated"><time dateTime={time.dateTime} title={time.title}>{time.label || t("unknown")}</time></td>
            <td data-column="messages">{row.messageCount ?? "—"}</td>
          </tr>;
        })}</tbody>
      </table>
      {!rows.length && <p className="bp6-text-muted">{t("No matching saved sessions in the loaded scope.")}</p>}
    </div>
    <footer className="session-browser-footer">
      <span className="bp6-text-muted">{t("Enter or double-click opens the selected session.")}</span>
      <button type="button" className="bp6-button bp6-small bp6-intent-primary" disabled={stale || !rows[selected]?.valid} onClick={() => activate(selected)}>{t("Open")}</button>
    </footer>
    {batch && <SessionBatchDeletePanel controls={{ ...batch, canReview: batch.canReview && !stale }}
      inputKey={JSON.stringify([scope, query, sort, stale, rows.map(item => tabKey(item.tab))])}
      candidates={rows.flatMap(item => { const request = batchDeleteCandidate(snapshot, item.tab, batch.epoch); return request ? [request] : []; })} />}
  </AppWindowSurface></dialog>;
}
