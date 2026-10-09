import type { WorkspaceArchiveProjectRequest, WorkspaceArchiveProjectResponse } from "#neoastra";

export type ArchiveScope = Readonly<{ epoch: string; id: string; path: string; archived: boolean; generation: number }>;
export type ArchiveTarget = ArchiveScope & Readonly<{ source: string; revision: string }>;
/** How a write ended: done, refused with nothing written, or without an answer that says which. */
export type ArchiveOutcome = Readonly<{ target: ArchiveTarget; state: "confirmed" | "refused" | "uncertain"; status: string }>;
type Rpc = (request: WorkspaceArchiveProjectRequest, options: { timeoutMilliseconds: number }) => Promise<WorkspaceArchiveProjectResponse>;

export function archiveScopeCurrent(target: ArchiveScope, current: ArchiveScope | null): boolean {
  return !!current && target.epoch === current.epoch && target.id === current.id && target.path === current.path
    && target.archived === current.archived && target.generation === current.generation;
}

// App-owned for this window, never reset by scope/Settings changes or snapshot reads.
export function createProjectArchive(rpc: Rpc) {
  let active = false;
  let uncertain = false;
  const request = (scope: ArchiveScope, confirmed: boolean, sourcePath: string | null = null, revision: string | null = null): WorkspaceArchiveProjectRequest => ({
    expectedHostEpoch: scope.epoch, projectId: scope.id, projectPath: scope.path, expectedArchived: scope.archived,
    archived: !scope.archived, confirmed, sourcePath, revision,
  });
  const exact = (scope: ArchiveScope, reply: WorkspaceArchiveProjectResponse) => reply?.hostEpoch === scope.epoch && reply.projectId === scope.id && reply.projectPath === scope.path;
  return {
    get locked() { return active || uncertain; },
    /** A write ended without an answer: nothing more is written from this window. */
    get uncertain() { return uncertain; },
    async prepare(scope: ArchiveScope): Promise<ArchiveTarget | string> {
      if (active || uncertain) return "An earlier change of a project archive is pending or unconfirmed. Nothing was sent.";
      active = true;
      try {
        const reply = await rpc(request(scope, false), { timeoutMilliseconds: 10_000 });
        if (!exact(scope, reply) || reply.status !== "confirmation_required" || reply.archived !== scope.archived
          || !reply.sourcePath || !/^[0-9A-F]{64}$/u.test(reply.revision ?? ""))
          return `The project could not be read (${reply?.status ?? "invalid response"}). Nothing was written.`;
        return Object.freeze({ ...scope, source: reply.sourcePath, revision: reply.revision! });
      } catch { return "The project could not be read. Nothing was written."; }
      finally { active = false; }
    },
    async confirm(target: ArchiveTarget, current: ArchiveScope | null): Promise<ArchiveOutcome | null> {
      if (active || uncertain || !archiveScopeCurrent(target, current)) return null;
      active = true;
      let result: ArchiveOutcome = { target, state: "uncertain", status: "The change could not be confirmed. Check the project; it is not sent again from this window." };
      try {
        const reply = await rpc(request(target, true, target.source, target.revision), { timeoutMilliseconds: 30_000 });
        if (exact(target, reply)) {
          if (reply.status === "ok" && reply.sourcePath === target.source && reply.revision === target.revision && reply.archived === !target.archived)
            result = { target, state: "confirmed", status: target.archived ? "Unarchive confirmed." : "Archive confirmed." };
          else if (["conflict", "unsupported", "invalid_scope", "busy", "closed", "read_failure"].includes(reply.status))
            result = { target, state: "refused", status: `Nothing was written (${reply.status}). Refresh the projects and try again.` };
        }
      } catch { /* A lost response or exception is not proof of no write. */ }
      finally { active = false; }
      uncertain = result.state === "uncertain";
      return result;
    },
  };
}
