import { useState, type ComponentProps } from "react";
import { Button, ButtonGroup, Card, HTMLSelect, Slider, Switch } from "@blueprintjs/core";
import { AppIcon, type IconName } from "./AppIcon";
import { useElementSize } from "./AppWindow";
import { ChangesViewSetting } from "./changes/ChangesViewSetting";
import { ColorSchemeSettings } from "./ColorSchemeSettings";
import { LandingSettings } from "./landing/LandingSettings";
import type { ProjectSort } from "./explorer/projectRail";
import { SettingsField as Field } from "./SettingsField";
import { themeLabel, themes, type Confirmation, type Theme, type PreferenceNotices } from "./windowPreferences";
import { clampSessionWidth, defaultSessionWidth, minimumSessionWidth, sessionWidthStep } from "./sessionWidth";
import { useShellLanguage } from "./shellLanguage";
import { locales, languageNames, preferenceNotice } from "./localization";
import { defaultRecentSessionCount, defaultSubAgentCount } from "./recentSessions";
import { closeBehavior, closeBehaviorLabel, closeBehaviors, keepRunningPlace, type CloseBehavior } from "./desktopShell";
/** The icon of each theme choice, shared with the title-bar switch. */
export const themeIcons: Readonly<Record<Theme, IconName>> = { dark: "themeDark", light: "themeLight", system: "themeSystem" };

/**
 * The slider of the width of the conversation. Blueprint's slider measures its track once, when it is mounted, and
 * the pages of Settings are mounted before their dialog is shown, when nothing has a size: every press was then
 * read as the smallest or the largest width. The slider is made again when the room it has changes, so it always
 * reads a press on the track it is shown with.
 */
function SessionWidthSlider({ value, label, onChange }: { value: number; label: string; onChange: (value: number) => void }) {
  const [room, setRoom] = useState<HTMLDivElement | null>(null);
  const { width } = useElementSize(room);
  return <div ref={setRoom} className="settings-session-slider">
    <Slider key={width} min={minimumSessionWidth} max={defaultSessionWidth} stepSize={sessionWidthStep} labelRenderer={false}
      value={clampSessionWidth(value)} onChange={next => { if (next !== value) onChange(next); }} handleHtmlProps={{ "aria-label": label }} />
  </div>;
}

export function GeneralSettings({ theme, setTheme, darker, setDarker, schemes, sort, setSort, desktopCollapsed, setDesktopCollapsed, notices, recentSessionCount, setRecentSessionCount, subAgentCount, setSubAgentCount, sessionWidth, setSessionWidth, closing, confirms }: {
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
  /** How many sub-agents the Explorer lists under a session. */
  subAgentCount: number;
  setSubAgentCount: (value: number) => void;
  /** How much of the space of a session its timeline and its prompt take, in percent; absent where it cannot be set. */
  sessionWidth?: number;
  setSessionWidth?: (value: number) => void;
  /** What closing the window does; absent where the application cannot stay anywhere without it. */
  closing?: { behavior: CloseBehavior; platform: string; trayIcon?: boolean; set: (value: CloseBehavior) => void } | null;
  /** What asks before it is done: the answer "do not ask again" of a question is taken back here. */
  confirms?: Readonly<Record<Confirmation, boolean>> & { set: (confirmation: Confirmation, ask: boolean) => void };
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
      notice={notices.recent && <p role="status" className="notice" data-diagnostic={notices.recent}>{preferenceNotice(locale, "Recent session count", String(defaultRecentSessionCount), notices.recent)}</p>}>
      <HTMLSelect id="settings-recent-count" value={recentSessionCount} onChange={event => setRecentSessionCount(Number(event.target.value))}>
        {Array.from({ length: 50 }, (_, i) => <option key={i + 1} value={i + 1}>{i + 1}</option>)}
      </HTMLSelect>
    </Field>
    <Field label={t("Sub-agent display count (1–50)")} htmlFor="settings-sub-agent-count"
      notice={notices.subAgents && <p role="status" className="notice" data-diagnostic={notices.subAgents}>{preferenceNotice(locale, "Sub-agent count", String(defaultSubAgentCount), notices.subAgents)}</p>}>
      <HTMLSelect id="settings-sub-agent-count" value={subAgentCount} onChange={event => setSubAgentCount(Number(event.target.value))}>
        {Array.from({ length: 50 }, (_, i) => <option key={i + 1} value={i + 1}>{i + 1}</option>)}
      </HTMLSelect>
    </Field>
    {sessionWidth !== undefined && setSessionWidth && <Field label={t("Width of the conversation")}>
      <div className="settings-session-width">
        <SessionWidthSlider value={sessionWidth} label={t("Width of the conversation")} onChange={setSessionWidth} />
        <output>{sessionWidth}%</output>
        <Button variant="minimal" size="small" disabled={sessionWidth === defaultSessionWidth} onClick={() => setSessionWidth(defaultSessionWidth)}>{t("Reset")}</Button>
      </div>
    </Field>}
    <ChangesViewSetting />
    <Field label={t("Collapse desktop project rail")} htmlFor="settings-rail-collapsed"
      notice={notices.rail && <p role="status" className="notice" data-diagnostic={notices.rail}>{preferenceNotice(locale, "Desktop projects", locale === "en" ? "expanded" : t("Show projects"), notices.rail)}</p>}>
      <Switch id="settings-rail-collapsed" className="settings-checkbox" checked={desktopCollapsed} onChange={event => setDesktopCollapsed(event.currentTarget.checked)} />
    </Field>
    {confirms && <>
      <Field label={t("Ask before deleting a session")} htmlFor="settings-confirm-session-delete">
        <Switch id="settings-confirm-session-delete" className="settings-checkbox" checked={confirms.sessionDelete} onChange={event => confirms.set("sessionDelete", event.currentTarget.checked)} />
      </Field>
      <Field label={t("Ask before archiving a project")} htmlFor="settings-confirm-project-archive">
        <Switch id="settings-confirm-project-archive" className="settings-checkbox" checked={confirms.projectArchive} onChange={event => confirms.set("projectArchive", event.currentTarget.checked)} />
      </Field>
    </>}
    <LandingSettings />
    {closing && <Field label={t("When the window is closed")} htmlFor="settings-on-close"
      notice={closing.behavior === "keep" && <p className="settings-field-help">{t(keepRunningPlace(closing.platform, closing.trayIcon))}</p>}>
      <HTMLSelect id="settings-on-close" value={closing.behavior} onChange={event => closing.set(closeBehavior(event.target.value))}>
        {closeBehaviors.map(behavior => <option key={behavior} value={behavior}>{t(closeBehaviorLabel(behavior))}</option>)}
      </HTMLSelect>
    </Field>}
  </Card>;
}
