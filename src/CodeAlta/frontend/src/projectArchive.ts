import type { WorkspaceArchiveProjectRequest, WorkspaceArchiveProjectResponse } from "#neoastra";

export type ArchiveScope = Readonly<{ epoch: string; id: string; path: string; archived: boolean; generation: number }>;
export type ArchiveTarget = ArchiveScope & Readonly<{ source: string; revision: string }>;
export type ArchiveRecord = Readonly<{ target: ArchiveTarget; state: "pending" | "confirmed" | "refused" | "uncertain"; status: string }>;
type Rpc = (request: WorkspaceArchiveProjectRequest, options: { timeoutMilliseconds: number }) => Promise<WorkspaceArchiveProjectResponse>;
export const archiveConsequences = "Catalog metadata only. Existing work is not stopped or canceled. Project files, sessions and drafts are retained. Archived interaction is read-only; unarchive is explicit and does not start work.";

export function archiveScopeCurrent(target: ArchiveScope, current: ArchiveScope | null): boolean {
  return !!current && target.epoch === current.epoch && target.id === current.id && target.path === current.path
    && target.archived === current.archived && target.generation === current.generation;
}

// App-owned for this window, never reset by dialog/scope/Settings changes or snapshot reads.
export function createProjectArchive(rpc: Rpc) {
  let active = false;
  let uncertain = false;
  let records: readonly ArchiveRecord[] = [];
  const request = (scope: ArchiveScope, confirmed: boolean, sourcePath: string | null = null, revision: string | null = null): WorkspaceArchiveProjectRequest => ({
    expectedHostEpoch: scope.epoch, projectId: scope.id, projectPath: scope.path, expectedArchived: scope.archived,
    archived: !scope.archived, confirmed, sourcePath, revision,
  });
  const exact = (scope: ArchiveScope, reply: WorkspaceArchiveProjectResponse) => reply?.hostEpoch === scope.epoch && reply.projectId === scope.id && reply.projectPath === scope.path;
  return {
    get records() { return records; },
    get locked() { return active || uncertain; },
    async prepare(scope: ArchiveScope): Promise<ArchiveTarget | string> {
      if (active || uncertain) return "A previous operation is pending or unconfirmed; no retry will be sent.";
      active = true;
      try {
        const reply = await rpc(request(scope, false), { timeoutMilliseconds: 10_000 });
        if (!exact(scope, reply) || reply.status !== "confirmation_required" || reply.archived !== scope.archived
          || !reply.sourcePath || !/^[0-9A-F]{64}$/u.test(reply.revision ?? ""))
          return `Archive preflight refused (${reply?.status ?? "invalid response"}). Refresh and begin a new confirmation.`;
        return Object.freeze({ ...scope, source: reply.sourcePath, revision: reply.revision! });
      } catch { return "Archive evidence could not be read. No mutation was sent."; }
      finally { active = false; }
    },
    async confirm(target: ArchiveTarget, current: ArchiveScope | null): Promise<ArchiveRecord | null> {
      if (active || uncertain || !archiveScopeCurrent(target, current)) return null;
      active = true;
      const index = records.length;
      records = [...records, { target, state: "pending", status: "Original write pending; closing this dialog does not cancel it." }];
      let result: ArchiveRecord = { target, state: "uncertain", status: "Write unconfirmed. Inspect the catalog; no retry or read-based unlocking is available in this window." };
      try {
        const reply = await rpc(request(target, true, target.source, target.revision), { timeoutMilliseconds: 30_000 });
        if (exact(target, reply)) {
          if (reply.status === "ok" && reply.sourcePath === target.source && reply.revision === target.revision && reply.archived === !target.archived)
            result = { target, state: "confirmed", status: target.archived ? "Unarchive confirmed." : "Archive confirmed." };
          else if (["conflict", "unsupported", "invalid_scope", "busy", "closed", "read_failure"].includes(reply.status))
            result = { target, state: "refused", status: `Not written (${reply.status}). Close and begin a new confirmation after inspecting the catalog.` };
        }
      } catch { /* A lost response or exception is not proof of no write. */ }
      finally { active = false; }
      uncertain = result.state === "uncertain";
      records = records.map((record, i) => i === index ? result : record);
      return result;
    },
  };
}
