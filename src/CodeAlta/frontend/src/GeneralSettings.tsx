import { Button, ButtonGroup, Card, Checkbox, HTMLSelect } from "@blueprintjs/core";
import type { ProjectSort } from "./projectRail";
import type { Theme, PreferenceNotices } from "./windowPreferences";
import { useShellLanguage } from "./shellLanguage";
import { locales, languageNames, preferenceNotice } from "./localization";

export function GeneralSettings({ theme, setTheme, sort, setSort, desktopCollapsed, setDesktopCollapsed, notices, recentSessionCount, setRecentSessionCount }: {
  theme: Theme;
  setTheme: (value: Theme) => void;
  sort: ProjectSort;
  setSort: (value: ProjectSort) => void;
  desktopCollapsed: boolean;
  setDesktopCollapsed: (value: boolean) => void;
  recentSessionCount: number;
  setRecentSessionCount: (value: number) => void;
  notices: PreferenceNotices;
}) {
  const { t, locale, choice, issue, setLanguage } = useShellLanguage();
  return <Card className="appearance-settings" aria-labelledby="general-settings-title">
    <h2 id="general-settings-title">{t("Appearance & navigator")}</h2>
    <label htmlFor="settings-language">{t("Language")}</label>
    <HTMLSelect id="settings-language" value={choice} onChange={event => setLanguage(event.target.value)}>
      <option value="auto">{t("Auto")}</option>{locales.map(value => <option key={value} value={value} lang={value}>{languageNames[value]}</option>)}
    </HTMLSelect>
    {issue && <p role="status" className="notice" data-diagnostic={issue}>{preferenceNotice(locale, "Language", "English", issue)}</p>}
    <fieldset><legend>{t("Theme")}</legend><ButtonGroup>
      <Button active={theme === "dark"} aria-pressed={theme === "dark"} onClick={() => setTheme("dark")}>{t("Dark")}</Button>
      <Button active={theme === "light"} aria-pressed={theme === "light"} onClick={() => setTheme("light")}>{t("Light")}</Button>
    </ButtonGroup></fieldset>
    {notices.theme && <p role="status" className="notice" data-diagnostic={notices.theme}>{preferenceNotice(locale, "Theme", locale === "en" ? "dark" : t("Dark"), notices.theme)}</p>}
    <label htmlFor="settings-project-sort">{t("Sort projects")}</label>
    <HTMLSelect id="settings-project-sort" value={sort} onChange={event => setSort(event.target.value as ProjectSort)}>
      <option value="name">{t("Name")}</option><option value="recent">{t("Recent visible updates")}</option>
    </HTMLSelect>
    {notices.sort && <p role="status" className="notice" data-diagnostic={notices.sort}>{preferenceNotice(locale, "Project sort", locale === "en" ? "name" : t("Name"), notices.sort)}</p>}
    <label htmlFor="settings-recent-count">{t("Recent session display count (1–50)")}</label>
    <HTMLSelect id="settings-recent-count" value={recentSessionCount} onChange={event => setRecentSessionCount(Number(event.target.value))}>
      {Array.from({ length: 50 }, (_, i) => <option key={i + 1} value={i + 1}>{i + 1}</option>)}
    </HTMLSelect>
    <output>{t(recentSessionCount === 1 ? "{count} session" : "{count} sessions", { count: recentSessionCount })}</output>
    {notices.recent && <p role="status" className="notice" data-diagnostic={notices.recent}>{preferenceNotice(locale, "Recent session count", "20", notices.recent)}</p>}
    <Checkbox checked={desktopCollapsed} onChange={event => setDesktopCollapsed(event.currentTarget.checked)} label={t("Collapse desktop project rail")} />
    {notices.rail && <p role="status" className="notice" data-diagnostic={notices.rail}>{preferenceNotice(locale, "Desktop projects", locale === "en" ? "expanded" : t("Show projects"), notices.rail)}</p>}
  </Card>;
}
