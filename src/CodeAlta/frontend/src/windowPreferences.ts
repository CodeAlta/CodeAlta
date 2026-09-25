import { useRef, useState } from "react";
import { persistProjectSort, projectSortStorageKey, type ProjectSort } from "./projectRail";
import { persistProjectRailCollapsed, projectRailVisibilityKey, resetNarrowRail, toggleProjectRail, type ProjectRailState } from "./projectRailVisibility";

export type Theme = "dark" | "light";
export const themeStorageKey = "codealta.desktop.theme.v1";
type Preference = "theme" | "sort" | "rail";
type Notices = Partial<Record<Preference, string>>;

function readPreference<T extends string>(key: string, valid: readonly T[], fallback: T, label: string): { value: T; notice?: string } {
  try {
    const value = localStorage.getItem(key);
    if (value === null) return { value: fallback };
    if (valid.some(option => option === value)) return { value: value as T };
    return { value: fallback, notice: `${label}: invalid saved preference; using ${fallback}. Not overwritten.` };
  } catch {
    return { value: fallback, notice: `${label}: local storage unavailable; using ${fallback}. Not saved.` };
  }
}

export function useWindowPreferences() {
  const [initial] = useState(() => ({
    theme: readPreference(themeStorageKey, ["dark", "light"], "dark", "Theme"),
    sort: readPreference(projectSortStorageKey, ["name", "recent"], "name", "Project sort"),
    rail: readPreference(projectRailVisibilityKey, ["expanded", "collapsed"], "expanded", "Desktop projects"),
  }));
  const [theme, updateTheme] = useState<Theme>(initial.theme.value);
  const [projectSort, updateSort] = useState<ProjectSort>(initial.sort.value);
  const [railState, updateRail] = useState<ProjectRailState>({ desktopCollapsed: initial.rail.value === "collapsed", narrowOpen: false });
  const railCurrent = useRef(railState);
  const [notices, setNotices] = useState<Notices>({ theme: initial.theme.notice, sort: initial.sort.notice, rail: initial.rail.notice });

  function save(key: Preference, persist: () => boolean) {
    const saved = persist();
    setNotices(current => ({ ...current, [key]: saved ? undefined : `${key === "sort" ? "Project sort" : key === "rail" ? "Desktop projects" : "Theme"}: applied in this window, but local storage could not save the change.` }));
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

  return { theme, setTheme, projectSort, setProjectSort, railState, setDesktopCollapsed, toggleRail, closeNarrowRail, notices };
}
