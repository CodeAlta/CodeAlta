import { Card, HTMLSelect, Switch } from "@blueprintjs/core";
import type { globalConfig } from "#neoastra";
import { builtInToolNames, DisabledToolsField } from "./DisabledToolsField";
import { askPermissionMode, bypassPermissionMode, permissionModeDescription, permissionModeName } from "./permissionModes";
import { SettingsField as Field } from "./SettingsField";
import { useSettingsEditor } from "./SettingsPage";
import { useShellLanguage } from "./shellLanguage";
import type { Confirmation } from "./windowPreferences";

/**
 * Settings page for the permissions of the sessions: the mode a session runs in when neither it nor its provider has
 * one of its own, and what a session an agent creates is given. The choices are kept by the host: the first applies to
 * the next Send of each session, the second to the next session an agent creates.
 */
export function PermissionSettings({ permissions, confirms, tools = null }: {
  /**
   * The host the tools every session does without are read from and written to; null where the window has none. The
   * page is given the service: it is mounted alone in tests, without the client of the host.
   */
  tools?: Readonly<{ epoch: string; api: Pick<typeof globalConfig, "disabledTools" | "saveDisabledTools"> }> | null;
  /**
   * Whether the sessions are asked first, whether a session an agent creates takes the mode of its creator, and what
   * changes them; null where the window does not own the host.
   */
  permissions: { review: boolean; set: (value: boolean) => void; inherit: boolean; setInherit: (value: boolean) => void } | null;
  /** Local manual-action questions remain available without host-backed agent permissions. */
  confirms: Readonly<Record<Confirmation, boolean>> & { set: (confirmation: Confirmation, ask: boolean) => void };
}) {
  const { t } = useShellLanguage();
  const mode = permissions?.review ? askPermissionMode : bypassPermissionMode;
  const created = permissions?.inherit ? "creator" : bypassPermissionMode;
  return <div className="configuration-page settings-card-page" aria-label={t("Permissions")}>
    <header className="page-heading"><span className="eyebrow">{t("Agent & models")}</span><h1>{t("Permissions")}</h1>
      <p>{t("What the sessions do without asking you.")}</p></header>
    <div className="settings-grid"><Card className="appearance-settings" aria-labelledby="settings-agent-permissions-title">
      <h2 id="settings-agent-permissions-title">{t("Agent permissions")}</h2>
      <Field label={t("Default mode")} htmlFor="settings-permission-mode"
        notice={<p className="settings-field-help">{t(permissionModeDescription(mode)!)} {t("A session can have a mode of its own.")}</p>}>
        <HTMLSelect id="settings-permission-mode" value={mode} disabled={!permissions}
          onChange={event => permissions?.set(event.target.value === askPermissionMode)}>
          {[bypassPermissionMode, askPermissionMode].map(value => <option key={value} value={value}>{t(permissionModeName(value)!)}</option>)}
        </HTMLSelect>
      </Field>
      <Field label={t("Sessions created by agents")} htmlFor="settings-permission-created"
        notice={<p className="settings-field-help">{t(permissions?.inherit
          ? "A session an agent creates asks what the session that creates it asks."
          : "A session an agent creates does not ask you.")}</p>}>
        <HTMLSelect id="settings-permission-created" value={created} disabled={!permissions}
          onChange={event => permissions?.setInherit(event.target.value === "creator")}>
          <option value={bypassPermissionMode}>{t(permissionModeName(bypassPermissionMode)!)}</option>
          <option value="creator">{t("Same as the session that creates them")}</option>
        </HTMLSelect>
      </Field>
    </Card>
    {tools && <ToolSettings epoch={tools.epoch} api={tools.api} />}
    <Card className="appearance-settings" aria-labelledby="settings-confirmations-title">
      <h2 id="settings-confirmations-title">{t("Confirmations")}</h2>
      <p className="settings-field-help">{t("These confirmations apply to manual actions, not automatic session retention.")}</p>
      <Field label={t("Ask before deleting a session")} htmlFor="settings-confirm-session-delete">
        <Switch id="settings-confirm-session-delete" className="settings-checkbox" checked={confirms.sessionDelete} onChange={event => confirms.set("sessionDelete", event.currentTarget.checked)} />
      </Field>
      <Field label={t("Ask before archiving a project")} htmlFor="settings-confirm-project-archive">
        <Switch id="settings-confirm-project-archive" className="settings-checkbox" checked={confirms.projectArchive} onChange={event => confirms.set("projectArchive", event.currentTarget.checked)} />
      </Field>
      <Field label={t("Ask before stopping a plugin whose agents are at work")} htmlFor="settings-confirm-plugin-stop">
        <Switch id="settings-confirm-plugin-stop" className="settings-checkbox" checked={confirms.pluginStop} onChange={event => confirms.set("pluginStop", event.currentTarget.checked)} />
      </Field>
    </Card></div>
  </div>;
}

/** The tools every session does without (`[tools] disabled` of the settings of the user), saved at once. */
function ToolSettings({ epoch, api }: { epoch: string; api: Pick<typeof globalConfig, "disabledTools" | "saveDisabledTools"> }) {
  const { t } = useShellLanguage();
  const { listing, busy, notice, mutate } = useSettingsEditor(options => api.disabledTools({ expectedEpoch: epoch }, options), epoch);
  return <Card className="appearance-settings" aria-labelledby="settings-tools-title">
    <h2 id="settings-tools-title">{t("Tools")}</h2>
    <Field label={t("Tools turned off")}
      notice={<p className="settings-field-help" role={notice?.intent === "danger" ? "alert" : undefined}>
        {notice && notice.intent !== "success" ? t(notice.key) : t("Every session does without them, whatever its space and its agent.")}</p>}>
      <DisabledToolsField tools={listing?.tools ?? []} known={listing?.known ?? builtInToolNames} disabled={!listing || busy} label={t("Tools turned off")}
        onChange={tools => void mutate(() => api.saveDisabledTools({ expectedEpoch: epoch, tools }, { timeoutMilliseconds: 30_000 }))} />
    </Field>
  </Card>;
}
