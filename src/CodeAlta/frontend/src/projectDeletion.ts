import type { WorkspaceDeleteProjectRequest, WorkspaceDeleteProjectResponse } from "#neoastra";

export type ProjectDeleteTarget = Readonly<{ epoch: string; id: string; path: string; name: string; archived: boolean; generation: number }>;
type Rpc = (request: WorkspaceDeleteProjectRequest, options: { timeoutMilliseconds: number }) => Promise<WorkspaceDeleteProjectResponse>;
export function projectDeleteCurrent(target: ProjectDeleteTarget, current: ProjectDeleteTarget | null): boolean {
  return !!current && target.epoch === current.epoch && target.id === current.id && target.path === current.path
    && target.name === current.name && target.archived === current.archived && target.generation === current.generation;
}

/** App-owned: a lost response blocks further deletion even after the dialog or selection changes. */
export function createProjectDeletion(rpc: Rpc) {
  let active = false, uncertain = false;
  return {
    get locked() { return active || uncertain; },
    get uncertain() { return uncertain; },
    async confirm(target: ProjectDeleteTarget, current: ProjectDeleteTarget | null): Promise<"confirmed" | "refused" | "uncertain" | null> {
      if (active || uncertain || !projectDeleteCurrent(target, current)) return null;
      active = true;
      let outcome: "confirmed" | "refused" | "uncertain" = "uncertain";
      const request = Object.freeze({ expectedHostEpoch: target.epoch, projectId: target.id, projectPath: target.path,
        confirmedName: target.name, expectedArchived: target.archived, confirmed: true });
      try {
        const reply = await rpc(request, { timeoutMilliseconds: 600_000 });
        if (reply?.hostEpoch === target.epoch && reply.projectId === target.id && reply.projectPath === target.path) {
          if (reply.status === "ok") outcome = "confirmed";
          else if (["invalid_scope", "scope_missing", "busy", "closed", "has_children", "session_in_use", "session_missing", "confirmation_required"].includes(reply.status)) outcome = "refused";
        }
      } catch { /* Partial deletion and a lost reply must never authorize a retry. */ }
      finally { active = false; }
      uncertain = outcome === "uncertain";
      return outcome;
    },
  };
}
