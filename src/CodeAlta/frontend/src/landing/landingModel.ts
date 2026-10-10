import type { LandingProject, LandingProviders, LandingSession } from "./landingShell";

/** How many recent projects and recent sessions the page lists. */
export const recentLimit = 6;
/** The most cards of plugins the page draws. */
export const cardLimit = 24;
/** The command that opens the documentation shipped with the application, in the window, by the name it has after a slash. */
export const documentationCommand = "documentation";

const time = (value: string | null | undefined): number => { const parsed = value ? Date.parse(value) : Number.NaN; return Number.isFinite(parsed) ? parsed : 0; };

/** The sessions used last, the most recent first. A session started by another one (a sub-agent) is not listed: its parent is. */
export function recentSessions(sessions: readonly LandingSession[], limit = recentLimit): LandingSession[] {
  return sessions.filter(session => !session.child).map((session, index) => ({ session, index, at: time(session.updatedAt) }))
    .sort((left, right) => right.at - left.at || left.index - right.index).slice(0, Math.max(0, limit)).map(item => item.session);
}

/** A project with what its sessions say of it: how many there are, and when the last one was used. */
export type RecentProject = Readonly<{ project: LandingProject; sessions: number; updatedAt: string | null }>;

/** The projects used last, the most recent first, then the ones without any session, by name. */
export function recentProjects(projects: readonly LandingProject[], sessions: readonly LandingSession[], limit = recentLimit): RecentProject[] {
  const seen = new Map<string, { count: number; updatedAt: string | null }>();
  for (const session of sessions) {
    if (!session.projectId) continue;
    const known = seen.get(session.projectId) ?? { count: 0, updatedAt: null };
    known.count++;
    if (time(session.updatedAt) > time(known.updatedAt)) known.updatedAt = session.updatedAt;
    seen.set(session.projectId, known);
  }
  return projects.map(project => ({ project, sessions: seen.get(project.id)?.count ?? 0, updatedAt: seen.get(project.id)?.updatedAt ?? null }))
    .sort((left, right) => time(right.updatedAt) - time(left.updatedAt) || left.project.name.localeCompare(right.project.name))
    .slice(0, Math.max(0, limit));
}

/** What a new user has left to do before a first session: add a project, set up a provider. */
export type OnboardingStep = "project" | "provider";

/**
 * The steps to offer. Nothing is offered for what the window does not know yet: projects that are not read, providers that are
 * still looked for.
 */
export function onboardingSteps(projects: readonly LandingProject[] | null, providers: LandingProviders | null): OnboardingStep[] {
  const steps: OnboardingStep[] = [];
  if (providers && !providers.detecting && providers.ready === 0) steps.push("provider");
  if (projects && projects.length === 0) steps.push("project");
  return steps;
}

/** An action of a card: exactly one of the command and the canvas is set. */
export type LandingCardAction = Readonly<{
  label: string; icon: string | null; iconData: string | null; commandId: string | null; canvas: string | null;
  /** What the canvas is about: only a canvas of a project is opened for the project of the card. Null for a command. */
  canvasScope: "Application" | "Project" | "Session" | null;
  key: string | null; primary: boolean; disabled: boolean;
}>;

/** A command of the plugin of a card, as an element of its fragment names it (`data-alta-command`). */
export type LandingCardCommand = Readonly<{ name: string; id: string }>;

/** A card that a plugin pins on the page, as the host lists it. */
export type LandingCardView = Readonly<{
  id: string; pluginKey: string; plugin: string; cardId: string; title: string; icon: string | null; iconData: string | null;
  /** `failed` when the plugin threw or did not answer: the card then has no content. */
  state: "ok" | "failed";
  html: string; status: string | null; tone: "Info" | "Success" | "Warning" | "Error" | "Muted";
  /** The project of a plugin that belongs to one: its commands and canvases are about it. */
  projectId: string | null; projectName: string | null;
  actions: readonly LandingCardAction[];
  /** The commands of the plugin of the card, for the project of the card: what an element of the fragment can run. */
  commands: readonly LandingCardCommand[];
}>;

/** The command of its own plugin that a card names, or null: a name never reaches a command of another plugin. */
export function cardCommand(card: Pick<LandingCardView, "commands">, name: string): LandingCardCommand | null {
  const wanted = name.toLowerCase();
  return card.commands.find(command => command.name.toLowerCase() === wanted) ?? null;
}

const line = (value: unknown, maximum: number): value is string => typeof value === "string" && value.length <= maximum && !/[\u0000-\u001f\u007f]/u.test(value);
const identity = (value: unknown, maximum = 512): value is string => line(value, maximum) && value.length > 0;
const optional = (value: unknown, maximum: number): string | null => line(value, maximum) && value.length > 0 ? value : null;
// The file of a plugin, as the host sends it with a button: a clean SVG, as a data URL.
const iconData = (value: unknown): string | null => typeof value === "string" && value.length <= 64 * 1024 && value.startsWith("data:image/svg+xml;base64,") ? value : null;
const tones = ["Info", "Success", "Warning", "Error", "Muted"] as const;
const scopes = ["Application", "Project", "Session"] as const;

function readAction(entry: unknown): LandingCardAction | null {
  if (!entry || typeof entry !== "object") return null;
  const item = entry as Record<string, unknown>;
  const commandId = optional(item.commandId, 512), canvas = typeof item.canvas === "string" && /^[A-Za-z0-9._-]{1,64}$/u.test(item.canvas) ? item.canvas : null;
  if (!identity(item.label, 60) || (commandId === null) === (canvas === null)) return null;
  // A canvas whose scope the host did not say is opened as a canvas of the application: without the project of the card.
  const canvasScope = canvas === null ? null : scopes.find(scope => scope === item.canvasScope) ?? "Application";
  return { label: item.label, icon: optional(item.icon, 200), iconData: iconData(item.iconData), commandId, canvas, canvasScope, key: canvas ? optional(item.key, 128) : null,
    primary: item.primary === true, disabled: item.disabled === true };
}

function readCommands(entries: unknown): LandingCardCommand[] {
  if (!Array.isArray(entries)) return [];
  const commands: LandingCardCommand[] = [];
  for (const entry of entries.slice(0, 32)) {
    if (!entry || typeof entry !== "object") continue;
    const item = entry as Record<string, unknown>;
    if (!identity(item.name, 64) || !/^[A-Za-z0-9._-]+$/u.test(item.name) || !identity(item.id) || commands.some(known => known.name.toLowerCase() === (item.name as string).toLowerCase())) continue;
    commands.push({ name: item.name, id: item.id });
  }
  return commands;
}

/** Accepts a well-formed `pluginUi.landingCards` answer; anything else is null. A card that is not well formed is left out. */
export function readLandingCards(reply: unknown): LandingCardView[] | null {
  if (!reply || typeof reply !== "object") return null;
  const value = reply as Record<string, unknown>;
  if (value.status !== "ok" || !Array.isArray(value.cards)) return null;
  const cards: LandingCardView[] = [];
  for (const entry of value.cards.slice(0, cardLimit)) {
    if (!entry || typeof entry !== "object") continue;
    const item = entry as Record<string, unknown>;
    if (!identity(item.id) || !identity(item.pluginKey) || !line(item.plugin, 200) || !identity(item.cardId, 64) || !identity(item.title, 80)
      || cards.some(known => known.id === item.id)) continue;
    const failed = item.state !== "ok" || typeof item.html !== "string" || item.html.length > 64 * 1024;
    const projectId = optional(item.projectId, 256);
    cards.push({
      id: item.id, pluginKey: item.pluginKey, plugin: item.plugin, cardId: item.cardId, title: item.title, icon: optional(item.icon, 200), iconData: iconData(item.iconData),
      state: failed ? "failed" : "ok", html: failed ? "" : item.html as string, status: failed ? null : optional(item.statusText, 60),
      tone: tones.find(tone => tone === item.tone) ?? "Info", projectId, projectName: projectId ? optional(item.projectName, 200) : null,
      actions: failed || !Array.isArray(item.actions) ? [] : item.actions.slice(0, 3).map(readAction).filter((action): action is LandingCardAction => action !== null),
      commands: failed ? [] : readCommands(item.commands),
    });
  }
  return cards;
}

/** Whether two lists of cards show the same thing, so a read that changed nothing keeps what is drawn (and the fields a card may hold). */
export function sameLandingCards(left: readonly LandingCardView[], right: readonly LandingCardView[]): boolean {
  return left.length === right.length && left.every((card, index) => JSON.stringify(card) === JSON.stringify(right[index]));
}
