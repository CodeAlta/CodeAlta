import { useCallback, useEffect, useRef, useState, type CSSProperties, type ReactNode } from "react";
import { Button, Callout, Card, HTMLSelect, HTMLTable, NumericInput, PopoverNext, Switch } from "@blueprintjs/core";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { Chart } from "../charts/Chart";
import { seriesVariable } from "../charts/palette";
import { SettingsPage } from "../SettingsPage";
import { SettingsFileLocations } from "../SettingsFileLocation";
import { useSettingsFiles, type SettingsFilesApi } from "../settingsFiles";
import { settingsFailure, type SettingsNotice } from "../settingsEditing";
import { useShellLanguage } from "../shellLanguage";
import { measuredPercentage, retentionAmount, retentionUnits, storageBytes, storageCategories, storageSize, type StorageApi, type StorageMeasurement, type StoragePreferences } from "./storage";
import { retentionProgress } from "./maintenance";
import "./storage.css";

/** Host-owned scratchpad maintenance feedback. Closing this page cancels measurement only, never maintenance. */
export interface StorageMaintenanceSummary {
  state: "running" | "completed" | "partial" | "skipped" | "canceled" | "unavailable";
  reasons: readonly string[];
  processedFiles?: string;
  processedDirectories?: string;
}

/** Host-owned session retention feedback. An unknown state reads as unavailable and unknown reasons are never shown raw. */
export interface StorageRetentionSummary {
  state: string;
  examined: number;
  deleted: number;
  skipped: number;
  reason: string | null;
  /** Sessions the pass checks; zero or missing until it has listed them. */
  total?: number;
  /** Bytes the pass removed so far (an Int64 wire string). */
  freedBytes?: string;
}

/** Global storage preferences and a read-only measurement of the profile. No category has a removal action. */
export function StorageSettings({ epoch, api, filesApi, onOpenFile, maintenance, retention }: {
  epoch: string | null; api: StorageApi; filesApi?: SettingsFilesApi; onOpenFile?: () => void;
  maintenance?: StorageMaintenanceSummary; retention?: StorageRetentionSummary;
}) {
  const { t, locale } = useShellLanguage();
  const [settings, setSettings] = useState<StoragePreferences | null>(null);
  const [rootPath, setRootPath] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<SettingsNotice | null>(null);
  const [revision, setRevision] = useState(0);
  const [measurement, setMeasurement] = useState<StorageMeasurement | null>(null);
  const [scanState, setScanState] = useState<"loading" | "ready" | "error" | "canceled">("loading");
  const [progress, setProgress] = useState({ processedFiles: "0", processedDirectories: "0" });
  const scanController = useRef<AbortController | null>(null);
  const writeController = useRef<AbortController | null>(null);
  const files = useSettingsFiles({ page: "config", epoch, projectId: null, revision: settings, onOpened: onOpenFile, setNotice, api: filesApi });

  const refresh = useCallback(() => {
    scanController.current?.abort();
    const controller = new AbortController();
    scanController.current = controller;
    if (!epoch) { setScanState("error"); return; }
    setScanState("loading");
    setProgress({ processedFiles: "0", processedDirectories: "0" });
    void (async () => {
      try {
        for await (const reply of await api.scan({ expectedEpoch: epoch }, { signal: controller.signal, timeoutMilliseconds: 20_000 })) {
          if (controller.signal.aborted) return;
          setProgress({ processedFiles: reply.processedFiles, processedDirectories: reply.processedDirectories });
          if (reply.status === "running") continue;
          if ((reply.status === "ok" || reply.status === "partial") && reply.snapshot) {
            setMeasurement(reply.snapshot); setScanState("ready");
          } else setScanState(reply.status === "canceled" ? "canceled" : "error");
          return;
        }
        // A disconnected/empty stream is not a completed scan.
        if (!controller.signal.aborted) setScanState("error");
      } catch { if (!controller.signal.aborted) setScanState("error"); }
    })();
  }, [api, epoch]);
  useEffect(() => {
    setMeasurement(null); refresh();
    return () => scanController.current?.abort();
  }, [refresh]);
  useEffect(() => {
    const controller = new AbortController();
    setLoading(true); setBusy(false); setSettings(null); setRootPath(null); setNotice(null);
    if (!epoch) { setLoading(false); return () => controller.abort(); }
    void api.readSettings({ expectedEpoch: epoch }, { signal: controller.signal, timeoutMilliseconds: 20_000 }).then(reply => {
      if (controller.signal.aborted) return;
      setRootPath(reply.rootPath);
      if (reply.status === "ok" && reply.settings) setSettings(reply.settings);
      else setNotice({ key: "Storage settings could not be read. Cleanup remains unchanged.", intent: "danger", detail: reply.error ?? undefined });
    }).catch(() => { if (!controller.signal.aborted) setNotice({ key: "Storage settings could not be read. Cleanup remains unchanged.", intent: "danger" }); })
      .finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [api, epoch, revision]);
  useEffect(() => () => { writeController.current?.abort(); writeController.current = null; }, [api, epoch]);

  const save = async (next: StoragePreferences) => {
    if (!epoch || writeController.current || !settings) return;
    if (JSON.stringify(next) === JSON.stringify(settings)) return;
    const controller = new AbortController(); writeController.current = controller;
    setBusy(true); setNotice(null);
    try {
      const reply = await api.saveSettings({ expectedEpoch: epoch, settings: next }, { signal: controller.signal, timeoutMilliseconds: 30_000 });
      if (controller.signal.aborted) return;
      if (reply.status === "ok" && reply.settings) {
        setSettings(reply.settings); setNotice({ key: "Saved. Cleanup changes apply at the next startup.", intent: "success" });
      } else setNotice({ key: "Storage settings could not be saved.", intent: "danger", detail: reply.error ?? settingsFailure(reply.status)?.detail });
    } catch { if (!controller.signal.aborted) setNotice({ key: "Storage settings could not be saved.", intent: "danger" }); }
    finally { if (!controller.signal.aborted) { writeController.current = null; setBusy(false); } }
  };

  const measuredRoot = measurement?.rootPath ?? rootPath;
  return <SettingsPage className="storage-settings" label={t("Storage")} group="Advanced" title="Storage"
    notice={notice} loading={loading} busy={busy} onReload={() => setRevision(value => value + 1)}>
    <div className="storage-layout">
      {/* The controls come first: they are what a visitor changes, and stay above the fold in a small Settings window. */}
      <Card className="storage-card storage-cleanup" role="region" aria-labelledby="storage-cleanup-title">
        <div className="storage-card-heading">
          <h2 id="storage-cleanup-title">{t("Automatic cleanup")}</h2>
          <StorageHelp label={t("About automatic cleanup")}>
            <p>{t("Only this instance's scratchpad and sessions inactive for longer than the set time are removed.")}</p>
            <p>{t("Open sessions, drafts and active work are kept, and Statistics are saved first.")}</p>
          </StorageHelp>
          <span className="storage-hint">{t("Applies at next startup")}</span>
        </div>
        {!settings ? <p className="storage-hint" role="status">{t(loading ? "Loading…" : "Storage settings unavailable")}</p> : <>
          <div className="settings-field storage-field" data-setting="storage.clear_scratchpad_on_startup">
            <label htmlFor="storage-scratchpad">{t("Clear scratchpad on startup")}</label>
            <div className="settings-field-control">
              <Switch id="storage-scratchpad" checked={settings.clearScratchpadOnStartup} disabled={busy}
                onChange={event => void save({ ...settings, clearScratchpadOnStartup: event.currentTarget.checked })} />
            </div>
          </div>
          <RetentionEditor settings={settings} disabled={busy} onSave={next => void save(next)} />
        </>}
        {maintenance && <MaintenanceStatus maintenance={maintenance} />}
        {retention && <RetentionStatus retention={retention} />}
        <SettingsFileLocations files={files} disabled={busy} />
      </Card>
      <Card className="storage-card storage-usage" role="region" aria-labelledby="storage-distribution-title">
        <div className="storage-card-heading">
          <h2 id="storage-distribution-title">{t("File sizes")}</h2>
          <StorageHelp label={t("About file sizes")}>
            <p>{t("Logical sizes, which can differ from disk usage. Links are not followed.")}</p>
            <p>{t("Nothing can be deleted here.")}</p>
            {measuredRoot && <code className="storage-help-path">{measuredRoot}</code>}
          </StorageHelp>
          {measurement && <span className="storage-total" title={measuredRoot ?? undefined}>{storageSize(storageBytes(measurement.totalBytes), locale)}</span>}
          <span className="storage-heading-actions">
            {scanState === "loading" && <>
              <span className="storage-hint storage-scanning" role="status"><ActivitySpinner size={12} />{t("Measuring file sizes…")}</span>
              <Button size="small" variant="minimal" onClick={() => { scanController.current?.abort(); setScanState("canceled"); }}>{t("Cancel scan")}</Button>
            </>}
            <Button size="small" variant="minimal" icon={<AppIcon name="refresh" size={14} />} onClick={refresh}>{t("Refresh")}</Button>
          </span>
        </div>
        {scanState === "loading" && <p className="storage-hint storage-progress" role="status">{t("{files} files and {folders} folders processed.", {
          files: new Intl.NumberFormat(locale).format(storageBytes(progress.processedFiles)),
          folders: new Intl.NumberFormat(locale).format(storageBytes(progress.processedDirectories)),
        })}</p>}
        {measurement && <p className="storage-hint storage-scan-time">{t("Last scan: {time}", { time: new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" }).format(new Date(measurement.completedAt)) })}</p>}
        {scanState === "canceled" && <p className="storage-hint" role="status">{t("Scan canceled.")}</p>}
        {scanState === "error" && <Callout compact intent="warning" role="alert">{t("Storage could not be measured. Refresh to try again.")}</Callout>}
        {measurement && <StorageDistribution measurement={measurement} />}
      </Card>
    </div>
  </SettingsPage>;
}

/** A short explanation behind a quiet info button, instead of paragraphs on the page. Click or keyboard opens it. */
function StorageHelp({ label, children }: { label: string; children: ReactNode }) {
  return <PopoverNext placement="bottom-start" content={<div className="storage-help">{children}</div>}>
    <Button className="storage-help-button" variant="minimal" size="small" icon={<AppIcon name="info" size={14} />} aria-label={label} title={label} />
  </PopoverNext>;
}

const maintenanceTitles = {
  running: "Scratchpad cleanup is running…", completed: "Scratchpad cleanup completed.", partial: "Scratchpad cleanup was partial.",
  skipped: "Scratchpad cleanup was skipped.", canceled: "Scratchpad cleanup was canceled.", unavailable: "Scratchpad cleanup status is unavailable.",
} as const;

/** One line of host-owned scratchpad cleanup feedback, with its bounded reasons folded below it. A state it does not know reads as unavailable. */
function MaintenanceStatus({ maintenance }: { maintenance: StorageMaintenanceSummary }) {
  const { t, locale } = useShellLanguage();
  const state = Object.hasOwn(maintenanceTitles, maintenance.state) ? maintenance.state : "unavailable";
  const reasons = maintenance.reasons.slice(0, 8).map(reason => reason.slice(0, 500));
  const icon = state === "running" ? <ActivitySpinner size={12} />
    : <AppIcon name={state === "partial" ? "warning" : state === "completed" ? "check" : "info"} size={14} />;
  return <div className="storage-maintenance" data-state={state}>
    <span className="storage-maintenance-title" role="status">{icon}<span>{t(maintenanceTitles[state])}</span></span>
    {state === "running" && <span className="storage-hint" role="status">{t("{files} files and {folders} folders processed.", {
      files: new Intl.NumberFormat(locale).format(storageBytes(maintenance.processedFiles ?? "0")),
      folders: new Intl.NumberFormat(locale).format(storageBytes(maintenance.processedDirectories ?? "0")),
    })}</span>}
    {reasons.length > 0 && <details>
      <summary>{t("Details")}</summary>
      <ul>{reasons.map((reason, index) => <li key={index}>{reason}</li>)}</ul>
    </details>}
  </div>;
}

// Known skip reasons only; anything else stays a plain "skipped" without host text.
const retentionReasons: Readonly<Record<string, Readonly<{ title?: "Session cleanup is not available yet." | "Session cleanup is off."; detail?: "Saved Statistics are unavailable." | "Check the retention duration." }>>> = Object.freeze({
  external_admission_unqualified: { title: "Session cleanup is not available yet." },
  desktop_admission_unqualified: { title: "Session cleanup is not available yet." },
  protection_unavailable: { title: "Session cleanup is not available yet." },
  disabled: { title: "Session cleanup is off." },
  archive_unavailable: { detail: "Saved Statistics are unavailable." },
  invalid_cutoff: { detail: "Check the retention duration." },
});

/** One line of host-owned session retention feedback, separate from the scratchpad line so neither claims the other's result. */
function RetentionStatus({ retention }: { retention: StorageRetentionSummary }) {
  const { t, locale } = useShellLanguage();
  const count = (value: number) => new Intl.NumberFormat(locale).format(Number.isFinite(value) ? Math.max(0, Math.floor(value)) : 0);
  const { state } = retention;
  const reason = state === "skipped" && retention.reason ? retentionReasons[retention.reason] : undefined;
  const title = state === "running" ? t("Session cleanup is running…")
    : state === "completed" ? t("Session cleanup completed: {deleted} removed.", { deleted: count(retention.deleted) })
    : state === "partial" ? t("Session cleanup was partial: {deleted} removed, {skipped} kept.", { deleted: count(retention.deleted), skipped: count(retention.skipped) })
    : state === "skipped" ? t(reason?.title ?? "Session cleanup was skipped.")
    : state === "canceled" ? t("Session cleanup was canceled.")
    : t("Session cleanup status is unavailable.");
  const icon = state === "running" ? <ActivitySpinner size={12} />
    : <AppIcon name={state === "partial" ? "warning" : state === "completed" ? "check" : "info"} size={14} />;
  const known = ["running", "completed", "partial", "skipped", "canceled"].includes(state);
  const progress = known && state !== "skipped" ? retentionProgress(locale, retention) : undefined;
  return <div className="storage-maintenance storage-session-cleanup" data-state={known ? state : "unavailable"}>
    <span className="storage-maintenance-title" role="status">{icon}<span>{title}</span></span>
    {progress && <span className="storage-hint storage-session-cleanup-progress" role="status">{progress}</span>}
    {reason?.detail && <span className="storage-hint">{t(reason.detail)}</span>}
  </div>;
}

/** An integer draft stays local until a valid blur, Enter, step or unit change. Never does not need a valid draft. */
export function RetentionEditor({ settings, disabled, onSave }: { settings: StoragePreferences; disabled: boolean; onSave: (next: StoragePreferences) => void }) {
  const { t } = useShellLanguage();
  const [draft, setDraft] = useState(String(settings.retentionAmount));
  useEffect(() => setDraft(String(settings.retentionAmount)), [settings.retentionAmount]);
  const amount = retentionAmount(draft);
  const commit = (value = draft, unit = settings.retentionUnit) => {
    const valid = retentionAmount(value);
    if (valid !== null && settings.retentionEnabled && retentionUnits.includes(unit as typeof retentionUnits[number]))
      onSave({ ...settings, retentionAmount: valid, retentionUnit: unit });
  };
  return <div className="settings-field storage-field storage-retention" data-setting="storage.session_retention.enabled storage.session_retention.amount storage.session_retention.unit">
    <label htmlFor="storage-retention-mode">{t("Delete inactive sessions")}</label>
    <div className="settings-field-control storage-retention-controls"
      onBlur={event => { if (!(event.relatedTarget instanceof Node) || !event.currentTarget.contains(event.relatedTarget)) commit(); }}
      // Blueprint rounds an invalid fractional draft when stepping. Do not turn that draft into a shorter saved policy.
      onMouseDownCapture={event => { if (amount === null && event.target instanceof Element && event.target.closest(".bp6-numeric-input button")) { event.preventDefault(); event.stopPropagation(); } }}
      onKeyDownCapture={event => {
        if (amount === null && event.target instanceof Element && event.target.closest(".bp6-numeric-input")
          && (event.key === "ArrowUp" || event.key === "ArrowDown" || (event.target.closest("button") && (event.key === " " || event.key === "Enter")))) {
          event.preventDefault(); event.stopPropagation();
        }
      }}>
      <HTMLSelect id="storage-retention-mode" value={settings.retentionEnabled ? "duration" : "never"} disabled={disabled}
        onChange={event => {
          // Never remains available for invalid drafts, but must not discard a valid unsaved duration.
          const nextAmount = amount ?? settings.retentionAmount;
          setDraft(String(nextAmount));
          onSave({ ...settings, retentionAmount: nextAmount, retentionEnabled: event.currentTarget.value === "duration" });
        }}>
        <option value="duration">{t("After")}</option><option value="never">{t("Never")}</option>
      </HTMLSelect>
      <NumericInput id="storage-retention-amount" aria-label={t("Retention amount")} aria-describedby={amount === null ? "storage-retention-error" : undefined}
        aria-invalid={amount === null} value={draft} min={1} stepSize={1} minorStepSize={null} majorStepSize={1}
        clampValueOnBlur={false} buttonPosition="right" disabled={disabled || !settings.retentionEnabled}
        onValueChange={(_value, text) => setDraft(text)} onButtonClick={(_value, text) => commit(text)}
        onKeyUp={event => { if (event.key === "Enter" || event.key === "ArrowUp" || event.key === "ArrowDown") commit(event.currentTarget.value); }} />
      <HTMLSelect aria-label={t("Retention unit")} value={settings.retentionUnit} disabled={disabled || !settings.retentionEnabled}
        onChange={event => commit(draft, event.currentTarget.value)}>
        {retentionUnits.map(unit => <option key={unit} value={unit}>{t(unit)}</option>)}
      </HTMLSelect>
    </div>
    {amount === null && <p id="storage-retention-error" role="alert" className="storage-validation">{t("Enter a positive whole number.")}</p>}
  </div>;
}

/**
 * One permanent accessible table, then a donut whose legend it is: the swatch of a row is the color of its
 * slice. Rows are sorted by size; colors stay with their category. Percentages always use the whole measured total.
 */
export function StorageDistribution({ measurement }: { measurement: StorageMeasurement }) {
  const { t, locale } = useShellLanguage();
  const total = storageBytes(measurement.totalBytes);
  const rows = storageCategories.map(([id, label], index) => ({ id, index, label: t(label), bytes: storageBytes(measurement.categories.find(value => value.id === id)?.bytes ?? "0") }));
  const sorted = [...rows].sort((left, right) => right.bytes - left.bytes || left.index - right.index);
  const share = new Intl.NumberFormat(locale, { style: "percent", maximumFractionDigits: 1 });
  // A category that holds bytes never reads as 0%.
  const percentage = (bytes: number) => bytes > 0 && measuredPercentage(bytes, total) < .001 ? `<${share.format(.001)}` : share.format(measuredPercentage(bytes, total));
  return <div className="storage-measurement">
    {measurement.isPartial && <Callout compact intent="warning" role="status">{t("Partial scan: {links} links and {entries} unavailable entries skipped.",
      { links: measurement.skippedLinks, entries: measurement.skippedEntries })}</Callout>}
    {total === 0 && <p className="storage-hint" role="status">{t("No file bytes were measured.")}</p>}
    <div className="storage-distribution" data-chart={total > 0 || undefined}>
      <HTMLTable className="storage-table">
        <caption>{t("Measured file sizes by category")}</caption>
        <thead><tr><th scope="col">{t("Category")}</th><th scope="col">{t("Size")}</th><th scope="col">{t("Share")}</th></tr></thead>
        <tbody>{sorted.map(row => <tr key={row.id} data-empty={row.bytes === 0 || undefined}>
          <th scope="row"><span className="storage-swatch" aria-hidden="true" style={{ "--storage-swatch": `var(${seriesVariable(row.index)})` } as CSSProperties} />{row.label}</th>
          <td>{storageSize(row.bytes, locale)}</td><td>{percentage(row.bytes)}</td></tr>)}</tbody>
      </HTMLTable>
      {/* No `legend` in the option: the table is the legend, so the chart draws none of its own. Its size is in the CSS. */}
      {total > 0 && <Chart className="storage-donut" ariaLabel={t("Storage distribution")} height={168} hideTableToggle
        option={{ tooltip: { trigger: "item", formatter: "{b}: {d}%" }, series: [{ type: "pie", radius: ["56%", "94%"], padAngle: 1, itemStyle: { borderRadius: 2 },
          label: { show: false }, emphasis: { scale: false }, data: rows.map(row => ({ name: row.label, value: row.bytes })) }] }} />}
    </div>
  </div>;
}
