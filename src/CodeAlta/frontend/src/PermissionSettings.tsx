import { Card, HTMLSelect } from "@blueprintjs/core";
import { askPermissionMode, bypassPermissionMode, permissionModeDescription, permissionModeName } from "./permissionModes";
import { SettingsField as Field } from "./SettingsField";
import { useShellLanguage } from "./shellLanguage";

/**
 * Settings page for the permissions of the sessions: the mode a session runs in when neither it nor its provider has
 * one of its own. The choice is kept by the host and applies to the next Send of each session.
 */
export function PermissionSettings({ permissions }: {
  /** Whether the sessions are asked first, and what changes it; null where the window does not own the host. */
  permissions: { review: boolean; set: (value: boolean) => void } | null;
}) {
  const { t } = useShellLanguage();
  const mode = permissions?.review ? askPermissionMode : bypassPermissionMode;
  return <div className="configuration-page settings-card-page" aria-label={t("Permissions")}>
    <header className="page-heading"><span className="eyebrow">{t("Agent & models")}</span><h1>{t("Permissions")}</h1>
      <p>{t("What the sessions do without asking you.")}</p></header>
    <div className="settings-grid"><Card className="appearance-settings">
      <Field label={t("Default mode")} htmlFor="settings-permission-mode"
        notice={<p className="settings-field-help">{t(permissionModeDescription(mode)!)} {t("A session can have a mode of its own.")}</p>}>
        <HTMLSelect id="settings-permission-mode" value={mode} disabled={!permissions}
          onChange={event => permissions?.set(event.target.value === askPermissionMode)}>
          {[bypassPermissionMode, askPermissionMode].map(value => <option key={value} value={value}>{t(permissionModeName(value)!)}</option>)}
        </HTMLSelect>
      </Field>
    </Card></div>
  </div>;
}
