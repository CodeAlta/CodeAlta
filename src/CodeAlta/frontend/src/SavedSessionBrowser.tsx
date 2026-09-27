import { useEffect, useRef, useState, useSyncExternalStore } from "react";
import type { WorkspaceSnapshot } from "#neoastra";
import type { SessionTab } from "./sessionTabs";
import { browseSessions } from "./sessionBrowser";
import { RuntimeObservationBadge, RuntimeObservationRefresh, type RuntimeObservationControls } from "./RuntimeObservation";
import { tabKey } from "./sessionTabs";
import { defaultRecentSessionCount, orderObservedActivity } from "./recentSessions";
import { batchDeleteCandidate } from "./sessionBatchDeletion";
import { SessionBatchDeletePanel, type BatchDeleteControls } from "./SessionBatchDeletePanel";
import { useShellLanguage } from "./shellLanguage";

const noSubscribe = () => () => {};
const noSnapshot = () => null;

export function SessionBrowser({ snapshot, projectId, stale, open, close, observations, recentCount = defaultRecentSessionCount, activeSessionId = null, batch }: {
  snapshot: WorkspaceSnapshot; projectId: string | null; stale: boolean;
  open: (tab: SessionTab) => boolean; close: () => void;
  observations?: RuntimeObservationControls;
  recentCount?: number; activeSessionId?: string | null;
  batch?: BatchDeleteControls;
}) {
  const { t } = useShellLanguage();
  const dialog = useRef<HTMLDialogElement>(null);
  const search = useRef<HTMLInputElement>(null);
  const [scope, setScope] = useState(projectId);
  const [query, setQuery] = useState("");
  const [selectedKey, setSelectedKey] = useState<string | null>(null);
  const [showAll, setShowAll] = useState(false);
  const [sort, setSort] = useState("saved");
  const observed = useSyncExternalStore(observations?.store.subscribe ?? noSubscribe, observations?.store.getSnapshot ?? noSnapshot);
  const [refused, setRefused] = useState(false);
  const page = browseSessions(snapshot, scope, query);
  const activity = (tab: SessionTab) => {
    const value = observed?.rows.get(tabKey(tab));
    return !stale && observations?.enabled && observations.canObserve(tab) && !value?.stale ? value?.activity : undefined;
  };
  const savedRows = [...page.rows].sort((a, b) => (Date.parse(b.row.updatedAt) || 0) - (Date.parse(a.row.updatedAt) || 0));
  const ordered = sort === "activity" ? orderObservedActivity(savedRows, item => activity(item.tab)?.timestamp ?? null)
    : sort === "name" ? [...page.rows].sort((a, b) => a.row.title.localeCompare(b.row.title)) : savedRows;
  const rows = showAll ? ordered : ordered.filter((item, index) => index < recentCount || item.row.id === activeSessionId || tabKey(item.tab) === selectedKey);
  const selected = Math.max(0, rows.findIndex(item => tabKey(item.tab) === selectedKey));
  function setSelected(index: number) { setSelectedKey(rows[index] ? tabKey(rows[index].tab) : null); }
  const project = snapshot.projects.filter(row => row.id === projectId);
  useEffect(() => { const element = dialog.current!; element.showModal(); search.current?.focus(); return () => element.close(); }, []);
  useEffect(() => { dialog.current?.querySelector('[aria-selected="true"]')?.scrollIntoView({ block: "nearest" }); }, [selected, query, scope]);
  function activate(index: number) {
    const item = rows[index];
    if (stale || !item?.valid || !open(item.tab)) setRefused(true);
  }
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
    <header><h2 id="session-browser-title">{t("Browse saved sessions")}</h2><button type="button" onClick={close}>{t("Close")}</button></header>
    <label>{t("Scope")} <select aria-label={t("Session browser scope")} value={scope === null ? "global" : "project"} onChange={event => { batch?.owner.invalidate(); observations?.store.invalidate(); setScope(event.target.value === "global" ? null : projectId); setSelected(0); }}>
      {projectId !== null && <option value="project">{t("Selected project: {name}", { name: project.length === 1 ? project[0].name : t("ambiguous or missing") })}{project[0]?.archived ? t(" (archived, read-only)") : ""}</option>}
      <option value="global">{t("Global sessions")}</option>
    </select></label>
    <label>{t("Title or ID")} <input ref={search} aria-label={t("Find saved sessions")} maxLength={256} value={query}
      onChange={event => { batch?.owner.invalidate(); observations?.store.invalidate(); setQuery(event.target.value); setSelected(0); }} role="combobox" aria-autocomplete="list" aria-expanded="true" aria-controls="session-browser-results"
      aria-activedescendant={rows[selected] ? `session-browser-row-${selected}` : undefined} /></label>
    <label>{t("Order loaded sessions")} <select aria-label={t("Order loaded sessions")} value={sort} onChange={event => { batch?.owner.invalidate(); setSelected(selected); setSort(event.target.value); }}>
      <option value="saved">{t("Saved update")}</option><option value="name">{t("Name")}</option><option value="activity">{t("Observed activity (explicit refresh)")}</option>
    </select></label>
    <button type="button" onClick={() => { batch?.owner.invalidate(); setShowAll(value => !value); }}>{t(showAll ? "Use recent session limit" : "Show all loaded matches")}</button>
    <p role="status">{t("{shown} shown / {matching} matching / {loaded} loaded in scope. Limit {limit}; active/highlighted rows retained.", { shown: rows.length, matching: page.matched, loaded: page.loaded, limit: recentCount })}{" "}{page.hidden > 0 && <>{t("{count} matches omitted by the 200-row display limit.", { count: page.hidden })}{" "}</>}
      {t(page.incomplete ? "Snapshot or display text was truncated; more may exist." : "Loaded snapshot only; not a completeness or running-status report.")}</p>
    {(stale || refused) && <p role="alert">{t("Catalog, host or selection changed, or identity is ambiguous. Close and reopen the browser; nothing was opened.")}</p>}
    {batch && <SessionBatchDeletePanel controls={{ ...batch, canReview: batch.canReview && !stale }}
      inputKey={JSON.stringify([scope, query, sort, showAll, recentCount, stale, rows.map(item => tabKey(item.tab))])}
      candidates={rows.flatMap(item => { const request = batchDeleteCandidate(snapshot, item.tab, batch.epoch); return request ? [request] : []; })} />}
    {observations && <RuntimeObservationRefresh controls={{ ...observations, refresh: tabs => { setSelected(selected); observations.refresh(tabs); } }} disabled={stale} tabs={page.rows.map(item => item.tab)} />}
    {sort === "activity" && <p>{t("Last activity observed in this runtime/attachment: last admitted matching agent-event timestamp, not historical latest or maximum. Unknowns follow known observations in stable saved order. Refresh reads at most 32 loaded candidates, independently of the display limit; partial results are not globally recent. No tab reordering.")}</p>}
    <div id="session-browser-results" role="listbox" aria-label={t("Saved sessions")} className="session-browser-results">
      {rows.map(({ row, valid, tab }, index) => <button type="button" id={`session-browser-row-${index}`} key={valid ? tabKey(tab) : `${tabKey(tab)}:${index}`} role="option" aria-selected={index === selected}
        disabled={stale || !valid} onFocus={() => setSelected(index)} onClick={() => activate(index)}>
        <strong>{row.fullTitle || row.title || t("Untitled session")}</strong><span>{row.id}</span>
        <small>{!valid ? t("Ambiguous or unverified identity — unavailable") : t("Saved metadata · updated {time}", { time: row.updatedAt || t("unknown") })}{row.fullTitleTruncated ? t(" · title truncated") : ""}</small>
        {observations && <RuntimeObservationBadge controls={observations} tab={tab} />}
        {sort === "activity" && <small className={activity(tab)?.timestamp ? "observed-activity" : "unknown-activity"}>
          {activity(tab)?.timestamp ? t("Observed activity · {time}", { time: activity(tab)!.timestamp! }) : t("Unknown activity · missing, stale, omitted or not yet observed")}
          {activity(tab) && t(" · {admitted} admitted / {omitted} omitted events · this attachment only", { admitted: activity(tab)!.admittedEvents, omitted: activity(tab)!.omittedEvents })}
        </small>}
      </button>)}
      {!rows.length && <p>{t("No matching saved sessions in the loaded scope.")}</p>}
    </div>
    <p>{t("Up/Down then Enter from search to open; Escape closes. Filtering never selects a session. Archived projects open read-only.")}</p>
  </dialog>;
}
