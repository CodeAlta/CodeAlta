import { Switch } from "@blueprintjs/core";
import { SettingsField as Field } from "../SettingsField";
import { useShellLanguage } from "../shellLanguage";
import { landingPreferences, useLandingPreferences, type LandingPreferenceStore } from "./landingPreferences";

/** The settings of the welcome page: whether it opens when the application starts, and whether its accent moves. The page has the same two switches. */
export function LandingSettings({ preferences = landingPreferences }: { preferences?: LandingPreferenceStore }) {
  const { t } = useShellLanguage();
  const chosen = useLandingPreferences(preferences);
  return <>
    <Field label={t("Show the welcome page at startup")} htmlFor="settings-landing-startup">
      <Switch id="settings-landing-startup" className="settings-checkbox" checked={chosen.openAtStartup} onChange={event => preferences.set("openAtStartup", event.currentTarget.checked)} />
    </Field>
    <Field label={t("Animate the welcome page")} htmlFor="settings-landing-animation">
      <Switch id="settings-landing-animation" className="settings-checkbox" checked={chosen.animate} onChange={event => preferences.set("animate", event.currentTarget.checked)} />
    </Field>
  </>;
}
