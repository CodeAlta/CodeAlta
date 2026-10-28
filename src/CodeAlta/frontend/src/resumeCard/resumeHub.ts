import type { NeoRpcCallOptions } from "@neoastra/client";
import type { SessionResumeActionRequest, SessionResumeRequest, SessionResumeResult, SessionResumeState } from "#neoastra";

// The resume after a limit: when no model of the tier of a session can take over from a run that stopped on a limit,
// the host can resume the session by itself at the earliest reset. The warning card of the timeline says when, and
// offers Cancel and Resume now while that resume still waits; the host is the one that knows.

/** The resume the card of a timeline offers, as its details name it. */
export type ResumeOffer = Readonly<{ id: string; at: string; providerName: string }>;

/** What the timeline of a session knows of the resume it waits for, as the host last said it. */
export type ResumeHubState = Readonly<{
  pending: ResumeOffer | null;
  /** The action the user started and the host has not answered yet. */
  acting: "cancel" | "resume" | null;
  /** The status of an action the host refused, said beside the buttons until the next one. */
  refused: string | null;
}>;

/** The calls of the host the card makes (the `sessions` service). */
export type ResumeApi = Readonly<{
  pendingResume: (request: SessionResumeRequest, options?: NeoRpcCallOptions) => Promise<SessionResumeState>;
  cancelResume: (request: SessionResumeActionRequest, options?: NeoRpcCallOptions) => Promise<SessionResumeResult>;
  resumeNow: (request: SessionResumeActionRequest, options?: NeoRpcCallOptions) => Promise<SessionResumeResult>;
}>;

export type ResumeHub = Readonly<{
  subscribe: (listener: () => void) => () => void;
  getSnapshot: () => ResumeHubState;
  /** Reads the resume again: the run of the session started or ended. */
  noteRunning: (running: boolean) => void;
  refresh: () => Promise<void>;
  act: (action: "cancel" | "resume", resumeId: string) => Promise<void>;
  dispose: () => void;
}>;

type TimerHandle = ReturnType<typeof setTimeout>;
/** The timers the hub waits with: those of the page, or those of a test. */
export type ResumeTimers = Readonly<{ setTimeout: (callback: () => void, delay: number) => TimerHandle; clearTimeout: (handle: TimerHandle) => void }>;
const pageTimers: ResumeTimers = { setTimeout: (callback, delay) => setTimeout(callback, delay), clearTimeout: handle => clearTimeout(handle) };

const idle: ResumeHubState = Object.freeze({ pending: null, acting: null, refused: null });
const identity = /^[0-9a-f]{32}$/u;
// The host starts a resume at its time: it is read again a little after, to see that it started.
const afterDue = 2_000;
// A timer of the page cannot wait longer than about 24 days: a later resume is read again then.
const longestWait = 0x7fffffff;

/** The state of a session without a resume, which a timeline without a host shows. */
export const noResume = idle;

const isObject = (value: unknown): value is Record<string, unknown> => typeof value === "object" && value !== null && !Array.isArray(value);
const validTime = (value: unknown): value is string => typeof value === "string" && value.length <= 64 && !Number.isNaN(Date.parse(value));
const shortText = (value: unknown, limit: number): value is string => typeof value === "string" && value.trim().length > 0 && value.length <= limit;

/** The resume a card offers, from its details: null for a card that schedules none. */
export function readResumeOffer(details: unknown): ResumeOffer | null {
  if (!isObject(details) || details.resume !== "scheduled") return null;
  const { resumeId, resumeAt, resumeProviderName } = details;
  if (typeof resumeId !== "string" || !identity.test(resumeId) || !validTime(resumeAt)) return null;
  return { id: resumeId, at: resumeAt, providerName: shortText(resumeProviderName, 256) ? resumeProviderName : "" };
}

/** What a reply of the host says the session waits for; undefined for a reply that is not a reading. */
export function readPendingResume(reply: SessionResumeState | null | undefined): ResumeOffer | null | undefined {
  if (!reply || reply.status !== "ok") return undefined;
  const resume = reply.resume;
  if (!resume) return null;
  if (!identity.test(resume.resumeId) || !validTime(resume.resumeAt)) return undefined;
  return { id: resume.resumeId, at: resume.resumeAt, providerName: shortText(resume.providerName, 256) ? resume.providerName : "" };
}

/**
 * The line of the card, as a key of the translations with its values: the time of the resume in the language of the
 * window, with the day when it is not today.
 */
export function resumeLine(offer: ResumeOffer, now: Date, locale?: string): Readonly<{ key: "Resumes at {time} with {provider}" | "Resumes on {date} at {time} with {provider}"
  | "Resumes at {time}" | "Resumes on {date} at {time}"; values: Readonly<Record<string, string>> }> {
  const at = new Date(offer.at);
  const time = new Intl.DateTimeFormat(locale, { hour: "2-digit", minute: "2-digit" }).format(at);
  const sameDay = at.getFullYear() === now.getFullYear() && at.getMonth() === now.getMonth() && at.getDate() === now.getDate();
  const date = new Intl.DateTimeFormat(locale, { weekday: "short", month: "short", day: "numeric" }).format(at);
  const provider = offer.providerName;
  if (sameDay) return provider ? { key: "Resumes at {time} with {provider}", values: { time, provider } } : { key: "Resumes at {time}", values: { time } };
  return provider ? { key: "Resumes on {date} at {time} with {provider}", values: { date, time, provider } } : { key: "Resumes on {date} at {time}", values: { date, time } };
}

/**
 * The resume a session waits for, read from the host when a card first shows it, again when the run of the session
 * starts or ends, a little after its time, and after each action. An action is sent once, whatever its reply.
 */
export function createResumeHub(api: ResumeApi, epoch: string, sessionId: string, timers: ResumeTimers = pageTimers): ResumeHub {
  let state = idle;
  let disposed = false;
  let reads = 0;
  let wake: TimerHandle | null = null;
  let running: boolean | null = null;
  const listeners = new Set<() => void>();
  const publish = (next: ResumeHubState) => {
    if (disposed || next === state) return;
    state = next;
    for (const listener of [...listeners]) listener();
  };
  const stopWaking = () => {
    if (wake !== null) timers.clearTimeout(wake);
    wake = null;
  };
  // Only a resume a card shows is followed.
  const schedule = () => {
    stopWaking();
    if (disposed || !state.pending || listeners.size === 0) return;
    const delay = Math.min(longestWait, Math.max(0, Date.parse(state.pending.at) - Date.now()) + afterDue);
    wake = timers.setTimeout(() => { wake = null; void refresh(); }, delay);
  };
  async function refresh() {
    if (disposed) return;
    const turn = ++reads;
    const reply = await api.pendingResume({ expectedEpoch: epoch, sessionId }, { timeoutMilliseconds: 15_000 }).catch(() => null);
    if (disposed || turn !== reads) return;
    const pending = readPendingResume(reply);
    if (pending === undefined) return;
    if (pending?.id !== state.pending?.id || pending?.at !== state.pending?.at) publish({ ...state, pending });
    schedule();
  }
  return {
    subscribe(listener) {
      const first = listeners.size === 0;
      listeners.add(listener);
      // Nothing is read for a session whose timeline shows no resume; a card shown again reads it again.
      if (first) void refresh();
      return () => {
        listeners.delete(listener);
        if (listeners.size === 0) stopWaking();
      };
    },
    getSnapshot: () => state,
    noteRunning(value) {
      const changed = running !== null && running !== value;
      running = value;
      if (changed && listeners.size > 0) void refresh();
    },
    refresh,
    async act(action, resumeId) {
      if (disposed || state.acting || !identity.test(resumeId)) return;
      publish({ ...state, acting: action, refused: null });
      const request = { expectedEpoch: epoch, sessionId, resumeId };
      const reply = await (action === "cancel" ? api.cancelResume(request, { timeoutMilliseconds: 30_000 }) : api.resumeNow(request, { timeoutMilliseconds: 30_000 }))
        .catch(() => null);
      // A resume that no longer waits is not a refusal: the card goes, as it would have.
      publish({ ...state, acting: null, refused: reply?.status === "ok" || reply?.status === "unknown" ? null : reply?.status ?? "failed" });
      await refresh();
    },
    dispose() {
      disposed = true;
      listeners.clear();
      stopWaking();
    },
  };
}
