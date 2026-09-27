import { useRef, useState } from "react";
import type { PreferenceIssue } from "./localization";
import { readRecentSessionCount, recentSessionCountKey, validRecentSessionCount } from "./recentSessions";
import { persistProjectSort, projectSortStorageKey, type ProjectSort } from "./projectRail";
import { persistProjectRailCollapsed, projectRailVisibilityKey, resetNarrowRail, toggleProjectRail, type ProjectRailState } from "./projectRailVisibility";

export type Theme = "dark" | "light";
export const themeStorageKey = "codealta.desktop.theme.v1";
type Preference = "theme" | "sort" | "rail" | "recent";
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
    theme: readPreference(themeStorageKey, ["dark", "light"], "dark"),
    recent: readRecentSessionCount(() => localStorage.getItem(recentSessionCountKey)),
    sort: readPreference(projectSortStorageKey, ["name", "recent"], "name"),
    rail: readPreference(projectRailVisibilityKey, ["expanded", "collapsed"], "expanded"),
  }));
  const [theme, updateTheme] = useState<Theme>(initial.theme.value);
  const [recentSessionCount, updateRecent] = useState(initial.recent.value);
  const [projectSort, updateSort] = useState<ProjectSort>(initial.sort.value);
  const [railState, updateRail] = useState<ProjectRailState>({ desktopCollapsed: initial.rail.value === "collapsed", narrowOpen: false });
  const railCurrent = useRef(railState);
  const [notices, setNotices] = useState<PreferenceNotices>({ theme: initial.theme.notice, sort: initial.sort.notice, rail: initial.rail.notice, recent: initial.recent.issue });

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

  return { theme, setTheme, projectSort, setProjectSort, railState, setDesktopCollapsed, toggleRail, closeNarrowRail, notices, recentSessionCount, setRecentSessionCount };
}
