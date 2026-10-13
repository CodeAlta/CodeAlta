import { Card, HTMLSelect, Switch } from "@blueprintjs/core";
import { globalConfig } from "#neoastra";
import { SettingsField as Field } from "./SettingsField";
import { SettingsFileLocations } from "./SettingsFileLocation";
import { useSettingsFiles, type SettingsFilesApi } from "./settingsFiles";
import { SettingsPage, SettingsUnavailable, useSettingsEditor } from "./SettingsPage";
import { sessionTitleChoice, sessionTitleModes } from "./sessionTitles";
import { useShellLanguage } from "./shellLanguage";

/**
 * Settings page for the sessions: how a session nobody named is titled, and whether the sessions share a single tab.
 * The title choice is saved at once, in the configuration file of the user, and a session takes it with its next
 * prompt. The single tab is a preference of the window, which keeps it.
 */
export function SessionSettings({ epoch, api = globalConfig, onOpenFile, filesApi, tabs }: {
  epoch: string | null;
  api?: Pick<typeof globalConfig, "sessionTitles" | "saveSessionTitles">;
  /** Whether the sessions share a single tab, and what changes it; absent where the window has no tabs to set. */
  tabs?: { single: boolean; setSingle: (value: boolean) => void };
  /** Called once the code editor was asked to show the configuration file: the window leaves Settings. */
  onOpenFile?: () => void;
  /** Says where the configuration file is and opens it; the host by default. */
  filesApi?: SettingsFilesApi;
}) {
  const { t } = useShellLanguage();
  const { listing, loading, busy, notice, setNotice, reload, mutate } = useSettingsEditor(options => api.sessionTitles({ expectedEpoch: epoch }, options), `${epoch}`);
  // The choice is kept in the configuration file of the user: the page says where that file is.
  const files = useSettingsFiles({ page: "config", epoch, projectId: null, revision: listing, onOpened: onOpenFile, setNotice, api: filesApi });
  const choice = sessionTitleChoice(listing?.mode);

  return <SettingsPage className="session-settings" label={t("Sessions")} group="Agent & models" title="Sessions"
    description="How CodeAlta titles your sessions and shows them in tabs." notice={notice} loading={loading} busy={busy} onReload={reload}>
    {/* The tabs are the window's own: they are set also where the configuration file cannot be read. */}
    {!listing && (loading || !tabs) ? <SettingsUnavailable loading={loading} icon="chat" title="Session settings unavailable" /> : <>
      {listing && <SettingsFileLocations files={files} disabled={busy} />}
      <div className="settings-grid">{listing && <Card className="appearance-settings" aria-labelledby="settings-session-titles-title">
        <h2 id="settings-session-titles-title">{t("Session titles")}</h2>
        <Field label={t("Title of a session you did not name")} htmlFor="settings-session-title-mode"
          notice={<p className="settings-field-help">{choice && <>{t(choice.help)} </>}{t("A title you give stays.")}</p>}>
          <HTMLSelect id="settings-session-title-mode" value={choice?.value ?? ""} disabled={busy}
            onChange={event => {
              const mode = event.target.value;
              void mutate(() => api.saveSessionTitles({ expectedEpoch: epoch, mode }, { timeoutMilliseconds: 30_000 }), "Saved.");
            }}>
            {!choice && <option value="" disabled>{listing.mode}</option>}
            {sessionTitleModes.map(mode => <option key={mode.value} value={mode.value}>{t(mode.label)}</option>)}
          </HTMLSelect>
        </Field>
      </Card>}
      {tabs && <Card className="appearance-settings" aria-labelledby="settings-session-tabs-title">
        <h2 id="settings-session-tabs-title">{t("Session tabs")}</h2>
        <Field label={t("Single tab for sessions")} htmlFor="settings-single-session-tab"
          notice={<p className="settings-field-help">{t("A session you open takes the place of the one shown.")}</p>}>
          <Switch id="settings-single-session-tab" className="settings-checkbox" checked={tabs.single} onChange={event => tabs.setSingle(event.currentTarget.checked)} />
        </Field>
      </Card>}</div>
    </>}
  </SettingsPage>;
}
