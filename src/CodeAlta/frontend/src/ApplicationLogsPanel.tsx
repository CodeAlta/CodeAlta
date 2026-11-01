import { useEffect, useLayoutEffect, useMemo, useRef, useState, useSyncExternalStore } from "react";
import { Button, ButtonGroup, HTMLSelect, InputGroup, PopoverNext, Switch } from "@blueprintjs/core";
import type { ApplicationLogLevelsRequest, ApplicationLogLevelsResponse, ApplicationLogLevelsSaveRequest, ApplicationLogsRequest, ApplicationLogsResponse } from "#neoastra";
import { AppIcon } from "./AppIcon";
import { logClearTarget, type createApplicationLogClearActions } from "./applicationLogClear";
import { changeLogLevels, countLogFeatures, filterLogRows, logLevelTone, logRowFeature, logTime, type LogTone } from "./applicationLogView";
import { useShellLanguage } from "./shellLanguage";
import type { MessageKey } from "./localization";

const refreshMilliseconds = 2000;
const filters: readonly ["all" | LogTone, MessageKey][] = [["all", "All"], ["info", "Info"], ["warning", "Warnings"], ["error", "Errors"]];
// The names of the features and of the levels the host knows, in the language of the window; another one shows as the host names it.
const featureNames: Readonly<Record<string, MessageKey>> = { system: "System", providers: "Providers", sessions: "Sessions", tools: "Tools",
  issues: "Issue trackers", git: "Git", mcp: "MCP", plugins: "Plugins", search: "Search", speech: "Speech", mobile: "Mobile" };
const levelNames: Readonly<Record<string, MessageKey>> = { trace: "Trace", debug: "Debug", info: "Info", warn: "Warning", error: "Error" };

/** Reads and changes the levels of the features of the log; absent where the window has no log of its own. */
export interface ApplicationLogLevelsApi {
  levels: (request: ApplicationLogLevelsRequest, options: { signal?: AbortSignal; timeoutMilliseconds: number }) => Promise<ApplicationLogLevelsResponse>;
  saveLevels: (request: ApplicationLogLevelsSaveRequest, options: { timeoutMilliseconds: number }) => Promise<ApplicationLogLevelsResponse>;
}

function validLevels(value: ApplicationLogLevelsResponse): boolean {
  return typeof value?.status === "string" && typeof value.level === "string" && Array.isArray(value.levels) && value.levels.length <= 16
    && value.levels.every(level => typeof level === "string" && level.length <= 16) && Array.isArray(value.features) && value.features.length <= 64
    && value.features.every(feature => typeof feature.id === "string" && feature.id.length <= 32 && typeof feature.name === "string" && feature.name.length <= 64
      && (feature.level === null || typeof feature.level === "string" && feature.level.length <= 16));
}

function valid(value: ApplicationLogsResponse): boolean {
  return ["ok", "unavailable", "read_failed"].includes(value.status) && Array.isArray(value.rows)
    && value.rows.length <= 400 && /^\d{1,19}$/.test(value.captureOmitted)
    && Number.isInteger(value.readOmitted) && value.readOmitted >= 0 && value.readOmitted <= 1000
    && value.rows.every(row => typeof row.timestamp === "string" && row.timestamp.length <= 64
      && typeof row.level === "string" && row.level.length <= 32 && typeof row.logger === "string" && row.logger.length <= 128
      && typeof row.text === "string" && row.text.length <= 2048 && typeof row.textTruncated === "boolean"
      && (row.feature === undefined || row.feature === null || typeof row.feature === "string" && row.feature.length <= 32))
    && !(value.status === "ok" && value.rows.length > 0 && !logClearTarget(value));
}

/**
 * The messages this app has logged since it started, as a live log view: the newest messages are read when the
 * page opens and every two seconds after, coloured by level, and the view stays at its end unless the reader
 * scrolled up. The messages are filtered by level, by feature (the issue trackers, the providers...) and by words;
 * the levels of the features, which say what is written to the log, are changed here and saved at once.
 */
export function ApplicationLogsPanel({ read, clearActions, levelsApi, epoch = null }: {
  read: (request: ApplicationLogsRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<ApplicationLogsResponse>;
  clearActions: ReturnType<typeof createApplicationLogClearActions>;
  levelsApi?: ApplicationLogLevelsApi;
  /** The epoch of the host a change of the levels names. */
  epoch?: string | null;
}) {
  const { t } = useShellLanguage();
  const [snapshot, setSnapshot] = useState<ApplicationLogsResponse>();
  const [error, setError] = useState<MessageKey | "">("");
  const [wrap, setWrap] = useState(false);
  const [level, setLevel] = useState<"all" | LogTone>("all");
  const [query, setQuery] = useState("");
  const [feature, setFeature] = useState("");
  const [levels, setLevels] = useState<ApplicationLogLevelsResponse>();
  const [levelsOpen, setLevelsOpen] = useState(false);
  const [levelsSaving, setLevelsSaving] = useState(false);
  const [levelsError, setLevelsError] = useState(false);
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

  useEffect(() => {
    if (!levelsApi) return;
    const abort = new AbortController();
    void levelsApi.levels({}, { signal: abort.signal, timeoutMilliseconds: 8000 })
      .then(value => { if (!abort.signal.aborted && validLevels(value)) setLevels(value); }).catch(() => {});
    return () => abort.abort();
  }, [levelsApi]);

  // One choice of a level is saved at once; the answer is what the log now runs with.
  const saveLevel = (featureId: string | null, level: string | null) => {
    if (!levels || !levelsApi || levelsSaving) return;
    const next = changeLogLevels(levels, featureId, level);
    setLevelsSaving(true);
    setLevelsError(false);
    void levelsApi.saveLevels({ expectedEpoch: epoch, level: next.level, features: next.features }, { timeoutMilliseconds: 30_000 })
      // A refusal leaves the levels as they were: the answer says so, and the page keeps them.
      .then(value => { const saved = value.status === "ok" && validLevels(value); if (saved) setLevels(value); setLevelsError(!saved); })
      .catch(() => setLevelsError(true)).finally(() => setLevelsSaving(false));
  };

  const target = snapshot && logClearTarget(snapshot);
  const clearedPage = !!target && clearState.kind === "confirmed" && clearState.target?.boundary === target.boundary
    && clearState.target.captureId === target.captureId;
  const clearAllowed = !!target && !clearedPage && (clearState.kind === "idle" || clearState.kind === "confirmed"
    && clearState.target?.captureId === target.captureId && BigInt(target.boundary) > BigInt(clearState.target.boundary)
    && target.grant !== clearState.target.grant);
  const rows = snapshot?.status === "ok" && !clearedPage ? snapshot.rows : [];
  const shown = useMemo(() => filterLogRows(rows, { level, feature, query }), [rows, level, feature, query]);
  const counts = useMemo(() => countLogFeatures(rows), [rows]);
  // The features the host knows, in its order, then those of rows it did not list.
  const features = useMemo(() => {
    const known = (levels?.features ?? []).map(item => ({ id: item.id, name: item.name }));
    for (const id of counts.keys()) if (!known.some(item => item.id === id)) known.push({ id, name: id });
    if (feature && !known.some(item => item.id === feature)) known.push({ id: feature, name: feature });
    return known;
  }, [levels, counts, feature]);
  const featureName = (id: string, name = id) => featureNames[id] ? t(featureNames[id]) : name;
  const levelName = (id: string) => levelNames[id] ? t(levelNames[id]) : id;
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
      <HTMLSelect className="logs-feature-filter" value={feature} aria-label={t("Feature")} onChange={event => setFeature(event.target.value)}>
        <option value="">{t("All features")}</option>
        {features.map(item => <option key={item.id} value={item.id}>{t("{name} ({count})", { name: featureName(item.id, item.name), count: counts.get(item.id) ?? 0 })}</option>)}
      </HTMLSelect>
      <span className="settings-editor-spacer" />
      {levels?.status === "ok" && <PopoverNext placement="bottom-end" isOpen={levelsOpen} onInteraction={setLevelsOpen}
        content={<div className="logs-levels" role="group" aria-label={t("Log levels")}>
          <h2>{t("What is written to the log")}</h2>
          <p className="settings-field-help">{t(levels.editable
            ? "Debug and Trace write more details of a feature, such as each request of an issue tracker and what the service answered. Saved in config.toml ([logging])."
            : "The log levels can be changed only in the window of the running application.")}</p>
          <div className="logs-levels-grid">
            <label htmlFor="logs-level-all" data-setting="logging.level">{t("Default level")}</label>
            <HTMLSelect id="logs-level-all" value={levels.level} disabled={!levels.editable || levelsSaving} onChange={event => saveLevel(null, event.target.value)}>
              {levels.levels.map(item => <option key={item} value={item}>{levelName(item)}</option>)}
            </HTMLSelect>
            {levels.features.map(item => <FeatureLevel key={item.id} id={`logs-level-${item.id}`} label={featureName(item.id, item.name)} value={item.level ?? ""}
              disabled={!levels.editable || levelsSaving} onChange={value => saveLevel(item.id, value || null)}
              ariaLabel={t("Level of {feature}", { feature: featureName(item.id, item.name) })}>
              <option value="">{t("Default ({level})", { level: levelName(levels.level) })}</option>
              {levels.levels.map(choice => <option key={choice} value={choice}>{levelName(choice)}</option>)}
            </FeatureLevel>)}
          </div>
          {levelsError && <p role="alert" className="error-text">{t("The log levels could not be saved.")}</p>}
        </div>}>
        <Button size="small" icon={<AppIcon name="settings" size={14} />} aria-expanded={levelsOpen}>{t("Log levels")}</Button>
      </PopoverNext>}
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
        <time title={time.title}>{time.label}</time><span className="log-level">{row.level.toUpperCase()}</span><span className="log-logger" title={featureName(logRowFeature(row))}>{row.logger}</span>
        <span className="log-text">{row.text}{row.textTruncated && ` ${t("[message truncated]")}`}</span></div>; })}
      {snapshot?.status === "ok" && shown.length === 0 && <p className="logs-empty" role="status">{t(rows.length ? "No message matches the filter." : "No captured messages in this process.")}</p>}
    </div>
    {snapshot?.status === "ok" && <p className="logs-summary bp6-text-muted" role="status">{t("{shown} of {total} messages", { shown: shown.length, total: rows.length })}
      {(snapshot.captureOmitted !== "0" || snapshot.readOmitted > 0) && ` · ${t("Older entries omitted: {captureOmitted} from in-memory capacity, {readOmitted} from the bounded response.", { captureOmitted: snapshot.captureOmitted, readOmitted: snapshot.readOmitted })}`}</p>}
  </main>;
}

function FeatureLevel({ id, label, value, disabled, onChange, ariaLabel, children }: {
  id: string; label: string; value: string; disabled: boolean; onChange: (value: string) => void; ariaLabel: string; children: React.ReactNode;
}) {
  return <>
    <label htmlFor={id}>{label}</label>
    <HTMLSelect id={id} value={value} disabled={disabled} aria-label={ariaLabel} onChange={event => onChange(event.target.value)}>{children}</HTMLSelect>
  </>;
}
