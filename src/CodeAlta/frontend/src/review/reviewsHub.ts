// The page's link to the reviews of the host: one `reviews.watch` for the whole page, the threads of each review a
// surface shows, the counts of each project, and the calls that change them. Shaped for `useSyncExternalStore`.
import type { reviews, ReviewCheckoutCounts, ReviewRequestDto, ReviewRoundDto, ReviewViewedDto } from "#neoastra";
import { sameCheckout } from "./reviewSessions";
import { orderThreads, readThread, type ReviewSide, type ReviewTargetKind, type ReviewThread, type ReviewVerdict } from "./reviewThreads";

export type ReviewsApi = Pick<typeof reviews, "list" | "add" | "update" | "delete" | "resolve" | "resolveAddressed" | "reply" | "viewed" | "submit" | "counts" | "watch">;
type Timers = Readonly<{ set: (run: () => void, milliseconds: number) => unknown; clear: (timer: unknown) => void }>;

/** A checkout of a project: its folder (`checkout` null) or one of its worktrees, by its full path. */
export type ReviewScope = Readonly<{ projectId: string; checkout: string | null }>;
/** What a review is about: a file of the checkout (relative, with forward slashes), or its changes. */
export type ReviewTarget = Readonly<{ kind: "file"; path: string }> | Readonly<{ kind: "changes" | "branch" }>;

/**
 * The threads of one review as the host last said, resolved ones included. `loaded` is false until the host answered;
 * `status` is what it answered (`ok`, `unavailable`, `invalid_checkout`…); `readOnly` says why nothing can be changed.
 */
export type ReviewList = Readonly<{ loaded: boolean; status: string; threads: readonly ReviewThread[]; rounds: readonly ReviewRoundDto[];
  viewed: readonly ReviewViewedDto[]; request: ReviewRequestDto | null; readOnly: string | null }>;
/** What waits in each checkout of a project that has reviews. */
export type ReviewProjectCounts = Readonly<{ loaded: boolean; checkouts: readonly ReviewCheckoutCounts[] }>;
/** How a change ended: done, or refused with what the host said. */
export type ReviewOutcome = Readonly<{ ok: boolean; status: string; message: string | null }>;
export type ReviewThreadOutcome = ReviewOutcome & Readonly<{ thread: ReviewThread | null }>;
export type ReviewSubmitOutcome = ReviewOutcome & Readonly<{ sessionId: string | null; roundId: string | null }>;
/** A new comment: its lines (0 and 0 for the whole file), its text, and the text it suggests in place of its lines. */
export type ReviewDraft = Readonly<{ side: ReviewSide; startLine: number; endLine: number; body: string; suggestion: string | null }>;
/** Where a round goes: a session, or a new session in the checkout, which takes the provider and model of `like`. */
export type ReviewDestination = Readonly<{ sessionId: string }> | Readonly<{ newSession: true; like?: string | null }>;

const emptyList: ReviewList = Object.freeze({ loaded: false, status: "loading", threads: [], rounds: [], viewed: [], request: null, readOnly: null });
const emptyCounts: ReviewProjectCounts = Object.freeze({ loaded: false, checkouts: [] });
const failed = (status = "failed", message: string | null = null) => ({ ok: false, status, message });
/** How long the page waits before it listens again after the host stopped telling. */
export const reconnectMilliseconds = 2000;

/** The key of a review: a checkout and a target. */
export function reviewKey(scope: ReviewScope, target: ReviewTarget): string {
  return JSON.stringify([scope.projectId, scope.checkout, target.kind, target.kind === "file" ? target.path : null]);
}
const scopeOf = (key: string): ReviewScope => { const [projectId, checkout] = JSON.parse(key) as [string, string | null]; return { projectId, checkout }; };
const sameScope = (first: ReviewScope, second: ReviewScope) => first.projectId === second.projectId && first.checkout === second.checkout;

/** The counts of a checkout among those of its project, or zeros. */
export function checkoutCounts(counts: ReviewProjectCounts, checkout: string | null): ReviewCheckoutCounts {
  return counts.checkouts.find(row => sameCheckout(row.checkout, checkout)) ?? { checkout, drafts: 0, open: 0, addressed: 0, missing: false };
}

export type ReviewsHub = ReturnType<typeof createReviewsHub>;

/**
 * Keeps what the host says of the reviews the page shows. A surface retains the review it shows (a file, the changes
 * of a checkout) and the counts of a project; each is read when it is first retained, and again each time the host
 * says a review changed, through the window or an agent. A change made here reads the reviews of its checkout again
 * before it says it is done, so that what a surface shows next is what the host holds.
 */
export function createReviewsHub(api: ReviewsApi, timers: Timers = { set: (run, milliseconds) => setTimeout(run, milliseconds), clear: timer => clearTimeout(timer as number) }) {
  let epoch: string | null = null;
  const lists = new Map<string, { state: ReviewList; holders: number; turn: number; target: ReviewTarget }>();
  const counts = new Map<string, { state: ReviewProjectCounts; holders: number; turn: number }>();
  const listeners = new Set<() => void>();
  const publish = () => { for (const listener of [...listeners]) listener(); };

  async function readList(key: string) {
    const host = epoch;
    const entry = lists.get(key);
    if (!host || !entry) return;
    const turn = ++entry.turn;
    const scope = scopeOf(key), target = entry.target;
    try {
      const reply = await api.list({ expectedEpoch: host, projectId: scope.projectId, checkout: scope.checkout, target: target.kind,
        path: target.kind === "file" ? target.path : null, includeResolved: true }, { timeoutMilliseconds: 60_000 });
      if (epoch !== host || lists.get(key) !== entry || entry.turn !== turn) return;
      const state: ReviewList = reply.status === "ok"
        ? Object.freeze({ loaded: true, status: "ok", threads: Object.freeze(reply.threads.map(readThread).filter((thread): thread is ReviewThread => thread !== null)),
          rounds: reply.rounds, viewed: reply.viewed, request: reply.request, readOnly: reply.readOnly })
        : Object.freeze({ ...emptyList, loaded: true, status: reply.status, readOnly: reply.message });
      // The same answer again changes nothing on the page.
      if (JSON.stringify(state) === JSON.stringify(entry.state)) return;
      entry.state = state;
      publish();
    } catch {
      if (epoch === host && lists.get(key) === entry && entry.turn === turn && !entry.state.loaded) { entry.state = Object.freeze({ ...emptyList, loaded: true, status: "failed" }); publish(); }
    }
  }

  async function readCounts(projectId: string) {
    const host = epoch;
    const entry = counts.get(projectId);
    if (!host || !entry) return;
    const turn = ++entry.turn;
    try {
      const reply = await api.counts({ expectedEpoch: host, projectId }, { timeoutMilliseconds: 30_000 });
      if (epoch !== host || counts.get(projectId) !== entry || entry.turn !== turn) return;
      const state: ReviewProjectCounts = Object.freeze({ loaded: true, checkouts: reply.status === "ok" ? reply.checkouts : [] });
      if (JSON.stringify(state) === JSON.stringify(entry.state)) return;
      entry.state = state;
      publish();
    } catch { /* Read again at the next notice. */ }
  }

  const readAll = () => Promise.all([...[...lists.keys()].map(readList), ...[...counts.keys()].map(readCounts)]).then(() => undefined);
  // After a change: the reviews of its checkout and the counts of its project, before the change says it is done.
  const readScope = (scope: ReviewScope) => Promise.all([
    ...[...lists.keys()].filter(key => sameScope(scopeOf(key), scope)).map(readList),
    ...(counts.has(scope.projectId) ? [readCounts(scope.projectId)] : []),
  ]).then(() => undefined);

  async function change<T extends { status: string; message: string | null }>(scope: ReviewScope, call: (host: string) => Promise<T>): Promise<(T & { ok: boolean }) | ReturnType<typeof failed>> {
    const host = epoch;
    if (!host) return failed("unavailable");
    try {
      const reply = await call(host);
      await readScope(scope);
      return { ...reply, ok: reply.status === "ok" };
    } catch { return failed(); }
  }
  const threadOutcome = (reply: (Awaited<ReturnType<ReviewsApi["add"]>> & { ok: boolean }) | ReturnType<typeof failed>): ReviewThreadOutcome =>
    ({ ok: reply.ok, status: reply.status, message: reply.message ?? null, thread: "thread" in reply && reply.thread ? readThread(reply.thread) : null });
  const outcome = (reply: { ok: boolean; status: string; message: string | null }): ReviewOutcome => ({ ok: reply.ok, status: reply.status, message: reply.message ?? null });
  const base = (host: string, scope: ReviewScope) => ({ expectedEpoch: host, projectId: scope.projectId, checkout: scope.checkout });

  return {
    subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; },
    /** Whether the page has a host that keeps reviews. */
    available: () => epoch !== null,
    /** Listens to one host until the returned function is called. */
    connect(hostEpoch: string) {
      const abort = new AbortController();
      let timer: unknown = null;
      epoch = hostEpoch;
      for (const entry of lists.values()) { entry.state = emptyList; entry.turn++; }
      for (const entry of counts.values()) { entry.state = emptyCounts; entry.turn++; }
      publish();
      async function watch() {
        try {
          // The first notice comes at once: what was retained before the host answered is read then.
          for await (const _ of await api.watch({ expectedEpoch: hostEpoch }, { signal: abort.signal })) {
            if (abort.signal.aborted) return;
            void readAll();
          }
        } catch { /* Told again below. */ }
        if (!abort.signal.aborted) timer = timers.set(() => void watch(), reconnectMilliseconds);
      }
      void watch();
      return () => {
        abort.abort();
        if (timer !== null) timers.clear(timer);
        if (epoch === hostEpoch) {
          epoch = null;
          for (const entry of lists.values()) { entry.state = emptyList; entry.turn++; }
          for (const entry of counts.values()) { entry.state = emptyCounts; entry.turn++; }
          publish();
        }
      };
    },
    /** The review of a target of a checkout, as last read; a stable object until it changes. */
    list(scope: ReviewScope, target: ReviewTarget): ReviewList { return lists.get(reviewKey(scope, target))?.state ?? emptyList; },
    /** Keeps a review read while a surface shows it; the returned function lets it go. */
    retainList(scope: ReviewScope, target: ReviewTarget): () => void {
      const key = reviewKey(scope, target);
      let entry = lists.get(key);
      if (!entry) lists.set(key, entry = { state: emptyList, holders: 0, turn: 0, target });
      entry.holders++;
      if (entry.holders === 1) void readList(key);
      let released = false;
      return () => {
        if (released) return;
        released = true;
        const current = lists.get(key);
        if (current && --current.holders === 0) lists.delete(key);
      };
    },
    /** The counts of the checkouts of a project, as last read. */
    counts(projectId: string): ReviewProjectCounts { return counts.get(projectId)?.state ?? emptyCounts; },
    retainCounts(projectId: string): () => void {
      let entry = counts.get(projectId);
      if (!entry) counts.set(projectId, entry = { state: emptyCounts, holders: 0, turn: 0 });
      entry.holders++;
      if (entry.holders === 1) void readCounts(projectId);
      let released = false;
      return () => {
        if (released) return;
        released = true;
        const current = counts.get(projectId);
        if (current && --current.holders === 0) counts.delete(projectId);
      };
    },
    /** Reads every retained review again. */
    refresh: readAll,
    /** Where the drafts of a checkout are, in review order: the files and the changes they are written on. */
    async drafts(scope: ReviewScope): Promise<readonly ReviewTarget[]> {
      const host = epoch;
      if (!host) return [];
      try {
        const reply = await api.list({ ...base(host, scope), target: null, path: null, includeResolved: false }, { timeoutMilliseconds: 60_000 });
        if (reply.status !== "ok") return [];
        const found = new Map<string, ReviewTarget>();
        for (const thread of orderThreads(reply.threads.map(readThread).filter((thread): thread is ReviewThread => thread !== null && thread.state === "draft"))) {
          const target: ReviewTarget = thread.target === "file" ? { kind: "file", path: thread.path } : { kind: thread.target };
          found.set(JSON.stringify(target), target);
        }
        return [...found.values()];
      } catch { return []; }
    },
    async add(scope: ReviewScope, target: ReviewTarget, draft: ReviewDraft): Promise<ReviewThreadOutcome> {
      return threadOutcome(await change(scope, host => api.add({ ...base(host, scope), target: target.kind, path: target.kind === "file" ? target.path : "",
        side: draft.side, startLine: draft.startLine, endLine: draft.endLine, body: draft.body, suggestion: draft.suggestion }, { timeoutMilliseconds: 30_000 })));
    },
    /** Changes the text of a comment; a suggestion of null removes it. */
    async update(scope: ReviewScope, id: string, body: string, suggestion: string | null): Promise<ReviewThreadOutcome> {
      return threadOutcome(await change(scope, host => api.update({ ...base(host, scope), id, body, suggestion, clearSuggestion: suggestion === null },
        { timeoutMilliseconds: 30_000 })));
    },
    async remove(scope: ReviewScope, id: string): Promise<ReviewOutcome> {
      return outcome(await change(scope, host => api.delete({ ...base(host, scope), id }, { timeoutMilliseconds: 30_000 })));
    },
    /** Resolves a comment, or opens it again. */
    async resolve(scope: ReviewScope, id: string, resolved: boolean): Promise<ReviewThreadOutcome> {
      return threadOutcome(await change(scope, host => api.resolve({ ...base(host, scope), id, resolved }, { timeoutMilliseconds: 30_000 })));
    },
    async resolveAddressed(scope: ReviewScope, target: ReviewTarget): Promise<ReviewOutcome> {
      return outcome(await change(scope, host => api.resolveAddressed({ ...base(host, scope), target: target.kind, path: target.kind === "file" ? target.path : null },
        { timeoutMilliseconds: 30_000 })));
    },
    async reply(scope: ReviewScope, id: string, body: string): Promise<ReviewThreadOutcome> {
      return threadOutcome(await change(scope, host => api.reply({ ...base(host, scope), id, body }, { timeoutMilliseconds: 30_000 })));
    },
    /** Marks a file of the changes viewed at a revision; null clears the mark. */
    async viewed(scope: ReviewScope, target: Exclude<ReviewTargetKind, "file">, path: string, revision: string | null): Promise<ReviewOutcome> {
      return outcome(await change(scope, host => api.viewed({ ...base(host, scope), target, path, revision }, { timeoutMilliseconds: 30_000 })));
    },
    /** Sends a round: the drafts and the comments still open, with a verdict, to a session or a new one. */
    async submit(scope: ReviewScope, target: ReviewTarget, verdict: ReviewVerdict, overall: string | null, to: ReviewDestination): Promise<ReviewSubmitOutcome> {
      const reply = await change(scope, host => api.submit({ ...base(host, scope), target: target.kind, path: target.kind === "file" ? target.path : null, verdict,
        overall: overall?.trim() ? overall : null, sessionId: "sessionId" in to ? to.sessionId : null, newSession: !("sessionId" in to),
        likeSessionId: "sessionId" in to ? null : to.like ?? null }, { timeoutMilliseconds: 600_000 }));
      return { ok: reply.ok, status: reply.status, message: reply.message ?? null, sessionId: "sessionId" in reply ? reply.sessionId ?? null : null,
        roundId: "roundId" in reply ? reply.roundId ?? null : null };
    },
  };
}
