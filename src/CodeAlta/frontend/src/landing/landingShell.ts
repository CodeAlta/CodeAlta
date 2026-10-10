import { createContext } from "react";

/** A project of the space the landing page is shown in. */
export type LandingProject = Readonly<{ id: string; name: string; path: string }>;

/** A session of the space the landing page is shown in. */
export type LandingSession = Readonly<{
  id: string; title: string;
  /** The project of the session, or null for a chat that is in no project. */
  projectId: string | null;
  /** When the session was last used, as the catalog of sessions records it. */
  updatedAt: string;
  /** The session was started by another one (a sub-agent). */
  child: boolean;
}>;

/** What the providers of models are doing: how many are ready, and whether the window still looks for them. */
export type LandingProviders = Readonly<{ ready: number; detecting: boolean }>;

/**
 * What the shell of the window lends the landing page: the projects and the sessions it already has for the space it shows, and
 * what only the shell can do (open a project, a session, run a command of the window). The page reads nothing else and keeps
 * nothing: the catalogs are the ones the Explorer shows.
 */
export type LandingShell = Readonly<{
  /** The host the window talks to, or null while there is none: the page then reads no card. */
  epoch: string | null;
  /** The running host's version, validated as on the About page; null for a development or unavailable build. */
  version: string | null;
  /** The space the window shows. */
  space: Readonly<{ id: string; name: string; isDefault: boolean }>;
  /** The projects of the space that are not archived, or null while the window has not read them. */
  projects: readonly LandingProject[] | null;
  /** The sessions of the space, or null while the window has not read them. */
  sessions: readonly LandingSession[] | null;
  /** Reads what the providers of models are doing; null when it could not be read. */
  readProviders(signal: AbortSignal): Promise<LandingProviders | null>;
  /** Reads the cards that plugins pin, as the host answers (`pluginUi.landingCards`). */
  readCards(signal: AbortSignal): Promise<unknown>;
  /**
   * Runs a command by the name it has after a slash (`open`, `model_providers`, `documentation`): a command of the window first,
   * then a command of a plugin. False when no such command can run now.
   */
  run(name: string): boolean;
  /** Tells the user that what a button of the page names cannot be done now. */
  notifyUnavailable(label: string): void;
  /** Shows a project of the space: its new-session prompt, ready for a first message. A project the space does not show is not opened. */
  openProject(projectId: string): void;
  /** Opens a session in a tab. */
  openSession(sessionId: string): void;
  /** Runs the command of an action of a card, for the project of the card. */
  runCardCommand(commandId: string, projectId: string | null, label: string): void;
}>;

/** Given by the shell around the tabs of canvases. Null where there is no shell (a fixture gives its own). */
export const LandingShellContext = createContext<LandingShell | null>(null);

/** Raised on the window when a plugin says its cards of the landing page changed: the page reads them again. */
export const landingCardsChangedEvent = "codealta:landing-cards-changed";

/** The plugin of the application that provides the landing page, and its canvas. */
export const landingPluginKey = "builtin:landing";
export const landingCanvasId = "landing";
