import type { ComponentProps } from "react";
import { Button, ButtonGroup, Card, HTMLSelect, Switch } from "@blueprintjs/core";
import { AppIcon, type IconName } from "./AppIcon";
import { ColorSchemeSettings } from "./ColorSchemeSettings";
import type { ProjectSort } from "./explorer/projectRail";
import { SettingsField as Field } from "./SettingsField";
import { themeLabel, themes, type Theme, type PreferenceNotices } from "./windowPreferences";
import { useShellLanguage } from "./shellLanguage";
import { locales, languageNames, preferenceNotice } from "./localization";
import { closeBehavior, closeBehaviorLabel, closeBehaviors, keepRunningPlace, type CloseBehavior } from "./desktopShell";

/** The icon of each theme choice, shared with the title-bar switch. */
export const themeIcons: Readonly<Record<Theme, IconName>> = { dark: "themeDark", light: "themeLight", system: "themeSystem" };

export function GeneralSettings({ theme, setTheme, darker, setDarker, schemes, sort, setSort, desktopCollapsed, setDesktopCollapsed, notices, recentSessionCount, setRecentSessionCount, closing }: {
  theme: Theme;
  setTheme: (value: Theme) => void;
  /** Whether the dark theme is the darker one. */
  darker: boolean;
  setDarker: (value: boolean) => void;
  /** The color scheme: the selection, the user's own schemes and what edits them. */
  schemes: Omit<ComponentProps<typeof ColorSchemeSettings>, "notice">;
  sort: ProjectSort;
  setSort: (value: ProjectSort) => void;
  desktopCollapsed: boolean;
  setDesktopCollapsed: (value: boolean) => void;
  recentSessionCount: number;
  setRecentSessionCount: (value: number) => void;
  /** What closing the window does; absent where the application cannot stay anywhere without it. */
  closing?: { behavior: CloseBehavior; platform: string; set: (value: CloseBehavior) => void } | null;
  notices: PreferenceNotices;
}) {
  const { t, locale, choice, issue, setLanguage } = useShellLanguage();
  return <Card className="appearance-settings" aria-labelledby="general-settings-title">
    <h2 id="general-settings-title">{t("Appearance & navigator")}</h2>
    <Field label={t("Language")} htmlFor="settings-language"
      notice={issue && <p role="status" className="notice" data-diagnostic={issue}>{preferenceNotice(locale, "Language", "English", issue)}</p>}>
      <HTMLSelect id="settings-language" value={choice} onChange={event => setLanguage(event.target.value)}>
        <option value="auto">{t("Auto")}</option>{locales.map(value => <option key={value} value={value} lang={value}>{languageNames[value]}</option>)}
      </HTMLSelect>
    </Field>
    <Field label={t("Theme")}
      notice={notices.theme && <p role="status" className="notice" data-diagnostic={notices.theme}>{preferenceNotice(locale, "Theme", locale === "en" ? "dark" : t("Dark"), notices.theme)}</p>}>
      <ButtonGroup role="group" aria-label={t("Theme")}>
        {themes.map(value => <Button key={value} active={theme === value} aria-pressed={theme === value}
          icon={<AppIcon name={themeIcons[value]} size={15} />} onClick={() => setTheme(value)}>{t(themeLabel(value))}</Button>)}
      </ButtonGroup>
    </Field>
    <Field label={t("Darker dark theme")} htmlFor="settings-darker">
      <Switch id="settings-darker" className="settings-checkbox" checked={darker} disabled={theme === "light"} onChange={event => setDarker(event.currentTarget.checked)} />
    </Field>
    <ColorSchemeSettings {...schemes}
      notice={notices.scheme && <p role="status" className="notice" data-diagnostic={notices.scheme}>{preferenceNotice(locale, "Color scheme", "Blueprint", notices.scheme)}</p>} />
    <Field label={t("Sort projects")} htmlFor="settings-project-sort"
      notice={notices.sort && <p role="status" className="notice" data-diagnostic={notices.sort}>{preferenceNotice(locale, "Project sort", locale === "en" ? "name" : t("Name"), notices.sort)}</p>}>
      <HTMLSelect id="settings-project-sort" value={sort} onChange={event => setSort(event.target.value as ProjectSort)}>
        <option value="name">{t("Name")}</option><option value="recent">{t("Recent visible updates")}</option>
      </HTMLSelect>
    </Field>
    <Field label={t("Recent session display count (1–50)")} htmlFor="settings-recent-count"
      notice={notices.recent && <p role="status" className="notice" data-diagnostic={notices.recent}>{preferenceNotice(locale, "Recent session count", "20", notices.recent)}</p>}>
      <HTMLSelect id="settings-recent-count" value={recentSessionCount} onChange={event => setRecentSessionCount(Number(event.target.value))}>
        {Array.from({ length: 50 }, (_, i) => <option key={i + 1} value={i + 1}>{i + 1}</option>)}
      </HTMLSelect>
    </Field>
    <Field label={t("Collapse desktop project rail")} htmlFor="settings-rail-collapsed"
      notice={notices.rail && <p role="status" className="notice" data-diagnostic={notices.rail}>{preferenceNotice(locale, "Desktop projects", locale === "en" ? "expanded" : t("Show projects"), notices.rail)}</p>}>
      <Switch id="settings-rail-collapsed" className="settings-checkbox" checked={desktopCollapsed} onChange={event => setDesktopCollapsed(event.currentTarget.checked)} />
    </Field>
    {closing && <Field label={t("When the window is closed")} htmlFor="settings-on-close"
      notice={closing.behavior === "keep" && <p className="settings-field-help">{t(keepRunningPlace(closing.platform))}</p>}>
      <HTMLSelect id="settings-on-close" value={closing.behavior} onChange={event => closing.set(closeBehavior(event.target.value))}>
        {closeBehaviors.map(behavior => <option key={behavior} value={behavior}>{t(closeBehaviorLabel(behavior))}</option>)}
      </HTMLSelect>
    </Field>}
  </Card>;
}
