// The HTTP API of the Mobile plugin's server (src/CodeAlta.Plugin.Mobile), as the phone page reads it. Same origin as the page.

/** What a session is doing: working, running tools, thinking, or nothing. */
export type LiveState = "idle" | "running" | "tools" | "thinking";

/** The folded activity of a session's last turn, as the server sums it up for the one line of its card. */
export type ActivityFacts = Readonly<{
  state?: "running" | "done" | "failed";
  headline: string;
  names?: string | null;
  thoughts: number;
  tools: number;
  succeeded: number;
  failed: number;
  running?: number;
  startedAt?: string | null;
  endedAt?: string | null;
}>;

/** The live state of a session, as the list and the `session` events of `/api/events` give it. */
export type Live = Readonly<{
  id: string;
  projectId?: string | null;
  live: LiveState;
  kind: "user" | "assistant" | "writing" | "activity" | "error" | null;
  line: string | null;
  at: string | null;
  activity: ActivityFacts | null;
  canSteer: boolean;
}>;

/** A space of the computer: its name, and the color and icon the desktop shows it with. */
export type Space = Readonly<{ id: string; name: string; color: string | null; icon: string | null }>;

/** What a session runs on: the provider, the model and the reasoning effort, with the names the desktop shows. */
export type SessionModel = Readonly<{ provider: string; providerName: string | null; id: string | null; name: string | null; reasoning: string | null }>;

/** A session of the list, or one read alone. */
export type Session = Readonly<{
  id: string;
  title: string | null;
  projectId: string | null;
  project: string | null;
  space?: Space | null;
  model?: SessionModel | null;
  /** The agent prompt. */
  agent?: Readonly<{ id: string; name: string | null }> | null;
  /** The folder name of the git worktree the session works in. */
  worktree?: string | null;
  /** The session that started this one: a sub-agent. */
  parentId?: string | null;
  summary: string | null;
  lastActivity: string | null;
  live: Live | null;
}>;

/** A page of `GET /api/sessions`. */
export type SessionPage = Readonly<{
  sessions: readonly Session[];
  next: number;
  hasMore: boolean;
  /** The projects the page keeps to; empty for every project. */
  projects: readonly string[];
  machine: string;
  /** The spaces the phone sees; empty when it sees every space. */
  spaces?: readonly Space[];
  error: string | null;
}>;

/** An event of a session's timeline (`GET /api/sessions/<id>/events`). */
export type TimelineEvent = Readonly<{ seq: number; at: string | null; kind: string; text: string | null; activityId: string | null }>;

/** The events of a session after a sequence number. */
export type EventPage = Readonly<{ since: number; events: readonly TimelineEvent[]; error: string | null }>;

/** A project a session can be started in. */
export type Project = Readonly<{ id: string; path: string; name: string; /** The space a new session of the project goes to. */ space?: Space | null }>;

/** `GET /api/projects`. */
export type ProjectList = Readonly<{ projects: readonly Project[]; default: string | null; error: string | null }>;

/** A model a session can be started with. */
export type ModelOption = Readonly<{ provider: string; id: string; name: string; providerName: string; efforts: readonly string[]; defaultEffort: string | null }>;

/** An agent prompt. */
export type PromptOption = Readonly<{ id: string; name: string; description: string | null }>;

/** A model tier of the settings: its model, or why it has none. */
export type TierOption = Readonly<{ tier: string; provider: string | null; model: string | null; reasoning: string | null; error: string | null }>;

/** A permission mode a session can be given: its id, and the name and description alta gives it (the page names those it knows). */
export type PermissionModeOption = Readonly<{ id: string; name?: string | null; description?: string | null }>;

/** `GET /api/options`. */
export type Options = Readonly<{
  models: readonly ModelOption[];
  prompts: readonly PromptOption[];
  tiers: readonly TierOption[];
  defaultTier: string | null;
  defaults: Readonly<{ provider: string | null; model: string | null; reasoning: string | null; prompt: string | null }> | null;
  /** The modes a new session of each provider can start in; a provider that offers none is not there. */
  permissionModes?: Readonly<Record<string, readonly PermissionModeOption[]>>;
  error: string | null;
}>;

/** A model of the provider of a session, as its composer offers it. */
export type ChoiceModel = Readonly<{ id: string; name: string; efforts: readonly string[]; startEffort: string | null }>;

/** `GET /api/sessions/<id>/choices`: what the composer of the session offers, as the desktop's does. */
export type SessionChoices = Readonly<{
  /** What the session runs on now; a null permission mode is the provider's. */
  current: Readonly<{
    provider: string | null; providerName: string | null; model: string | null; modelName: string | null; reasoning: string | null;
    agent: string | null; agentName: string | null; permissionMode: string | null;
  }>;
  models: readonly ChoiceModel[];
  prompts: readonly Readonly<{ id: string; name: string }>[];
  /** Empty when the provider offers no mode. */
  permissionModes: readonly PermissionModeOption[];
  error: string | null;
}>;

/** How a prompt goes to a session. */
export type PromptMode = "send" | "steer" | "queue";

/** What a send or a queue changes of the session, chosen on the phone: each stays the session's. */
export type PromptSelection = Readonly<{ model?: string; reasoning?: string; agent?: string; permissionMode?: string }>;

/** `POST /api/sessions`. */
export type NewSessionRequest = Readonly<{
  project: string; text: string; tier?: string; provider?: string; model?: string; reasoning?: string; prompt?: string; permissionMode?: string; worktree?: boolean;
}>;

/** What `POST /api/sessions` answers. */
export type NewSessionResult = Readonly<{ id: string; pending?: boolean; sendError?: string | null; error?: string | null }>;

/** What the commands of a session answer. */
export type ActionResult = Readonly<{ ok?: boolean; mode?: PromptMode; pending?: boolean; error?: string | null }>;

/** Whether a session works now. */
export const isBusy = (state: LiveState | null | undefined): boolean => state === "running" || state === "tools" || state === "thinking";
