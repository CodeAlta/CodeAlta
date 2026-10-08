import { HTMLSelect } from "@blueprintjs/core";
import { SettingsField as Field } from "../SettingsField";
import { useShellLanguage } from "../shellLanguage";
import { useChangesPreferences } from "./changesPreferences";
import { changesViewLabel, changesViews } from "./projectChanges";

/** The setting of what a Changes tab shows beside the files: one file at a time, or all of them in one view. The tab has the same choice. */
export function ChangesViewSetting() {
  const { t } = useShellLanguage();
  const [preferences, update] = useChangesPreferences();
  return <Field label={t("Changes are shown")} htmlFor="settings-changes-view">
    <HTMLSelect id="settings-changes-view" value={preferences.view} onChange={event => update({ view: event.target.value === "all" ? "all" : "file" })}>
      {changesViews.map(view => <option key={view} value={view}>{t(changesViewLabel(view))}</option>)}
    </HTMLSelect>
  </Field>;
}
