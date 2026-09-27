export type ProjectRailState = Readonly<{ desktopCollapsed: boolean; narrowOpen: boolean }>;
export const projectRailVisibilityKey = "codealta.desktop.projectRail.v1";

export function restoreProjectRailCollapsed(read: () => string | null): boolean {
  try { return read() === "collapsed"; }
  catch { return false; }
}

export function persistProjectRailCollapsed(write: (value: string) => void, collapsed: boolean): boolean {
  try { write(collapsed ? "collapsed" : "expanded"); return true; }
  catch { return false; }
}

export function projectRailVisible(state: ProjectRailState, narrow: boolean): boolean {
  return narrow ? state.narrowOpen : !state.desktopCollapsed;
}

export function toggleProjectRail(state: ProjectRailState, narrow: boolean): ProjectRailState {
  return narrow ? { ...state, narrowOpen: !state.narrowOpen }
    : { ...state, desktopCollapsed: !state.desktopCollapsed };
}

export function resetNarrowRail(state: ProjectRailState): ProjectRailState {
  return { ...state, narrowOpen: false };
}

export function focusVisibleProject(rail: Pick<HTMLElement, "querySelector"> | null,
  filter: (Pick<HTMLInputElement, "focus"> & Partial<Pick<HTMLInputElement, "closest">>) | null): boolean {
  const selected = rail?.querySelector<HTMLButtonElement>('.project-list button[aria-pressed="true"], .project-root-list button[aria-pressed="true"]');
  const target = selected ?? filter;
  if (!target) return false;
  if (target === filter) {
    const disclosure = filter?.closest?.<HTMLDetailsElement>("details");
    if (disclosure) disclosure.open = true;
  }
  target.focus();
  return true;
}

export function restoreProjectRailFocus(rail: Pick<HTMLElement, "contains"> | null,
  active: Node | null, toggle: Pick<HTMLButtonElement, "focus"> | null): boolean {
  if (!rail?.contains(active) || !toggle) return false;
  toggle.focus();
  return true;
}
