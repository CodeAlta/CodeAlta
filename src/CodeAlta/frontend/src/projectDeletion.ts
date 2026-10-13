import type { WorkspaceDeleteProjectRequest, WorkspaceDeleteProjectResponse } from "#neoastra";

export type ProjectDeleteTarget = Readonly<{ epoch: string; id: string; path: string; name: string; archived: boolean; generation: number }>;
/**
 * How a deletion ended: `deleted`; `inUse` when a session of the project runs or waits; `children` when a session of
 * another project was started from one of the project; `changed` when the project is no longer the one that was
 * asked about; `failed` when it could not be done or the answer was lost, in which case sessions may be gone already.
 */
export type ProjectDeleteOutcome = "deleted" | "inUse" | "children" | "changed" | "failed";
type Rpc = (request: WorkspaceDeleteProjectRequest, options: { timeoutMilliseconds: number }) => Promise<WorkspaceDeleteProjectResponse>;
export function projectDeleteCurrent(target: ProjectDeleteTarget, current: ProjectDeleteTarget | null): boolean {
  return !!current && target.epoch === current.epoch && target.id === current.id && target.path === current.path
    && target.name === current.name && target.archived === current.archived && target.generation === current.generation;
}

/** One deletion at a time for the window. A deletion that failed can be asked again: the host deletes what is left. */
export function createProjectDeletion(rpc: Rpc) {
  let active = false;
  return {
    get locked() { return active; },
    /** Null when nothing was sent: a deletion is going on, or the project changed since it was asked about. */
    async confirm(target: ProjectDeleteTarget, current: ProjectDeleteTarget | null): Promise<ProjectDeleteOutcome | null> {
      if (active || !projectDeleteCurrent(target, current)) return null;
      active = true;
      const request = Object.freeze({ expectedHostEpoch: target.epoch, projectId: target.id, projectPath: target.path,
        confirmedName: target.name, expectedArchived: target.archived, confirmed: true });
      try {
        const reply = await rpc(request, { timeoutMilliseconds: 600_000 });
        if (reply?.hostEpoch !== target.epoch || reply.projectId !== target.id || reply.projectPath !== target.path) return "failed";
        if (reply.status === "ok") return "deleted";
        if (reply.status === "busy" || reply.status === "session_in_use") return "inUse";
        if (reply.status === "has_children") return "children";
        return reply.status === "scope_missing" || reply.status === "invalid_scope" ? "changed" : "failed";
      } catch { return "failed"; }
      finally { active = false; }
    },
  };
}
