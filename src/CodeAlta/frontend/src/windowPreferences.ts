import { useEffect, useMemo, useRef, useState } from "react";
import { colorSchemeIds, colorSchemeOf, colorSchemeStorageKey, defaultColorScheme, schemePalette, type ColorVariant, type ShownAppearance } from "./colorSchemes";
import type { PreferenceIssue } from "./localization";
import { readRecentSessionCount, recentSessionCountKey, validRecentSessionCount } from "./recentSessions";
import { persistProjectSort, projectSortStorageKey, type ProjectSort } from "./explorer/projectRail";
import { persistProjectRailCollapsed, projectRailVisibilityKey, resetNarrowRail, toggleProjectRail, type ProjectRailState } from "./explorer/projectRailVisibility";

/** The theme the user picked; "system" follows the operating system. */
export type Theme = "dark" | "light" | "system";
/** The theme actually shown. */
export type EffectiveTheme = "dark" | "light";
export const themes: readonly Theme[] = ["dark", "light", "system"];

/** The theme after the one given, in the order the title-bar switch cycles through. */
export function nextTheme(theme: Theme): Theme { return themes[(themes.indexOf(theme) + 1) % themes.length]; }

/** The name of a theme choice, as a translation key. */
export function themeLabel(theme: Theme): "Dark" | "Light" | "Auto" { return theme === "dark" ? "Dark" : theme === "light" ? "Light" : "Auto"; }

/** Resolves a theme choice against what the operating system prefers. */
export function effectiveTheme(theme: Theme, systemDark: boolean): EffectiveTheme {
  return theme === "system" ? systemDark ? "dark" : "light" : theme;
}

/** The palette variant of a theme: the dark theme has a darker one, the light theme only its own. */
export function colorVariant(theme: EffectiveTheme, darker: boolean): ColorVariant {
  return theme === "light" ? "light" : darker ? "darker" : "dark";
}

const systemDarkQuery = "(prefers-color-scheme: dark)";
function useSystemDark(): boolean {
  const [dark, setDark] = useState(() => typeof matchMedia !== "function" || matchMedia(systemDarkQuery).matches);
  useEffect(() => {
    if (typeof matchMedia !== "function") return;
    const media = matchMedia(systemDarkQuery);
    const change = () => setDark(media.matches);
    change();
    media.addEventListener("change", change);
    return () => media.removeEventListener("change", change);
  }, []);
  return dark;
}
export const themeStorageKey = "codealta.desktop.theme.v1";
export const darkerStorageKey = "codealta.desktop.darker.v1";
type Preference = "theme" | "scheme" | "sort" | "rail" | "recent";
export type PreferenceNotices = Partial<Record<Preference, PreferenceIssue>>;

function readPreference<T extends string>(key: string, valid: readonly T[], fallback: T): { value: T; notice?: PreferenceIssue } {
  try {
    const value = localStorage.getItem(key);
    if (value === null) return { value: fallback };
    if (valid.some(option => option === value)) return { value: value as T };
    return { value: fallback, notice: "invalid" };
  } catch {
    return { value: fallback, notice: "unavailable" };
  }
}

export function useWindowPreferences() {
  const [initial] = useState(() => ({
    theme: readPreference(themeStorageKey, themes, "dark"),
    darker: readPreference(darkerStorageKey, ["on", "off"], "off"),
    scheme: readPreference(colorSchemeStorageKey, colorSchemeIds, defaultColorScheme),
    recent: readRecentSessionCount(() => localStorage.getItem(recentSessionCountKey)),
    sort: readPreference(projectSortStorageKey, ["name", "recent"], "name"),
    rail: readPreference(projectRailVisibilityKey, ["expanded", "collapsed"], "expanded"),
  }));
  const [theme, updateTheme] = useState<Theme>(initial.theme.value);
  const [darker, updateDarker] = useState(initial.darker.value === "on");
  const [colorScheme, updateColorScheme] = useState(initial.scheme.value);
  const [recentSessionCount, updateRecent] = useState(initial.recent.value);
  const [projectSort, updateSort] = useState<ProjectSort>(initial.sort.value);
  const [railState, updateRail] = useState<ProjectRailState>({ desktopCollapsed: initial.rail.value === "collapsed", narrowOpen: false });
  const railCurrent = useRef(railState);
  const [notices, setNotices] = useState<PreferenceNotices>({ theme: initial.theme.notice ?? initial.darker.notice, scheme: initial.scheme.notice, sort: initial.sort.notice, rail: initial.rail.notice, recent: initial.recent.issue });

  function setRecentSessionCount(value: number) {
    if (!validRecentSessionCount(value)) return;
    updateRecent(value);
    try { localStorage.setItem(recentSessionCountKey, String(value)); setNotices(current => ({ ...current, recent: undefined })); }
    catch { setNotices(current => ({ ...current, recent: "unsaved" })); }
  }

  function save(key: Preference, persist: () => boolean) {
    const saved = persist();
    setNotices(current => ({ ...current, [key]: saved ? undefined : "unsaved" }));
  }
  function setTheme(value: Theme) {
    updateTheme(value);
    save("theme", () => { try { localStorage.setItem(themeStorageKey, value); return true; } catch { return false; } });
  }
  // Part of the theme: a failure to keep it is reported with the theme.
  function setDarker(value: boolean) {
    updateDarker(value);
    save("theme", () => { try { localStorage.setItem(darkerStorageKey, value ? "on" : "off"); return true; } catch { return false; } });
  }
  function setColorScheme(value: string) {
    if (!colorSchemeIds.includes(value)) return;
    updateColorScheme(value);
    save("scheme", () => { try { localStorage.setItem(colorSchemeStorageKey, value); return true; } catch { return false; } });
  }
  function setProjectSort(value: ProjectSort) {
    updateSort(value);
    save("sort", () => persistProjectSort(value => localStorage.setItem(projectSortStorageKey, value), value));
  }
  function changeRail(next: ProjectRailState, persist: boolean) {
    railCurrent.current = next;
    updateRail(next);
    if (persist) save("rail", () => persistProjectRailCollapsed(value => localStorage.setItem(projectRailVisibilityKey, value), next.desktopCollapsed));
  }
  function setDesktopCollapsed(value: boolean) { changeRail({ ...railCurrent.current, desktopCollapsed: value }, true); }
  function toggleRail(narrow: boolean) { changeRail(toggleProjectRail(railCurrent.current, narrow), !narrow); }
  function closeNarrowRail() { changeRail(resetNarrowRail(railCurrent.current), false); }

  const shownTheme = effectiveTheme(theme, useSystemDark());
  const variant = colorVariant(shownTheme, darker);
  const appearance: ShownAppearance = useMemo(() => ({ theme: shownTheme, scheme: colorScheme, palette: schemePalette(colorSchemeOf(colorScheme), variant) }), [shownTheme, colorScheme, variant]);
  return { theme, shownTheme, variant, appearance, setTheme, darker, setDarker, colorScheme, setColorScheme, projectSort, setProjectSort, railState, setDesktopCollapsed, toggleRail, closeNarrowRail, notices, recentSessionCount, setRecentSessionCount };
}
