import { useEffect, useMemo, useRef } from "react";
import { modelCatalog, pluginUi, type WorkspaceSnapshot } from "#neoastra";
import { providerSummary } from "../providerSummary";
import { landingPreferences, type LandingPreferenceStore } from "./landingPreferences";
import { landingCanvasId, landingPluginKey, type LandingProject, type LandingSession, type LandingShell } from "./landingShell";

// What the shell of the window does for the landing page: it lends the page what it shows, and opens the page when the application starts.

/** What the shell gives to make the value it lends the page. */
export type LandingShellInput = Readonly<{
  /** The host the window talks to, or null. */
  epoch: string | null;
  /** The space the window shows. */
  space: Readonly<{ id: string; name: string; isDefault: boolean }>;
  /** The projects and the sessions of that space, as the window has them; undefined while it has not read them. */
  snapshot: WorkspaceSnapshot | undefined;
  run: (name: string) => boolean;
  notifyUnavailable: (label: string) => void;
  openProject: (projectId: string) => void;
  openSession: (sessionId: string) => void;
  runCardCommand: (commandId: string, projectId: string | null, label: string) => void;
}>;

/** The projects of a snapshot that the landing page lists: the ones that are not archived. */
export function landingProjects(snapshot: WorkspaceSnapshot): LandingProject[] {
  return snapshot.projects.filter(project => !project.archived).map(project => ({ id: project.id, name: project.name, path: project.path }));
}

/** The sessions of a snapshot as the landing page lists them: a session of an archived project is left out. */
export function landingSessions(snapshot: WorkspaceSnapshot): LandingSession[] {
  const archived = new Set(snapshot.projects.filter(project => project.archived).map(project => project.id));
  return snapshot.sessions.filter(session => session.scopeKind !== "project" || !session.projectId || !archived.has(session.projectId))
    .map(session => ({ id: session.id, title: session.title, projectId: session.scopeKind === "project" ? session.projectId ?? null : null, updatedAt: session.updatedAt,
      child: !!session.parentSessionId }));
}

/**
 * What the shell lends the landing page. It changes with the host, the space and the catalogs only: what the shell does (a command, a
 * project, a session) is read when the page asks, so a new function of the shell does not draw the page again.
 */
export function useLandingShell(input: LandingShellInput): LandingShell {
  const latest = useRef(input);
  latest.current = input;
  const { epoch, snapshot } = input, { id, name, isDefault } = input.space;
  const projects = useMemo(() => snapshot ? landingProjects(snapshot) : null, [snapshot]);
  const sessions = useMemo(() => snapshot ? landingSessions(snapshot) : null, [snapshot]);
  // What the page reads from the host changes with the host and the space only: a new list of sessions does not read the cards again.
  const readers = useMemo<Pick<LandingShell, "readProviders" | "readCards">>(() => ({
    async readProviders(signal) {
      if (!epoch) return null;
      const page = await modelCatalog.providers({ expectedEpoch: epoch }, { signal, timeoutMilliseconds: 8000 });
      if (page.epoch !== epoch || page.status !== "ok") return null;
      const { ready, detecting } = providerSummary(page, epoch);
      return { ready, detecting };
    },
    readCards: signal => pluginUi.landingCards({ expectedEpoch: epoch, spaceId: id }, { signal, timeoutMilliseconds: 20_000 }),
  }), [epoch, id]);
  return useMemo<LandingShell>(() => ({
    epoch, space: { id, name, isDefault }, projects, sessions, ...readers,
    run: command => latest.current.run(command),
    notifyUnavailable: label => latest.current.notifyUnavailable(label),
    // Only a project the page lists: the shell selects nothing that the space does not show.
    openProject: projectId => { if (projects?.some(project => project.id === projectId)) latest.current.openProject(projectId); },
    openSession: sessionId => { if (sessions?.some(session => session.id === sessionId)) latest.current.openSession(sessionId); },
    runCardCommand: (commandId, projectId, label) => latest.current.runCardCommand(commandId, projectId, label),
  }), [epoch, id, name, isDefault, projects, sessions, readers]);
}

/** How long after the window is ready the landing page is still opened for the start: a plugin that comes later does not open it. */
export const landingStartupMilliseconds = 20_000;

/**
 * Whether the landing page opens now, for the start of the application: the user wants it, the window is ready, the start was not
 * handled yet and is not too long ago, and the canvas of the page is among those the plugins declare.
 */
export function opensLandingAtStartup(state: Readonly<{ ready: boolean; handled: boolean; openAtStartup: boolean; elapsedMilliseconds: number; declared: boolean }>): "open" | "wait" | "done" {
  if (state.handled) return "done";
  if (!state.ready) return "wait";
  if (!state.openAtStartup || state.elapsedMilliseconds > landingStartupMilliseconds) return "done";
  return state.declared ? "open" : "wait";
}

/**
 * Opens the landing page in front once, when the application starts, in the space the window shows then. It never runs again in the
 * same run of the window: not when another space is shown, not when the user turns the preference on later, and not for a tab the
 * user closed. A tab of the page that was left open is restored with the other tabs, whatever the preference.
 *
 * @param ready The window has its tabs and a host that plugins run in.
 * @param catalog The canvases the plugins declare now.
 * @param open Opens the tab of a canvas of the application.
 */
export function useLandingAtStartup<T extends Readonly<{ pluginKey: string; id: string }>>(ready: boolean, catalog: readonly T[], open: (item: T) => void,
  preferences: LandingPreferenceStore = landingPreferences): void {
  const handled = useRef(false);
  const readyAt = useRef<number | null>(null);
  const latest = useRef(open);
  latest.current = open;
  useEffect(() => {
    if (ready && readyAt.current === null) readyAt.current = Date.now();
    const item = catalog.find(candidate => candidate.pluginKey === landingPluginKey && candidate.id === landingCanvasId);
    const decision = opensLandingAtStartup({ ready, handled: handled.current, openAtStartup: preferences.get().openAtStartup,
      elapsedMilliseconds: readyAt.current === null ? 0 : Date.now() - readyAt.current, declared: !!item });
    if (decision === "wait") return;
    handled.current = true;
    if (decision === "open" && item) latest.current(item);
  }, [ready, catalog, preferences]);
}
