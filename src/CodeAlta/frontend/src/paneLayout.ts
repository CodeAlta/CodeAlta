export type PaneLayout = Readonly<{ projects: number; sessions: number }>;
export type PaneName = keyof PaneLayout;

export const defaultPaneLayout: PaneLayout = { projects: 240, sessions: 310 };
const minimum = { projects: 160, sessions: 220 } as const;
const maximum = { projects: 440, sessions: 560 } as const;
const minimumContentWidth = 480;
const splitterWidth = 16;

function clamp(value: number, lower: number, upper: number) {
  return Math.min(upper, Math.max(lower, value));
}

export function constrainPaneLayout(layout: PaneLayout, availableWidth: number): PaneLayout {
  let projects = clamp(Math.round(layout.projects), minimum.projects, maximum.projects);
  let sessions = clamp(Math.round(layout.sessions), minimum.sessions, maximum.sessions);
  const railBudget = Math.max(minimum.projects + minimum.sessions,
    Math.floor(availableWidth) - minimumContentWidth - splitterWidth);
  let overflow = projects + sessions - railBudget;
  if (overflow > 0) {
    const sessionReduction = Math.min(overflow, sessions - minimum.sessions);
    sessions -= sessionReduction;
    overflow -= sessionReduction;
    projects -= Math.min(overflow, projects - minimum.projects);
  }
  return { projects, sessions };
}

export function resizePane(layout: PaneLayout, pane: PaneName, delta: number, availableWidth: number): PaneLayout {
  const other = pane === "projects" ? layout.sessions : layout.projects;
  const paneMaximum = Math.min(maximum[pane],
    Math.max(minimum[pane], Math.floor(availableWidth) - minimumContentWidth - splitterWidth - other));
  return { ...layout, [pane]: clamp(Math.round(layout[pane] + delta), minimum[pane], paneMaximum) };
}

export function parsePaneLayout(value: string | null, availableWidth: number): PaneLayout {
  if (!value) return constrainPaneLayout(defaultPaneLayout, availableWidth);
  try {
    const parsed = JSON.parse(value) as Partial<PaneLayout>;
    if (!Number.isFinite(parsed.projects) || !Number.isFinite(parsed.sessions))
      return constrainPaneLayout(defaultPaneLayout, availableWidth);
    return constrainPaneLayout({ projects: parsed.projects!, sessions: parsed.sessions! }, availableWidth);
  } catch {
    return constrainPaneLayout(defaultPaneLayout, availableWidth);
  }
}

export function restorePaneLayout(load: () => string | null, availableWidth: number): PaneLayout {
  try {
    return parsePaneLayout(load(), availableWidth);
  } catch {
    return constrainPaneLayout(defaultPaneLayout, availableWidth);
  }
}

export function persistPaneLayout(save: (value: string) => void, layout: PaneLayout): boolean {
  try {
    save(JSON.stringify(layout));
    return true;
  } catch {
    return false;
  }
}
