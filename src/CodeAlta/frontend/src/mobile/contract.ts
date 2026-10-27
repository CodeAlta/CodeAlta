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
  /** The ask the session waits on, which the page reads again when it changes; null when it waits for none (an older CodeAlta does not say). */
  asking?: AskMark | null;
}>;

/** The ask of a session, as the live stream says it: what changes when an answer goes, or did not reach the session. */
export type AskMark = Readonly<{ id: string; revision: string; state: Ask["state"]; immediate: boolean }>;

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
  /** The title of that session, when the phone sees it. */
  parentTitle?: string | null;
  summary: string | null;
  lastActivity: string | null;
  live: Live | null;
}>;

/** A page of `GET /api/sessions`. */
export type SessionPage = Readonly<{
  sessions: readonly Session[];
  next: number;
  hasMore: boolean;
  /** The parents of sub-agents of the page that no page gave yet, which the list shows them under; not counted in `next`. */
  parents?: readonly Session[];
  /** The projects the page keeps to; empty for every project. */
  projects: readonly string[];
  machine: string;
  /** The spaces the phone sees; empty when it sees every space. */
  spaces?: readonly Space[];
  error: string | null;
}>;

/**
 * A file of a user message: its title, its type and the id of its saved copy, a name without a folder (never a path of the
 * computer), by which a later page can ask for an image.
 */
export type EventAttachment = Readonly<{ title: string; mediaType: string; id: string }>;

/**
 * An image of the timeline. A saved copy of the session (an image a tool gave the model) has the `id` the page asks
 * `GET /api/sessions/<id>/images/<image>` for; a picture of the disk that an answer shows has its position in the answer
 * (`GET /api/sessions/<id>/linked-images/<seq>/<position>`) and `src`, its target as the Markdown writes it.
 */
export type EventImage = Readonly<{ title: string; mediaType?: string | null; id: string; src?: string | null }>;

/** A step a script of Code Mode declares (`meta.steps`). */
export type WorkflowStepDeclaration = Readonly<{ id: string; title: string }>;

/**
 * The facts of a record of a run of a script of Code Mode (`workflow.*` events): its run, the tool call that runs it, and
 * what the record says of a step, of a session the script started, of a wait or of its end.
 */
export type WorkflowFacts = Readonly<{
  runId: string; callId?: string | null; stepId?: string | null; childSessionId?: string | null; status?: string | null; title?: string | null;
  message?: string | null; durationMs?: number | null; wakeAt?: string | null; steps?: readonly WorkflowStepDeclaration[] | null;
}>;

/**
 * An event of a session's timeline (`GET /api/sessions/<id>/events`); a user message names its files in `attachments`. A
 * message the host sent names what it is (`provenance`) and the session it comes from (`sourceSessionId`); a call of a
 * script names the call that runs it (`parentId`); a `tool.*` event comes for the `images` a tool gave the model, an answer
 * lists the pictures of the disk it shows; a `workflow.*` event has the facts of a run of a script.
 */
export type TimelineEvent = Readonly<{
  seq: number; at: string | null; kind: string; text: string | null; activityId: string | null; attachments?: readonly EventAttachment[] | null;
  provenance?: string | null; sourceSessionId?: string | null; parentId?: string | null; images?: readonly EventImage[] | null; workflow?: WorkflowFacts | null;
}>;

/** The events of a session after a sequence number. */
export type EventPage = Readonly<{ since: number; events: readonly TimelineEvent[]; error: string | null }>;

/** A project a session can be started in. */
export type Project = Readonly<{ id: string; path: string; name: string; /** The space a new session of the project goes to. */ space?: Space | null }>;

/** `GET /api/projects`. */
export type ProjectList = Readonly<{ projects: readonly Project[]; default: string | null; error: string | null }>;

/** A model a session can be started with. `imageInput` is true only when the model says it reads images. */
export type ModelOption = Readonly<{ provider: string; id: string; name: string; providerName: string; efforts: readonly string[]; defaultEffort: string | null; imageInput?: boolean | null }>;

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

/** A model of the provider of a session, as its composer offers it. `imageInput` is true only when the model says it reads images. */
export type ChoiceModel = Readonly<{ id: string; name: string; efforts: readonly string[]; startEffort: string | null; imageInput?: boolean | null }>;

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

/** A file sent with a prompt: its name, its type and its content, as the desktop composer sends them. */
export type PromptAttachment = Readonly<{ title: string; mediaType: string; base64: string }>;

/** How a prompt goes to a session. */
export type PromptMode = "send" | "steer" | "queue";

/** What a send or a queue changes of the session, chosen on the phone: each stays the session's. */
export type PromptSelection = Readonly<{ model?: string; reasoning?: string; agent?: string; permissionMode?: string }>;

/** `POST /api/sessions`. */
export type NewSessionRequest = Readonly<{
  project: string; text: string; tier?: string; provider?: string; model?: string; reasoning?: string; prompt?: string; permissionMode?: string; worktree?: boolean;
  attachments?: readonly PromptAttachment[];
}>;

/** What `POST /api/sessions` answers. */
export type NewSessionResult = Readonly<{ id: string; pending?: boolean; sendError?: string | null; error?: string | null }>;

/** What the commands of a session answer. */
export type ActionResult = Readonly<{ ok?: boolean; mode?: PromptMode; pending?: boolean; error?: string | null }>;

/** A question of an ask: answered with one of its choices, with text, or both. */
export type AskQuestion = Readonly<{
  title: string;
  question: string;
  description: string | null;
  choices: readonly Readonly<{ title: string; description: string | null }>[];
  freeform: Readonly<{ title: string | null; placeholder: string | null }> | null;
}>;

/** What a review request asks the user to review. */
export type AskReview = Readonly<{
  kind: "file" | "changes" | "branch";
  /** The file of a `file` review, relative to the folder of the session. */
  path: string | null;
  title: string | null;
  /** The message of the agent, in Markdown. */
  message: string | null;
  /** The comments the user wrote on the computer that the answer sends; null when the computer cannot say. */
  comments: number | null;
}>;

/** The ask a session waits on: the questions an agent asks the user, or a review it requests. */
export type Ask = Readonly<{
  id: string;
  /** The revision an answer names: it changes when the ask waits again after an answer the session did not take. */
  revision: string;
  state: "pending" | "submitting" | "indeterminate";
  /** A question of a script, which waits for the answer while its turn runs: shown at once (an older CodeAlta does not say). */
  immediate?: boolean;
  questions: readonly AskQuestion[];
  review: AskReview | null;
}>;

/** `GET /api/sessions/<id>/ask`. */
export type AskPage = Readonly<{ ask: Ask | null; error: string | null }>;

/** The verdict of a review. */
export type AskVerdict = "approved" | "changes_requested" | "commented";

/** `POST /api/sessions/<id>/ask/answer`; a cancel sends the first three only. */
export type AskAnswerRequest = Readonly<{
  actionId: string;
  askId: string;
  revision: string;
  answers: readonly Readonly<{ question: number; choices: readonly number[]; text: string | null }>[];
  review?: Readonly<{ verdict: AskVerdict; overall: string | null }>;
}>;

/** What an answer or a cancel became: `admitted` and `cancelled` reached the session; `pending` while the session takes it. */
export type AskResult = Readonly<{ ok: boolean; status: string; pending?: boolean; error: string | null }>;

/** Whether a session works now. */
export const isBusy = (state: LiveState | null | undefined): boolean => state === "running" || state === "tools" || state === "thinking";
