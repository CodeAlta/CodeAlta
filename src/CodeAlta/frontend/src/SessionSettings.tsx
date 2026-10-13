import { Card, HTMLSelect } from "@blueprintjs/core";
import { globalConfig } from "#neoastra";
import { SettingsField as Field } from "./SettingsField";
import { SettingsFileLocations } from "./SettingsFileLocation";
import { useSettingsFiles, type SettingsFilesApi } from "./settingsFiles";
import { SettingsPage, SettingsUnavailable, useSettingsEditor } from "./SettingsPage";
import { sessionTitleChoice, sessionTitleModes } from "./sessionTitles";
import { useShellLanguage } from "./shellLanguage";

/**
 * Settings page for the sessions: how a session nobody named is titled. The choice is saved at once, in the
 * configuration file of the user, and a session takes it with its next prompt.
 */
export function SessionSettings({ epoch, api = globalConfig, onOpenFile, filesApi }: {
  epoch: string | null;
  api?: Pick<typeof globalConfig, "sessionTitles" | "saveSessionTitles">;
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
    description="How CodeAlta titles your sessions." notice={notice} loading={loading} busy={busy} onReload={reload}>
    {!listing ? <SettingsUnavailable loading={loading} icon="chat" title="Session settings unavailable" /> : <>
      <SettingsFileLocations files={files} disabled={busy} />
      <div className="settings-grid"><Card className="appearance-settings" aria-labelledby="settings-session-titles-title">
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
      </Card></div>
    </>}
  </SettingsPage>;
}
