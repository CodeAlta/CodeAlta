import { Card, HTMLSelect, Switch } from "@blueprintjs/core";
import { closeBehavior, closeBehaviorLabel, closeBehaviors, keepRunningPlace, type CloseBehavior } from "./desktopShell";
import { LandingSettings } from "./landing/LandingSettings";
import { locales, languageNames, preferenceNotice } from "./localization";
import { SettingsField as Field } from "./SettingsField";
import { useShellLanguage } from "./shellLanguage";

/** Window behavior preferences; their existing stores and shell callbacks remain owned by the window. */
export function GeneralSettings({ remoteControl, closing }: {
  /** Whether the sessions that had Remote Control on have it turned on again when CodeAlta starts. */
  remoteControl?: { reconnect: boolean; set: (value: boolean) => void } | null;
  /** What closing the window does; absent where the application cannot stay anywhere without it. */
  closing?: { behavior: CloseBehavior; platform: string; trayIcon?: boolean; set: (value: CloseBehavior) => void } | null;
}) {
  const { t, locale, choice, issue, setLanguage } = useShellLanguage();
  return <div className="configuration-page settings-card-page" aria-label={t("General")}>
    <header className="page-heading"><span className="eyebrow">{t("Personalization")}</span><h1>{t("General")}</h1></header>
    <div className="settings-grid">
      <Card className="appearance-settings" aria-labelledby="settings-language-title">
        <h2 id="settings-language-title">{t("Language")}</h2>
        <Field label={t("Language")} htmlFor="settings-language"
          notice={issue && <p role="status" className="notice" data-diagnostic={issue}>{preferenceNotice(locale, "Language", "English", issue)}</p>}>
          <HTMLSelect id="settings-language" value={choice} onChange={event => setLanguage(event.target.value)}>
            <option value="auto">{t("Auto")}</option>{locales.map(value => <option key={value} value={value} lang={value}>{languageNames[value]}</option>)}
          </HTMLSelect>
        </Field>
      </Card>
      <Card className="appearance-settings" aria-labelledby="settings-welcome-title">
        <h2 id="settings-welcome-title">{t("Welcome page")}</h2>
        <LandingSettings />
      </Card>
      {(remoteControl || closing) && <Card className="appearance-settings" aria-labelledby="settings-startup-title">
        <h2 id="settings-startup-title">{t("Startup and closing")}</h2>
        {remoteControl && <Field label={t("Turn Remote Control back on when CodeAlta starts")} htmlFor="settings-reconnect-remote-control"
          notice={<p className="settings-field-help">{t(remoteControl.reconnect
            ? "Sessions that had Remote Control on when CodeAlta exited are connected again."
            : "Remote Control is turned on by hand, for each session.")}</p>}>
          <Switch id="settings-reconnect-remote-control" className="settings-checkbox" checked={remoteControl.reconnect}
            onChange={event => remoteControl.set(event.currentTarget.checked)} />
        </Field>}
        {closing && <Field label={t("When the window is closed")} htmlFor="settings-on-close"
          notice={closing.behavior === "keep" && <p className="settings-field-help">{t(keepRunningPlace(closing.platform, closing.trayIcon))}</p>}>
          <HTMLSelect id="settings-on-close" value={closing.behavior} onChange={event => closing.set(closeBehavior(event.target.value))}>
            {closeBehaviors.map(behavior => <option key={behavior} value={behavior}>{t(closeBehaviorLabel(behavior))}</option>)}
          </HTMLSelect>
        </Field>}
      </Card>}
    </div>
  </div>;
}
