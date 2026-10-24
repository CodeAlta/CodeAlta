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

/** A session of the list, or one read alone. */
export type Session = Readonly<{
  id: string;
  title: string | null;
  projectId: string | null;
  project: string | null;
  summary: string | null;
  lastActivity: string | null;
  live: Live | null;
}>;

/** A page of `GET /api/sessions`. */
export type SessionPage = Readonly<{
  sessions: readonly Session[];
  next: number;
  hasMore: boolean;
  project: string | null;
  machine: string;
  error: string | null;
}>;

/** An event of a session's timeline (`GET /api/sessions/<id>/events`). */
export type TimelineEvent = Readonly<{ seq: number; at: string | null; kind: string; text: string | null; activityId: string | null }>;

/** The events of a session after a sequence number. */
export type EventPage = Readonly<{ since: number; events: readonly TimelineEvent[]; error: string | null }>;

/** A project a session can be started in. */
export type Project = Readonly<{ id: string; path: string; name: string }>;

/** `GET /api/projects`. */
export type ProjectList = Readonly<{ projects: readonly Project[]; default: string | null; error: string | null }>;

/** A model a session can be started with. */
export type ModelOption = Readonly<{ provider: string; id: string; name: string; providerName: string; efforts: readonly string[]; defaultEffort: string | null }>;

/** An agent prompt. */
export type PromptOption = Readonly<{ id: string; name: string; description: string | null }>;

/** A model tier of the settings: its model, or why it has none. */
export type TierOption = Readonly<{ tier: string; provider: string | null; model: string | null; reasoning: string | null; error: string | null }>;

/** `GET /api/options`. */
export type Options = Readonly<{
  models: readonly ModelOption[];
  prompts: readonly PromptOption[];
  tiers: readonly TierOption[];
  defaultTier: string | null;
  defaults: Readonly<{ provider: string | null; model: string | null; reasoning: string | null; prompt: string | null }> | null;
  error: string | null;
}>;

/** How a prompt goes to a session. */
export type PromptMode = "send" | "steer" | "queue";

/** `POST /api/sessions`. */
export type NewSessionRequest = Readonly<{
  project: string; text: string; tier?: string; provider?: string; model?: string; reasoning?: string; prompt?: string; worktree?: boolean;
}>;

/** What `POST /api/sessions` answers. */
export type NewSessionResult = Readonly<{ id: string; pending?: boolean; sendError?: string | null; error?: string | null }>;

/** What the commands of a session answer. */
export type ActionResult = Readonly<{ ok?: boolean; mode?: PromptMode; pending?: boolean; error?: string | null }>;

/** Whether a session works now. */
export const isBusy = (state: LiveState | null | undefined): boolean => state === "running" || state === "tools" || state === "thinking";
