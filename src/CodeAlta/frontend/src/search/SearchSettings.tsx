import { useState } from "react";
import { Button, Card, HTMLSelect, ProgressBar, Switch } from "@blueprintjs/core";
import type { LocalSearchMeaning, LocalSearchSettings, LocalSearchStatusResponse } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { SettingsField as Field } from "../SettingsField";
import { SettingsPage, SettingsUnavailable } from "../SettingsPage";
import type { SettingsNotice } from "../settingsEditing";
import { useShellLanguage } from "../shellLanguage";
import { useLocalSearch } from "./localSearchContext";
import { MeaningDialogHost } from "./MeaningDialog";
import { durationParts, kilobytesText } from "./meaningEstimate";

/** What a part of the index is called, by the name of its kind. */
export type NamedEntry = Readonly<{ id: string; name: string }>;

/** The words of the state of the index: up to date, at work, or what went wrong. */
export function indexSummary(status: LocalSearchStatusResponse): Readonly<{ items: number; kilobytes: number; state: "ready" | "indexing" | "starting"; done: number; total: number }> {
  const items = Object.values(status.items).reduce((sum, count) => sum + count, 0);
  const state = status.busy && status.journalsTotal > 0 && status.journalsDone < status.journalsTotal ? "indexing" : status.ready ? "ready" : "starting";
  return { items, kilobytes: status.kilobytes, state, done: status.journalsDone, total: status.journalsTotal };
}

/** A size on disk, as the page shows it. */
export function formatSize(kilobytes: number, locale: string): string {
  const format = (value: number, digits: number) => value.toLocaleString(locale, { maximumFractionDigits: digits });
  return kilobytes >= 1024 * 1024 ? `${format(kilobytes / (1024 * 1024), 1)} GB` : kilobytes >= 1024 ? `${format(kilobytes / 1024, 1)} MB` : `${format(kilobytes, 0)} KB`;
}

/**
 * Settings page of the local search: the index, what it holds and whether the conversations are part of it, and the
 * projects and spaces kept out of it. The choices are saved at once in the configuration file of the user.
 */
export function SearchSettings({ projects, spaces, onOpenCanvas, onMeaningTurnedOn }: {
  /** The projects of the catalog, which name the excluded ones. */
  projects: readonly NamedEntry[];
  spaces: readonly NamedEntry[];
  /** Opens the Search canvas, where the index can be looked at and tried; absent where it cannot be opened. */
  onOpenCanvas?: () => void;
  /** Called once search by meaning was turned on from here: the window follows its progress. */
  onMeaningTurnedOn?: () => void;
}) {
  const { t, locale } = useShellLanguage();
  const { hub, state } = useLocalSearch();
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<SettingsNotice | null>(null);
  const [dialog, setDialog] = useState(false);
  const status = state.status;
  const settings = status?.settings ?? null;
  const semantic = status?.semantic ?? null;

  async function run(call: () => Promise<string>, saved = true) {
    setBusy(true);
    try {
      const outcome = await call();
      setNotice(outcome === "ok" ? saved ? { intent: "success", key: "Saved." } : null : { intent: "danger", key: "The search settings could not be saved." });
    } finally { setBusy(false); }
  }
  const save = (change: Partial<LocalSearchSettings>) => settings && hub ? run(() => hub.save({ ...settings, ...change })) : undefined;
  const name = (list: readonly NamedEntry[], id: string) => list.find(entry => entry.id.toLowerCase() === id.toLowerCase())?.name ?? id;
  const summary = status ? indexSummary(status) : null;

  return <SettingsPage className="search-settings" label={t("Search")} group="Personalization" title="Search"
    description="Find your conversations, the guide, skills and tools, on this computer only." notice={notice} loading={!state.loaded} busy={busy}
    onReload={() => void hub?.refresh()}>
    {!hub || !state.available || !settings || !summary ? <SettingsUnavailable loading={!!hub && !state.loaded} icon="search" title="Search is not available here" /> :
      <div className="settings-grid">
        <Card className="appearance-settings" aria-labelledby="settings-search-index-title">
          <h2 id="settings-search-index-title">{t("Search index")}</h2>
          <Field label={t("Search index")} htmlFor="settings-search-enabled"
            notice={<p className="settings-field-help">{t("Conversations, plans, skills, tools, MCP servers, plugins and the guide, for the search window and for the agents' alta search. Off, nothing is indexed.")}</p>}>
            <Switch id="settings-search-enabled" className="settings-checkbox" checked={settings.enabled} disabled={busy} onChange={event => void save({ enabled: event.currentTarget.checked })} />
          </Field>
          <Field label={t("Conversations")} htmlFor="settings-search-sessions"
            notice={<p className="settings-field-help">{t("The prompts and the final answers of your sessions, and their notes. Off keeps only the guide, the plans, the skills and the tools.")}</p>}>
            <Switch id="settings-search-sessions" className="settings-checkbox" checked={settings.sessions} disabled={busy || !settings.enabled}
              onChange={event => void save({ sessions: event.currentTarget.checked })} />
          </Field>
          {settings.enabled && <div className="settings-field search-settings-state">
            <label>{t("Index")}</label>
            <span />
            <p className="settings-field-help" role="status">
              {t("{count} items", { count: summary.items.toLocaleString(locale) })} · {formatSize(summary.kilobytes, locale)} · {summary.state === "indexing"
                ? t("indexing {done} of {total} sessions", { done: summary.done, total: summary.total })
                : summary.state === "ready" ? t("up to date") : t("starting")}
              {status?.lastError && <><br /><span className="search-settings-error">{status.lastError}</span></>}
            </p>
            <span className="search-settings-actions">
              <Button size="small" icon={<AppIcon name="refresh" size={14} />} disabled={busy} onClick={() => void run(() => hub.reindex(), false)}>{t("Check now")}</Button>
              {onOpenCanvas && <Button size="small" icon={<AppIcon name="search" size={14} />} onClick={onOpenCanvas}>{t("Open Search")}</Button>}
            </span>
          </div>}
        </Card>
        {semantic && settings.enabled && <MeaningCard meaning={semantic} busy={busy} onOpen={() => setDialog(true)}
          onTurnOff={() => void run(() => hub.turnOffMeaning())} onModel={id => void run(() => hub.turnOnMeaning(id))}
          onPause={paused => void run(() => hub.pauseMeaning(paused), false)} onCancel={() => void run(() => hub.cancelDownload(), false)}
          onDelete={id => void run(() => hub.deleteModel(id))} onRetry={() => void run(() => hub.turnOnMeaning(semantic.modelId), false)} />}
        <Card className="appearance-settings" aria-labelledby="settings-search-excluded-title">
          <h2 id="settings-search-excluded-title">{t("Kept out of search")}</h2>
          <p className="settings-field-help">{t("Nothing of these projects and spaces is indexed, and no result shows them, for you or for an agent. Keep one out from the menu of a project or the settings of a space.")}</p>
          {settings.excludedProjects.length + settings.excludedSpaces.length === 0
            ? <p className="search-settings-none">{t("Every project and every space is searched.")}</p>
            : <ul className="search-settings-excluded" aria-label={t("Kept out of search")}>
              {[...settings.excludedProjects.map(id => ({ kind: "project" as const, id, label: name(projects, id) })),
                ...settings.excludedSpaces.map(id => ({ kind: "space" as const, id, label: name(spaces, id) }))].map(entry =>
                <li key={`${entry.kind}:${entry.id}`}>
                  <AppIcon name={entry.kind === "project" ? "folder" : "space"} size={14} />
                  <span className="search-settings-excluded-name">{entry.label}</span>
                  <span className="search-settings-excluded-kind">{t(entry.kind === "project" ? "Project" : "Space")}</span>
                  <Button size="small" variant="minimal" disabled={busy} onClick={() => void run(() => hub.setExcluded(entry.kind, entry.id, false))}>{t("Include in search")}</Button>
                </li>)}
            </ul>}
        </Card>
      </div>}
    {dialog && <MeaningDialogHost offered={false} onClose={() => setDialog(false)} onTurnedOn={onMeaningTurnedOn} />}
  </SettingsPage>;
}

/** The words of the state of search by meaning, with the numbers it has. */
export function meaningStateText(meaning: LocalSearchMeaning, t: ReturnType<typeof useShellLanguage>["t"], locale: string): string {
  const left = meaning.secondsLeft === null ? null : durationParts(meaning.secondsLeft);
  const remaining = !left ? "" : left.unit === "seconds" ? t("about {count} s left", { count: left.value })
    : left.unit === "minutes" ? t("about {count} min left", { count: left.value }) : t("about {hours} h {minutes} min left", { hours: left.value, minutes: left.minutes });
  switch (meaning.state) {
    case "downloading": return t("Downloading — {received} of {total}", { received: kilobytesText(meaning.download.receivedKilobytes, locale), total: kilobytesText(meaning.download.totalKilobytes, locale) });
    case "loading": return t("Loading the model…");
    case "indexing": return [t("Indexing — {done} of {total}", { done: meaning.embedded.toLocaleString(locale), total: meaning.total.toLocaleString(locale) }), remaining].filter(Boolean).join(" · ");
    case "paused": return t("Paused — {done} of {total}", { done: meaning.embedded.toLocaleString(locale), total: meaning.total.toLocaleString(locale) });
    case "ready": return t("Ready — {count} chunks", { count: meaning.total.toLocaleString(locale) });
    case "failed": return t("Stopped: {reason}", { reason: meaning.error ?? meaning.download.error ?? "" });
    default: return t("Off");
  }
}

/** The card of search by meaning: on or off, the model, the progress and what can be done with it. */
function MeaningCard({ meaning, busy, onOpen, onTurnOff, onModel, onPause, onCancel, onDelete, onRetry }: {
  meaning: LocalSearchMeaning; busy: boolean; onOpen: () => void; onTurnOff: () => void; onModel: (id: string) => void;
  onPause: (paused: boolean) => void; onCancel: () => void; onDelete: (id: string) => void; onRetry: () => void;
}) {
  const { t, locale } = useShellLanguage();
  const progress = meaning.state === "downloading" ? meaning.download.totalKilobytes > 0 ? meaning.download.receivedKilobytes / meaning.download.totalKilobytes : undefined
    : meaning.state === "indexing" || meaning.state === "paused" ? meaning.total > 0 ? meaning.embedded / meaning.total : undefined : null;
  const unused = meaning.models.filter(model => model.installed && (!meaning.enabled || model.id !== meaning.modelId));
  return <Card className="appearance-settings" aria-labelledby="settings-search-meaning-title">
    <h2 id="settings-search-meaning-title">{t("Search by meaning")}</h2>
    {!meaning.supported ? <p className="settings-field-help">{t("Search by meaning needs an ONNX Runtime for this platform, which it does not have.")}</p> : <>
      <Field label={t("Search by meaning")} htmlFor="settings-search-meaning"
        notice={<p className="settings-field-help">{t("Finds what you look for from what it is about, in any language. The model runs on this computer.")}</p>}>
        <Switch id="settings-search-meaning" className="settings-checkbox" checked={meaning.enabled} disabled={busy}
          onChange={event => { if (event.currentTarget.checked) onOpen(); else onTurnOff(); }} />
      </Field>
      {meaning.enabled && <>
        <Field label={t("Model")} htmlFor="settings-search-model">
          <HTMLSelect id="settings-search-model" value={meaning.modelId} disabled={busy} onChange={event => onModel(event.target.value)}>
            {meaning.models.map(model => <option key={model.id} value={model.id}>{`${model.name} · ${model.summary} · ${kilobytesText(model.kilobytes, locale)}`}</option>)}
          </HTMLSelect>
        </Field>
        <div className="settings-field search-settings-state">
          <label>{t("State")}</label>
          <span />
          <div className="search-settings-meaning-state">
            <p className="settings-field-help" role="status">{meaningStateText(meaning, t, locale)}</p>
            {progress !== null && <ProgressBar value={progress} intent={meaning.state === "paused" ? "warning" : "primary"} animate={meaning.state !== "paused"} stripes={meaning.state !== "paused"} />}
          </div>
          <span className="search-settings-actions">
            {meaning.state === "downloading" && <Button size="small" disabled={busy} onClick={onCancel}>{t("Cancel")}</Button>}
            {meaning.state === "indexing" && <Button size="small" icon={<AppIcon name="pause" size={14} />} disabled={busy} onClick={() => onPause(true)}>{t("Pause")}</Button>}
            {meaning.state === "paused" && <Button size="small" icon={<AppIcon name="play" size={14} />} disabled={busy} onClick={() => onPause(false)}>{t("Resume")}</Button>}
            {meaning.state === "failed" && <Button size="small" icon={<AppIcon name="refresh" size={14} />} disabled={busy} onClick={onRetry}>{t("Try again")}</Button>}
          </span>
        </div>
      </>}
      {unused.map(model => <div key={model.id} className="settings-field">
        <label>{model.name}</label>
        <span className="search-settings-actions">
          <span className="settings-field-help">{t("Downloaded · {size}", { size: kilobytesText(model.kilobytes, locale) })}</span>
          <Button size="small" variant="minimal" intent="danger" icon={<AppIcon name="trash" size={14} />} disabled={busy} onClick={() => onDelete(model.id)}>{t("Delete")}</Button>
        </span>
      </div>)}
    </>}
  </Card>;
}
