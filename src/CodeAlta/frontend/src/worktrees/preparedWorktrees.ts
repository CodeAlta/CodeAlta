import type { WorkspaceCreateSessionOutcomeRequest, WorkspacePreparedWorktreeResponse, WorkspacePrepareWorktreeRequest } from "#neoastra";
import type { SessionStartStep } from "../SessionStartProgress";

type Call<Request> = (request: Request, options: { timeoutMilliseconds: number }) => Promise<WorkspacePreparedWorktreeResponse>;

/** The part of the host the worktrees made ahead of their session use; a test gives its own. */
export type PreparedWorktreesApi = Readonly<{
  prepare: Call<WorkspacePrepareWorktreeRequest>;
  read: Call<WorkspaceCreateSessionOutcomeRequest>;
  discard: Call<WorkspaceCreateSessionOutcomeRequest>;
}>;

/**
 * A worktree git makes for the next session of a project while its prompt is typed: its steps as the host says them,
 * then ready, or failed with why.
 */
export type PreparedWorktree = Readonly<{
  id: string;
  projectId: string;
  base: string | null;
  /** `dismissed`: a failure the user closed; nothing is made again for that branch until the place changes. */
  state: "preparing" | "ready" | "failed" | "dismissed";
  steps: readonly SessionStartStep[];
  branch: string | null;
  reason: string | null;
  message: string | null;
  warning: string | null;
}>;

/**
 * The worktrees made ahead of their session, one per project at most. The host makes them on its own threads; this
 * page only reads where they are, every `pollMilliseconds` while git works. A worktree that is no longer wanted (the
 * place or the branch changed, the tab was closed) is discarded, and the host removes it. Nothing here waits for git.
 */
export function createPreparedWorktrees(api: PreparedWorktreesApi, newId: () => string = () => crypto.randomUUID(), pollMilliseconds = 400) {
  let epoch: string | null = null;
  let entries: ReadonlyMap<string, PreparedWorktree> = new Map();
  let disposed = false;
  const listeners = new Set<() => void>();

  function publish(change: (next: Map<string, PreparedWorktree>) => void) {
    const next = new Map(entries);
    change(next);
    entries = next;
    for (const listener of [...listeners]) listener();
  }

  // The answer of the host, for the worktree it is about. A worktree the host forgot is forgotten, and made again
  // when it is still wanted; a refusal (or no answer) is a failure that stays, so that nothing is asked in a loop.
  function apply(projectId: string, id: string, reply: WorkspacePreparedWorktreeResponse | null) {
    const current = entries.get(projectId);
    if (current?.id !== id) return;
    if (reply && ["unknown", "used", "discarded"].includes(reply.status) && reply.hostEpoch === epoch) { publish(next => next.delete(projectId)); return; }
    const known = reply && reply.hostEpoch === epoch && ["preparing", "ready", "failed"].includes(reply.status);
    publish(next => next.set(projectId, Object.freeze(known
      ? { ...current, state: reply.status as PreparedWorktree["state"], steps: reply.steps, branch: reply.branch, reason: reply.reason, message: reply.message, warning: reply.warning }
      : { ...current, state: "failed" as const, reason: reply?.status ?? "failed", message: null })));
  }

  async function follow(projectId: string, id: string) {
    while (!disposed && entries.get(projectId)?.id === id && entries.get(projectId)?.state === "preparing") {
      await new Promise(resolve => setTimeout(resolve, pollMilliseconds));
      if (disposed || entries.get(projectId)?.id !== id || !epoch) return;
      try { apply(projectId, id, await api.read({ expectedHostEpoch: epoch, requestId: id }, { timeoutMilliseconds: 5000 })); }
      catch { /* Read again: git goes on whether the page reads it or not. */ }
    }
  }

  function discardEntry(projectId: string) {
    const current = entries.get(projectId);
    if (!current) return;
    publish(next => next.delete(projectId));
    if (epoch) void api.discard({ expectedHostEpoch: epoch, requestId: current.id }, { timeoutMilliseconds: 15_000 }).catch(() => { });
  }

  return {
    /**
     * Has a worktree made for the next session of a project, from a branch or from the commit of its folder (`base`
     * null): one being made or ready is kept, one from another branch is discarded first. A failed one stays, with
     * why, until it is tried again or dismissed. Another host forgets the worktrees of the last one.
     */
    ensure(hostEpoch: string, project: Readonly<{ id: string; path: string }>, base: string | null) {
      if (disposed) return;
      if (epoch !== hostEpoch) { epoch = hostEpoch; if (entries.size) publish(next => next.clear()); }
      const current = entries.get(project.id);
      if (current && current.base === base) return;
      if (current) discardEntry(project.id);
      const id = newId();
      publish(next => next.set(project.id, Object.freeze({ id, projectId: project.id, base, state: "preparing", steps: [], branch: null, reason: null, message: null, warning: null })));
      void api.prepare({ expectedHostEpoch: hostEpoch, projectId: project.id, projectPath: project.path, baseBranch: base, requestId: id }, { timeoutMilliseconds: 30_000 })
        .then(reply => { apply(project.id, id, reply); void follow(project.id, id); }, () => apply(project.id, id, null));
    },
    /** The worktree made for the next session of a project, if any. */
    get(projectId: string): PreparedWorktree | undefined { return entries.get(projectId); },
    /** The worktree of a project a new session can take: ready, and from that branch. */
    ready(projectId: string, base: string | null): string | null {
      const current = entries.get(projectId);
      return current?.state === "ready" && current.base === base ? current.id : null;
    },
    /** Whether git still makes the worktree of a project, from that branch: its session waits for it. */
    preparing(projectId: string, base: string | null): boolean {
      const current = entries.get(projectId);
      return current?.state === "preparing" && current.base === base;
    },
    /** Reads again a worktree a session was asked to take: one a session took is forgotten, one that is still free is kept. */
    async settle(projectId: string, id: string) {
      if (!epoch || entries.get(projectId)?.id !== id) return;
      try { apply(projectId, id, await api.read({ expectedHostEpoch: epoch, requestId: id }, { timeoutMilliseconds: 5000 })); }
      catch { /* The next creation names it again; the host makes another when it was taken. */ }
    },
    /** Gives up the worktree of a project: the host removes it once git has made it. */
    discard(projectId: string) { discardEntry(projectId); },
    /** Closes the failure of a project: nothing is made again for that branch, and the next session makes its own worktree. */
    dismiss(projectId: string) {
      const current = entries.get(projectId);
      if (current?.state === "failed") publish(next => next.set(projectId, Object.freeze({ ...current, state: "dismissed" as const })));
    },
    /** Makes the worktree of a project again after a failure. */
    retry(hostEpoch: string, project: Readonly<{ id: string; path: string }>) {
      const current = entries.get(project.id);
      if (!current || current.state === "preparing" || current.state === "ready") return;
      publish(next => next.delete(project.id));
      this.ensure(hostEpoch, project, current.base);
    },
    /** Gives up every worktree but those of the projects named. */
    keepOnly(projectIds: ReadonlySet<string>) { for (const projectId of [...entries.keys()]) if (!projectIds.has(projectId)) discardEntry(projectId); },
    subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; },
    snapshot(): ReadonlyMap<string, PreparedWorktree> { return entries; },
    dispose() { disposed = true; listeners.clear(); },
  };
}

export type PreparedWorktrees = ReturnType<typeof createPreparedWorktrees>;

/** How far git is in a progress line it wrote, such as `Updating files:  45% (450/1000)`; null when it says no percentage. */
export function progressPercent(line: string | null | undefined): number | null {
  const match = line ? /(\d{1,3})%/u.exec(line) : null;
  if (!match) return null;
  const value = Number(match[1]);
  return value >= 0 && value <= 100 ? value : null;
}
