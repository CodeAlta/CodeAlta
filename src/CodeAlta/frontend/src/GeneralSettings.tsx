import type { ReactNode } from "react";
import { Button, ButtonGroup, Card, HTMLSelect, Switch } from "@blueprintjs/core";
import { AppIcon, type IconName } from "./AppIcon";
import type { ProjectSort } from "./projectRail";
import { colorSchemes } from "./colorSchemes";
import { themeLabel, themes, type EffectiveTheme, type Theme, type PreferenceNotices } from "./windowPreferences";
import { useShellLanguage } from "./shellLanguage";
import { locales, languageNames, preferenceNotice } from "./localization";
import { keepRunningPlace } from "./desktopShell";

/** The icon of each theme choice, shared with the title-bar switch. */
export const themeIcons: Readonly<Record<Theme, IconName>> = { dark: "themeDark", light: "themeLight", system: "themeSystem" };

// One preference per row: its name on the left, its control on the right, and any storage notice below.
function Field({ label, htmlFor, notice, children }: { label: string; htmlFor?: string; notice?: ReactNode; children: ReactNode }) {
  return <div className="settings-field">
    {htmlFor ? <label htmlFor={htmlFor}>{label}</label> : <span className="settings-field-label">{label}</span>}
    <div className="settings-field-control">{children}</div>
    {notice}
  </div>;
}

export function GeneralSettings({ theme, setTheme, shownTheme, colorScheme, setColorScheme, sort, setSort, desktopCollapsed, setDesktopCollapsed, notices, recentSessionCount, setRecentSessionCount, keepRunning }: {
  theme: Theme;
  setTheme: (value: Theme) => void;
  /** The theme on screen, which decides the variant of each scheme that is previewed. */
  shownTheme: EffectiveTheme;
  colorScheme: string;
  setColorScheme: (value: string) => void;
  sort: ProjectSort;
  setSort: (value: ProjectSort) => void;
  desktopCollapsed: boolean;
  setDesktopCollapsed: (value: boolean) => void;
  recentSessionCount: number;
  setRecentSessionCount: (value: number) => void;
  /** Whether closing the window leaves the application running; absent where it cannot stay anywhere. */
  keepRunning?: { enabled: boolean; platform: string; set: (value: boolean) => void } | null;
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
    <Field label={t("Color scheme")}
      notice={notices.scheme && <p role="status" className="notice" data-diagnostic={notices.scheme}>{preferenceNotice(locale, "Color scheme", "Blueprint", notices.scheme)}</p>}>
      <div className="color-scheme-grid" role="radiogroup" aria-label={t("Color scheme")}>
        {colorSchemes.map(scheme => {
          const swatch = scheme[shownTheme];
          return <Button key={scheme.id} role="radio" aria-checked={colorScheme === scheme.id} active={colorScheme === scheme.id} alignText="start"
            icon={<span className="color-scheme-swatch" aria-hidden="true" style={{ background: swatch.background, borderColor: swatch.tint }}>
              <i style={{ background: swatch.foreground }} /><i style={{ background: swatch.tint }} /><i style={{ background: swatch.accent }} /></span>}
            onClick={() => setColorScheme(scheme.id)}>{scheme.name}</Button>;
        })}
      </div>
    </Field>
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
    {keepRunning && <Field label={t("Keep running when the window is closed")} htmlFor="settings-keep-running"
      notice={<p className="settings-field-help">{t(keepRunningPlace(keepRunning.platform))}</p>}>
      <Switch id="settings-keep-running" className="settings-checkbox" checked={keepRunning.enabled} onChange={event => keepRunning.set(event.currentTarget.checked)} />
    </Field>}
  </Card>;
}
