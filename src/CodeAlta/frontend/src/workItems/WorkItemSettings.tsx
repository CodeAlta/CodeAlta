import { useState, useSyncExternalStore } from "react";
import { HTMLSelect, Switch } from "@blueprintjs/core";
import type { WorkItemsSettings } from "#neoastra";
import type { MessageKey } from "../localization";
import { SettingsPage, SettingsUnavailable } from "../SettingsPage";
import type { SettingsNotice } from "../settingsEditing";
import { useShellLanguage } from "../shellLanguage";
import { startLabel, workStarts } from "./workItems";
import type { WorkItemsHub } from "./workItemsHub";

/**
 * Settings page for work items: whether agents may propose follow-up tasks and whether a session shows them,
 * which way of starting comes first, and what becomes of the tasks and the plans that are closed. A choice
 * is saved at once, in the configuration file of the user.
 */
export function WorkItemSettings({ hub }: { hub: WorkItemsHub }) {
  const { t } = useShellLanguage();
  const state = useSyncExternalStore(hub.subscribe, hub.getSnapshot, hub.getSnapshot);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<SettingsNotice | null>(null);
  const settings = state.settings;
  async function save(change: Partial<WorkItemsSettings>) {
    setBusy(true); setNotice(null);
    const saved = await hub.saveSettings({ ...settings, ...change });
    setBusy(false);
    setNotice(saved ? { key: "Saved.", intent: "success" } : { key: "The operation did not complete.", intent: "danger" });
  }
  const row = (title: MessageKey, detail: MessageKey, control: React.ReactNode) =>
    <label className="work-settings-row"><span><strong>{t(title)}</strong><small>{t(detail)}</small></span>{control}</label>;
  const closing = (value: string, onChange: (next: string) => void, label: MessageKey) =>
    <HTMLSelect aria-label={t(label)} value={value} disabled={busy} onChange={event => onChange(event.target.value)}>
      <option value="delete">{t("Delete the file")}</option>
      <option value="keep">{t("Keep the file")}</option>
    </HTMLSelect>;

  return <SettingsPage className="work-settings" label={t("Work items")} group="Agent & models" title="Work items"
    description="The follow-up tasks agents propose, and plans." notice={notice} loading={!state.loaded} busy={busy} onReload={() => void hub.refresh()}>
    {!state.available ? <SettingsUnavailable loading={!state.loaded} icon="task" title="Work items are unavailable in this window." /> : <>
      <section className="work-settings-group">
        <h2>{t("Proposals")}</h2>
        {row("Let agents propose follow-up tasks", "When an agent finds a real gap, a problem or an improvement beside what it was asked, it writes a task for you to decide on.",
          <Switch checked={settings.propose} disabled={busy} aria-label={t("Let agents propose follow-up tasks")} onChange={event => void save({ propose: event.currentTarget.checked })} />)}
        {row("Show proposals in the session", "A session shows the tasks it proposed and the plan it had approved as cards over its top right corner. Without it they are only in the Work items tab.",
          <Switch checked={settings.notify} disabled={busy} aria-label={t("Show proposals in the session")} onChange={event => void save({ notify: event.currentTarget.checked })} />)}
      </section>
      <section className="work-settings-group">
        <h2>{t("Starting work")}</h2>
        {row("Start by default", "The way of starting that the cards and the Work items tab put first.",
          <HTMLSelect aria-label={t("Start by default")} value={settings.start} disabled={busy} onChange={event => void save({ start: event.target.value })}>
            {workStarts.map(start => <option key={start} value={start}>{t(startLabel(start))}</option>)}
          </HTMLSelect>)}
      </section>
      <section className="work-settings-group">
        <h2>{t("Closed tasks")}</h2>
        {row("When a task is completed", "A kept task stays in its folder with the status done, and shows under Closed.",
          closing(settings.completedTasks, completedTasks => void save({ completedTasks }), "When a task is completed"))}
        {row("When a task is dismissed", "A kept task stays in its folder with the status dismissed, and shows under Closed.",
          closing(settings.dismissedTasks, dismissedTasks => void save({ dismissedTasks }), "When a task is dismissed"))}
      </section>
      <section className="work-settings-group">
        <h2>{t("Completed plans")}</h2>
        {row("When a plan is completed", "A kept plan stays in its folder with the status done, and shows under Closed.",
          closing(settings.completedPlans, completedPlans => void save({ completedPlans }), "When a plan is completed"))}
      </section>
    </>}
  </SettingsPage>;
}
