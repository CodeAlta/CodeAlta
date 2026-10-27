// The page's link to the scripts of the library and to their runs, for the host of one epoch.
import type { workflows, WorkflowRunDetailResponse, WorkflowRunItem, WorkflowScriptItem } from "#neoastra";

export type WorkflowsApi = Pick<typeof workflows, "scripts" | "runs" | "run" | "saveAsScript" | "resume" | "runDetail">;

/** What the host said of a list; `available` is false in a window without a host of its own. A request that failed gives null. */
export type WorkflowsList<T> = Readonly<{ available: boolean; items: readonly T[] }>;
/** How a request ended: done, or refused with what the host said. */
export type WorkflowsOutcome = Readonly<{ ok: boolean; sessionId?: string | null; message?: string | null }>;

export type WorkflowsHub = ReturnType<typeof createWorkflowsHub>;
/** How a run runs again: resumed (neither), over every recorded result (`all`), or from a step (`from`, its key). */
export type ScriptRerun = Readonly<{ from?: string; all?: boolean }>;

export function createWorkflowsHub(api: WorkflowsApi, epoch: string) {
  return {
    /** The scripts a project sees, or yours and the samples without a project. */
    async scripts(projectId: string | null, signal?: AbortSignal): Promise<WorkflowsList<WorkflowScriptItem> | null> {
      try {
        const reply = await api.scripts({ expectedEpoch: epoch, projectId }, { signal, timeoutMilliseconds: 15_000 });
        return reply.status === "ok" ? { available: true, items: reply.scripts } : reply.status === "unavailable" ? { available: false, items: [] } : null;
      } catch { return null; }
    },
    /** The runs of scripts of the host. */
    async runs(signal?: AbortSignal): Promise<WorkflowsList<WorkflowRunItem> | null> {
      try {
        const reply = await api.runs({ expectedEpoch: epoch }, { signal, timeoutMilliseconds: 15_000 });
        return reply.status === "ok" ? { available: true, items: reply.runs } : reply.status === "unavailable" ? { available: false, items: [] } : null;
      } catch { return null; }
    },
    /**
     * A run for its tab: its records, its script, its result and its graph as Mermaid. `gone` for a run the host no longer
     * keeps; null when the request failed.
     */
    async runDetail(runId: string, signal?: AbortSignal): Promise<WorkflowRunDetailResponse | "gone" | null> {
      try {
        const reply = await api.runDetail({ expectedEpoch: epoch, runId }, { signal, timeoutMilliseconds: 30_000 });
        return reply.status === "ok" ? reply : reply.status === "not_found" ? "gone" : null;
      } catch { return null; }
    },
    /** Starts a script in a new session of a project. The answer comes once the session exists. */
    async run(name: string, projectId: string, args: string | null): Promise<WorkflowsOutcome> {
      try {
        const reply = await api.run({ expectedEpoch: epoch, name, projectId, arguments: args }, { timeoutMilliseconds: 120_000 });
        return { ok: reply.status === "ok", sessionId: reply.sessionId ?? null, message: reply.message ?? null };
      } catch { return { ok: false }; }
    },
    /** Asks the session of a run to turn it into the script of a name. */
    async saveAsScript(sessionId: string, runId: string, name: string): Promise<WorkflowsOutcome> {
      try {
        const reply = await api.saveAsScript({ expectedEpoch: epoch, sessionId, runId, name }, { timeoutMilliseconds: 30_000 });
        return { ok: reply.status === "ok", message: reply.message ?? null };
      } catch { return { ok: false }; }
    },
    /** Asks the session of a run to resume it, to rerun it over its recorded results (`all`), or from one of its steps (`from`). */
    async resume(sessionId: string, runId: string, how: ScriptRerun): Promise<WorkflowsOutcome> {
      try {
        const reply = await api.resume({ expectedEpoch: epoch, sessionId, runId, from: how.from ?? null, all: how.all ?? false }, { timeoutMilliseconds: 30_000 });
        return { ok: reply.status === "ok", message: reply.message ?? null };
      } catch { return { ok: false }; }
    },
  };
}
