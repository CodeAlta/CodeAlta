/** One event of the answer the host streams: a part of its text, the whole of it, or why there is none. */
export type SideQuestionEvent = Readonly<{ kind: string; value?: string | null; status?: string | null }>;
/** Asks the host a side question about a session; aborting the signal stops it. */
export type SideQuestionAsk = (sessionId: string, question: string, signal: AbortSignal) => Promise<AsyncIterable<SideQuestionEvent>>;

export type SideQuestionState = "running" | "done" | "stopped" | "failed";
/** A side question and its answer, as it was shown when it was asked. */
export type SideQuestionEntry = Readonly<{ id: number; question: string; answer: string; state: SideQuestionState;
  /** When it was asked, as an ISO date: the list of the questions says how long ago. */
  askedAt: string;
  /** Why it failed: the status of the host (`unsupported`, `missing_session`…), with what the host said, if anything. */
  failure: Readonly<{ status: string; message: string | null }> | null }>;
/** What the panel of a session shows: one answer, the list of the questions, or nothing. */
export type SideQuestionView = Readonly<{ entries: readonly SideQuestionEntry[]; shown: number | null; listing: boolean }>;

/** How many questions a session keeps; the oldest goes first. */
export const sideQuestionLimit = 50;
const empty: SideQuestionView = Object.freeze({ entries: Object.freeze([]), shown: null, listing: false });

/**
 * The side questions of the sessions (`/ask`), for the lifetime of a window: asked of the host, which answers from the
 * conversation of the session and records nothing, and kept here only, by session, so that `/ask` without a question
 * shows them again as they were answered. A question is stopped when its panel is closed while it runs.
 */
export function createSideQuestions(ask: SideQuestionAsk, limit = sideQuestionLimit) {
  const views = new Map<string, SideQuestionView>();
  const running = new Map<number, AbortController>();
  const listeners = new Set<() => void>();
  let next = 1;

  const set = (sessionId: string, view: SideQuestionView) => {
    views.set(sessionId, view);
    for (const listener of [...listeners]) listener();
  };
  const update = (sessionId: string, id: number, change: (entry: SideQuestionEntry) => SideQuestionEntry) => {
    const view = views.get(sessionId);
    if (!view?.entries.some(entry => entry.id === id)) return;
    set(sessionId, { ...view, entries: view.entries.map(entry => entry.id === id ? change(entry) : entry) });
  };
  const stop = (sessionId: string, id: number | null) => {
    const controller = id === null ? undefined : running.get(id);
    if (!controller || id === null) return;
    running.delete(id);
    controller.abort();
    update(sessionId, id, entry => entry.state === "running" ? { ...entry, state: "stopped" } : entry);
  };

  async function run(sessionId: string, id: number, question: string, controller: AbortController) {
    try {
      const events = await ask(sessionId, question, controller.signal);
      for await (const event of events) {
        if (controller.signal.aborted) return;
        if (event.kind === "text" && event.value) update(sessionId, id, entry => ({ ...entry, answer: entry.answer + event.value }));
        else if (event.kind === "done") update(sessionId, id, entry => ({ ...entry, answer: event.value?.trim() ? event.value : entry.answer, state: "done" }));
        else if (event.kind === "error") update(sessionId, id, entry => ({ ...entry, state: "failed", failure: { status: event.status || "failed", message: event.value || null } }));
      }
      // A stream that ended without saying how is an answer that did not come.
      if (!controller.signal.aborted)
        update(sessionId, id, entry => entry.state === "running" ? { ...entry, state: "failed", failure: { status: "failed", message: null } } : entry);
    } catch (error) {
      if (!controller.signal.aborted)
        update(sessionId, id, entry => ({ ...entry, state: "failed", failure: { status: "failed", message: error instanceof Error ? error.message : null } }));
    } finally {
      if (running.get(id) === controller) running.delete(id);
    }
  }

  return {
    /** What the panel of a session shows: the same object until it changes. */
    view(sessionId: string): SideQuestionView { return views.get(sessionId) ?? empty; },
    subscribe(listener: () => void): () => void {
      listeners.add(listener);
      return () => { listeners.delete(listener); };
    },
    /** Asks a question about a session and shows its answer as it comes; the answer shown before is stopped if it runs. */
    ask(sessionId: string, question: string): number | null {
      const text = question.trim();
      if (!text) return null;
      const view = views.get(sessionId) ?? empty;
      stop(sessionId, view.shown);
      const id = next++;
      const entry: SideQuestionEntry = { id, question: text, answer: "", state: "running", failure: null, askedAt: new Date().toISOString() };
      const current = views.get(sessionId) ?? empty;
      const controller = new AbortController();
      running.set(id, controller);
      set(sessionId, { entries: [...current.entries, entry].slice(-limit), shown: id, listing: false });
      if (!controller.signal.aborted) void run(sessionId, id, text, controller);
      return id;
    },
    /** Shows the questions asked of a session, for one to be shown again. */
    list(sessionId: string) {
      const view = views.get(sessionId) ?? empty;
      stop(sessionId, view.shown);
      set(sessionId, { ...(views.get(sessionId) ?? empty), shown: null, listing: true });
    },
    /** Shows the answer of a question asked before, as it was shown. */
    show(sessionId: string, id: number) {
      const view = views.get(sessionId) ?? empty;
      if (!view.entries.some(entry => entry.id === id)) return;
      set(sessionId, { ...view, shown: id, listing: false });
    },
    /** Whether the panel of a session shows something: an answer or the list. */
    isOpen(sessionId: string): boolean {
      const view = views.get(sessionId);
      return !!view && (view.shown !== null || view.listing);
    },
    /** Closes the panel of a session, and stops the answer it shows if that still comes. */
    close(sessionId: string) {
      const view = views.get(sessionId);
      if (!view || view.shown === null && !view.listing) return;
      stop(sessionId, view.shown);
      set(sessionId, { ...(views.get(sessionId) ?? empty), shown: null, listing: false });
    },
    /** Stops every answer that still comes: the window goes away. */
    dispose() {
      for (const controller of running.values()) controller.abort();
      running.clear();
      listeners.clear();
    },
  };
}

export type SideQuestions = ReturnType<typeof createSideQuestions>;

/** The first words of an answer, as one line of plain text, for the list of the questions: no code block, no mark of Markdown. */
export function answerPreview(answer: string): string {
  return answer.replace(/```[\s\S]*?```/gu, " ")
    // A table reads as its cells: no row of dashes, and its cells apart by a dot.
    .replace(/^[ \t]*\|?(?:[ \t]*:?-{3,}:?[ \t]*\|)+[ \t]*:?-*:?[ \t]*\|?[ \t]*$/gmu, "")
    .replace(/^[ \t]*\|(.*)\|[ \t]*$/gmu, (_, cells: string) => cells.split("|").map(cell => cell.trim()).join(" · "))
    .replace(/`([^`\n]*)`/gu, "$1")
    .replace(/\*\*|__/gu, "")
    .replace(/^\s*(?:#{1,6}|[-*+>]|\d+\.)\s+/gmu, "")
    .replace(/\s+/gu, " ").trim();
}

/** A composer command: its question, or an empty string to list questions; null for an ordinary prompt. */
export function parseSideQuestion(text: string): string | null {
  const match = /^\s*\/ask(?:\s+([\s\S]*))?$/iu.exec(text);
  return match ? (match[1]?.trim() ?? "") : null;
}
