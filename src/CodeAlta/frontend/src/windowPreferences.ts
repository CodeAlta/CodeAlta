import { useEffect, useMemo, useRef, useState } from "react";
import { colorSchemeIds, colorSchemeOf, colorSchemeStorageKey, customSchemeIdOf, customSchemeSelection, customSchemeStorageKey, defaultColorScheme,
  parseCustomScheme, schemePalette, type ColorScheme, type ColorVariant, type CustomColorScheme, type ShownAppearance } from "./colorSchemes";
import type { PreferenceIssue } from "./localization";
import { readRecentSessionCount, readSubAgentCount, recentSessionCountKey, subAgentCountKey, validRecentSessionCount } from "./recentSessions";
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

/**
 * The scheme a selection stands for. A custom scheme is the one the host listed, otherwise the copy the
 * window kept of it (the host has not answered yet, or its file is missing or unreadable for now); a
 * selection that names neither shows the default scheme.
 */
export function selectedScheme(selection: string, listed: readonly CustomColorScheme[] | null, kept: CustomColorScheme | null): ColorScheme | CustomColorScheme {
  const id = customSchemeIdOf(selection);
  if (id === null) return colorSchemeOf(selection);
  return listed?.find(scheme => scheme.id === id) ?? (kept?.id === id ? kept : null) ?? colorSchemeOf(defaultColorScheme);
}

/**
 * The selection once the host has listed the custom schemes. A custom scheme that is not listed stays
 * selected while the window has a copy of it: its file may only be unreadable for a moment, while it is
 * edited by hand, and another instance may list another folder. Without a copy there is nothing to show,
 * and the default scheme is selected.
 */
export function reconciledSelection(selection: string, listed: readonly CustomColorScheme[], kept: CustomColorScheme | null): string {
  const id = customSchemeIdOf(selection);
  return id === null || listed.some(scheme => scheme.id === id) || kept?.id === id ? selection : defaultColorScheme;
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
/** What asks before it is done, unless the user answered not to be asked again: deleting a session, archiving a project. */
export const confirmations = ["sessionDelete", "projectArchive"] as const;
export type Confirmation = typeof confirmations[number];
export const confirmationStorageKey = (confirmation: Confirmation) => `codealta.desktop.confirm.${confirmation}.v1`;
type Preference = "theme" | "scheme" | "sort" | "rail" | "recent" | "subAgents";
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

// A scheme selection is the id of a built-in scheme or names a custom one, which the host lists later.
function readScheme(): { value: string; notice?: PreferenceIssue } {
  try {
    const value = localStorage.getItem(colorSchemeStorageKey);
    if (value === null) return { value: defaultColorScheme };
    return colorSchemeIds.includes(value) || customSchemeIdOf(value) !== null ? { value } : { value: defaultColorScheme, notice: "invalid" };
  } catch {
    return { value: defaultColorScheme, notice: "unavailable" };
  }
}

function readKeptScheme(): CustomColorScheme | null {
  try { return parseCustomScheme(JSON.parse(localStorage.getItem(customSchemeStorageKey) ?? "null")); } catch { return null; }
}

export function useWindowPreferences() {
  const [initial] = useState(() => ({
    theme: readPreference(themeStorageKey, themes, "dark"),
    darker: readPreference(darkerStorageKey, ["on", "off"], "off"),
    scheme: readScheme(),
    kept: readKeptScheme(),
    recent: readRecentSessionCount(() => localStorage.getItem(recentSessionCountKey)),
    subAgents: readSubAgentCount(() => localStorage.getItem(subAgentCountKey)),
    sort: readPreference(projectSortStorageKey, ["name", "recent"], "name"),
    rail: readPreference(projectRailVisibilityKey, ["expanded", "collapsed"], "expanded"),
    confirm: Object.fromEntries(confirmations.map(value => [value, readPreference(confirmationStorageKey(value), ["ask", "skip"], "ask").value === "ask"])) as Record<Confirmation, boolean>,
  }));
  const [theme, updateTheme] = useState<Theme>(initial.theme.value);
  const [darker, updateDarker] = useState(initial.darker.value === "on");
  const [colorScheme, updateColorScheme] = useState(initial.scheme.value);
  // The user's schemes as the host last listed them (null until it has), and the copy kept of the selected one.
  const [customSchemes, updateCustomSchemes] = useState<readonly CustomColorScheme[] | null>(null);
  const [keptScheme, updateKeptScheme] = useState(initial.kept);
  const selection = useRef({ scheme: colorScheme, kept: keptScheme });
  selection.current = { scheme: colorScheme, kept: keptScheme };
  const [recentSessionCount, updateRecent] = useState(initial.recent.value);
  const [subAgentCount, updateSubAgents] = useState(initial.subAgents.value);
  const [projectSort, updateSort] = useState<ProjectSort>(initial.sort.value);
  const [railState, updateRail] = useState<ProjectRailState>({ desktopCollapsed: initial.rail.value === "collapsed", narrowOpen: false });
  const railCurrent = useRef(railState);
  const [notices, setNotices] = useState<PreferenceNotices>({ theme: initial.theme.notice ?? initial.darker.notice, scheme: initial.scheme.notice, sort: initial.sort.notice, rail: initial.rail.notice, recent: initial.recent.issue, subAgents: initial.subAgents.issue });

  function setRecentSessionCount(value: number) {
    if (!validRecentSessionCount(value)) return;
    updateRecent(value);
    try { localStorage.setItem(recentSessionCountKey, String(value)); setNotices(current => ({ ...current, recent: undefined })); }
    catch { setNotices(current => ({ ...current, recent: "unsaved" })); }
  }
  function setSubAgentCount(value: number) {
    if (!validRecentSessionCount(value)) return;
    updateSubAgents(value);
    try { localStorage.setItem(subAgentCountKey, String(value)); setNotices(current => ({ ...current, subAgents: undefined })); }
    catch { setNotices(current => ({ ...current, subAgents: "unsaved" })); }
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
  // The selection, with the custom scheme it names: the next start shows that scheme before the host answers.
  function select(value: string, custom: CustomColorScheme | null) {
    selection.current = { scheme: value, kept: custom };
    updateColorScheme(value);
    updateKeptScheme(custom);
    save("scheme", () => {
      try {
        localStorage.setItem(colorSchemeStorageKey, value);
        if (custom) localStorage.setItem(customSchemeStorageKey, JSON.stringify(custom)); else localStorage.removeItem(customSchemeStorageKey);
        return true;
      } catch { return false; }
    });
  }
  function setColorScheme(value: string) {
    const id = customSchemeIdOf(value);
    const custom = id === null ? null : customSchemes?.find(scheme => scheme.id === id) ?? null;
    if (id === null ? colorSchemeIds.includes(value) : custom !== null) select(value, custom);
  }
  /**
   * Takes the custom schemes the host listed. `selected` also selects one of them (the one just saved).
   * The copy kept of the selected scheme follows its file; a scheme that is not listed keeps its copy.
   */
  function setCustomSchemes(listed: readonly CustomColorScheme[], selected?: string) {
    updateCustomSchemes(listed);
    const current = selection.current;
    const value = selected !== undefined && listed.some(scheme => scheme.id === selected)
      ? customSchemeSelection(selected) : reconciledSelection(current.scheme, listed, current.kept);
    const id = customSchemeIdOf(value);
    const custom = id === null ? null : listed.find(scheme => scheme.id === id) ?? (current.kept?.id === id ? current.kept : null);
    if (value !== current.scheme || JSON.stringify(custom) !== JSON.stringify(current.kept)) select(value, custom);
  }
  // Whether each of these asks first. An answer that could not be kept still stands until the window is closed.
  const [confirms, updateConfirms] = useState(initial.confirm);
  function setConfirm(confirmation: Confirmation, ask: boolean) {
    updateConfirms(current => ({ ...current, [confirmation]: ask }));
    try { localStorage.setItem(confirmationStorageKey(confirmation), ask ? "ask" : "skip"); } catch { /* Asked again at the next start. */ }
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
  const shownScheme = selectedScheme(colorScheme, customSchemes, keptScheme);
  const appearance: ShownAppearance = useMemo(() => ({ theme: shownTheme, scheme: colorScheme, palette: schemePalette(shownScheme, variant) }), [shownTheme, colorScheme, shownScheme, variant]);
  return { theme, shownTheme, variant, appearance, setTheme, darker, setDarker, colorScheme, shownScheme, setColorScheme, customSchemes, setCustomSchemes,
    projectSort, setProjectSort, railState, setDesktopCollapsed, toggleRail, closeNarrowRail, notices, recentSessionCount, setRecentSessionCount, subAgentCount, setSubAgentCount, confirms, setConfirm };
}
