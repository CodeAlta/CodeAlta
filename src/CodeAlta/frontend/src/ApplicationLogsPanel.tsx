import { useEffect, useLayoutEffect, useMemo, useRef, useState, useSyncExternalStore } from "react";
import { Button, ButtonGroup, InputGroup, PopoverNext, Switch } from "@blueprintjs/core";
import type { ApplicationLogsRequest, ApplicationLogsResponse } from "#neoastra";
import { AppIcon } from "./AppIcon";
import { logClearTarget, type createApplicationLogClearActions } from "./applicationLogClear";
import { logLevelTone, logTime, type LogTone } from "./applicationLogView";
import { useShellLanguage } from "./shellLanguage";
import type { MessageKey } from "./localization";

const refreshMilliseconds = 2000;
const filters: readonly ["all" | LogTone, MessageKey][] = [["all", "All"], ["info", "Info"], ["warning", "Warnings"], ["error", "Errors"]];

function valid(value: ApplicationLogsResponse): boolean {
  return ["ok", "unavailable", "read_failed"].includes(value.status) && Array.isArray(value.rows)
    && value.rows.length <= 400 && /^\d{1,19}$/.test(value.captureOmitted)
    && Number.isInteger(value.readOmitted) && value.readOmitted >= 0 && value.readOmitted <= 1000
    && value.rows.every(row => typeof row.timestamp === "string" && row.timestamp.length <= 64
      && typeof row.level === "string" && row.level.length <= 32 && typeof row.logger === "string" && row.logger.length <= 128
      && typeof row.text === "string" && row.text.length <= 2048 && typeof row.textTruncated === "boolean")
    && !(value.status === "ok" && value.rows.length > 0 && !logClearTarget(value));
}

/**
 * The messages this app has logged since it started, as a live log view: the newest messages are read when the
 * page opens and every two seconds after, coloured by level, and the view stays at its end unless the reader
 * scrolled up.
 */
export function ApplicationLogsPanel({ read, clearActions }: {
  read: (request: ApplicationLogsRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<ApplicationLogsResponse>;
  clearActions: ReturnType<typeof createApplicationLogClearActions>;
}) {
  const { t } = useShellLanguage();
  const [snapshot, setSnapshot] = useState<ApplicationLogsResponse>();
  const [error, setError] = useState<MessageKey | "">("");
  const [wrap, setWrap] = useState(false);
  const [level, setLevel] = useState<"all" | LogTone>("all");
  const [query, setQuery] = useState("");
  const [confirming, setConfirming] = useState(false);
  const clearState = useSyncExternalStore(clearActions.subscribe, clearActions.snapshot);
  const view = useRef<HTMLDivElement>(null);
  const following = useRef(true);

  useEffect(() => {
    const abort = new AbortController();
    let reading = false;
    const refresh = () => {
      if (reading) return;
      reading = true;
      void read({}, { signal: abort.signal, timeoutMilliseconds: 8000 }).then(value => {
        if (abort.signal.aborted) return;
        if (!valid(value)) { setError("Invalid logs response. Nothing was displayed."); return; }
        setError("");
        // An unchanged capture keeps the rows (and the reader's selection) as they are.
        setSnapshot(current => current && current.status === value.status && current.boundary === value.boundary
          && current.captureId === value.captureId && current.rows.length === value.rows.length ? { ...current, grant: value.grant } : value);
      }).catch(() => { if (!abort.signal.aborted) setError("Application logs could not be read."); })
        .finally(() => { reading = false; });
    };
    refresh();
    const timer = window.setInterval(refresh, refreshMilliseconds);
    return () => { abort.abort(); window.clearInterval(timer); };
  }, [read]);

  const target = snapshot && logClearTarget(snapshot);
  const clearedPage = !!target && clearState.kind === "confirmed" && clearState.target?.boundary === target.boundary
    && clearState.target.captureId === target.captureId;
  const clearAllowed = !!target && !clearedPage && (clearState.kind === "idle" || clearState.kind === "confirmed"
    && clearState.target?.captureId === target.captureId && BigInt(target.boundary) > BigInt(clearState.target.boundary)
    && target.grant !== clearState.target.grant);
  const rows = snapshot?.status === "ok" && !clearedPage ? snapshot.rows : [];
  const shown = useMemo(() => {
    const text = query.trim().toLowerCase();
    return rows.filter(row => (level === "all" || logLevelTone(row.level) === level)
      && (!text || row.text.toLowerCase().includes(text) || row.logger.toLowerCase().includes(text)));
  }, [rows, level, query]);
  useLayoutEffect(() => {
    const element = view.current;
    if (element && following.current) element.scrollTop = element.scrollHeight;
  }, [shown, wrap]);

  return <main className="configuration-page application-logs">
    <header className="page-heading"><span className="eyebrow">{t("Desktop")}</span><h1>{t("Application Logs")}</h1>
      <p>{t("Messages captured in memory by this app since it started.")}</p></header>
    <div className="logs-toolbar">
      <InputGroup className="logs-filter" type="search" size="small" leftIcon={<AppIcon name="search" size={14} className="bp6-icon" />} value={query}
        placeholder={t("Filter messages")} aria-label={t("Filter messages")} onChange={event => setQuery(event.target.value)} />
      <ButtonGroup role="group" aria-label={t("Level")}>
        {filters.map(([value, label]) => <Button key={value} size="small" active={level === value} aria-pressed={level === value} onClick={() => setLevel(value)}>{t(label)}</Button>)}
      </ButtonGroup>
      <span className="settings-editor-spacer" />
      <Switch className="logs-wrap-switch" checked={wrap} label={t("Wrap lines")} onChange={event => setWrap(event.currentTarget.checked)} />
      <PopoverNext placement="bottom-end" isOpen={confirming && !!target} onInteraction={open => setConfirming(open && clearAllowed)}
        content={<div className="provider-settings-confirm"><p>{target && t("Clear {rows} captured message(s) from memory? Log files are not changed.", target)}</p>
          <Button intent="danger" onClick={() => { if (target) clearActions.submit(target, "CLEAR CAPTURED LOGS"); setConfirming(false); }}>{t("Clear")}</Button></div>}>
        <Button size="small" icon={<AppIcon name="trash" size={14} />} disabled={!clearAllowed}>{t("Clear")}</Button>
      </PopoverNext>
    </div>
    {clearState.kind !== "idle" && clearState.kind !== "confirmed" && clearState.target && <p role="status" className="logs-clear-status">{clearState.kind === "pending"
      ? t("clear request pending; original request retained across navigation.")
      : clearState.kind === "uncertain" ? t("clear outcome unconfirmed; original request retained. Refresh cannot unlock or retry it.")
      : t("clear refused; no entries cleared by this request. Original request remains locked.")}</p>}
    {error && <p role="alert" className="error-text">{t(error)}</p>}
    {snapshot?.status === "unavailable" && <p role="status">{t("In-memory capture unavailable: this window has no desktop-owned capture (for example, external logger ownership or browser demo). No files were read.")}</p>}
    {snapshot?.status === "read_failed" && <p role="alert" className="error-text">{t("Application logs could not be read.")}</p>}
    <div ref={view} className={`logs-view${wrap ? " logs-wrap" : ""}`} role="log" tabIndex={0} aria-label={t("Current process log entries")}
      onScroll={event => { const element = event.currentTarget; following.current = element.scrollHeight - element.scrollTop - element.clientHeight < 24; }}>
      {shown.map((row, index) => { const time = logTime(row.timestamp); return <div key={index} className="log-line" data-level={logLevelTone(row.level)}>
        <time title={time.title}>{time.label}</time><span className="log-level">{row.level.toUpperCase()}</span><span className="log-logger">{row.logger}</span>
        <span className="log-text">{row.text}{row.textTruncated && ` ${t("[message truncated]")}`}</span></div>; })}
      {snapshot?.status === "ok" && shown.length === 0 && <p className="logs-empty" role="status">{t(rows.length ? "No message matches the filter." : "No captured messages in this process.")}</p>}
    </div>
    {snapshot?.status === "ok" && <p className="logs-summary bp6-text-muted" role="status">{t("{shown} of {total} messages", { shown: shown.length, total: rows.length })}
      {(snapshot.captureOmitted !== "0" || snapshot.readOmitted > 0) && ` · ${t("Older entries omitted: {captureOmitted} from in-memory capacity, {readOmitted} from the bounded response.", { captureOmitted: snapshot.captureOmitted, readOmitted: snapshot.readOmitted })}`}</p>}
  </main>;
}
