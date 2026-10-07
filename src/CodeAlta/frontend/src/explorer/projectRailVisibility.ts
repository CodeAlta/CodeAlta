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

/** Moves the focus into the Explorer: on the row that is selected, or on its first row when none is. */
export function focusVisibleProject(rail: Pick<HTMLElement, "querySelector"> | null): boolean {
  const target = rail?.querySelector<HTMLButtonElement>('.project-list button[aria-pressed="true"], .project-root-list button[aria-pressed="true"]')
    ?? rail?.querySelector<HTMLButtonElement>(".project-root-list button, .project-list button");
  if (!target) return false;
  target.focus();
  return true;
}

export function restoreProjectRailFocus(rail: Pick<HTMLElement, "contains"> | null,
  active: Node | null, toggle: Pick<HTMLButtonElement, "focus"> | null): boolean {
  if (!rail?.contains(active) || !toggle) return false;
  toggle.focus();
  return true;
}
