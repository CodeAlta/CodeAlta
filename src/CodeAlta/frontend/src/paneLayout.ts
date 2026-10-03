export type PaneLayout = Readonly<{ projects: number; sessions: number }>;

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

// The hidden project rail contributes no width.
export function collapsedSessionWidth(layout: PaneLayout, availableWidth: number): number {
  const budget = Math.max(minimum.sessions, Math.floor(availableWidth) - minimumContentWidth - splitterWidth / 2);
  return clamp(Math.round(layout.sessions), minimum.sessions, Math.min(maximum.sessions, budget));
}

// Keep preferred widths independent of viewport constraints so expanding the window restores them.
export function parsePaneLayout(value: string | null, _availableWidth: number): PaneLayout {
  if (!value) return defaultPaneLayout;
  try {
    const parsed = JSON.parse(value) as Partial<PaneLayout> | null;
    if (!parsed || !Number.isFinite(parsed.projects) || !Number.isFinite(parsed.sessions))
      return defaultPaneLayout;
    return { projects: clamp(Math.round(parsed.projects!), minimum.projects, maximum.projects),
      sessions: clamp(Math.round(parsed.sessions!), minimum.sessions, maximum.sessions) };
  } catch {
    return defaultPaneLayout;
  }
}

export function restorePaneLayout(load: () => string | null, availableWidth: number): PaneLayout {
  try {
    return parsePaneLayout(load(), availableWidth);
  } catch {
    return defaultPaneLayout;
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
