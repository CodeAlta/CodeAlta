import type { WorkspaceReadProjectNameRequest, WorkspaceReadProjectNameResponse, WorkspaceRenameProjectRequest,
  WorkspaceRenameProjectResponse, WorkspaceSnapshot } from "#neoastra";
import type { createMutationCapability } from "./sessionOperations";

type Capability = ReturnType<typeof createMutationCapability>;
export type ProjectNameTarget = Readonly<{ epoch: string; id: string; path: string; source: string; revision: string; name: string }>;
type Preflight = { kind: "ready"; target: ProjectNameTarget } | { kind: "error"; code: string };
type Outcome = { kind: "renamed"; target: ProjectNameTarget; name: string } | { kind: "error"; code: string };
type Read = (request: WorkspaceReadProjectNameRequest, options: { timeoutMilliseconds: number }) => Promise<WorkspaceReadProjectNameResponse>;
type Write = (request: WorkspaceRenameProjectRequest, options: { timeoutMilliseconds: number }) => Promise<WorkspaceRenameProjectResponse>;

export function projectNameVisible(snapshot: WorkspaceSnapshot, target: ProjectNameTarget, name: string): boolean {
  return snapshot.configured && snapshot.projects.some(project => project.id === target.id && project.path === target.path
    && !project.archived && project.name === name);
}

export function projectRenameSelectionCurrent(target: ProjectNameTarget, epoch: string | undefined, projectId: string | null): boolean {
  return target.epoch === epoch && target.id === projectId;
}

export function projectRenameMessage(code: string): string {
  switch (code) {
    case "unconfigured": return "Renaming requires an owned host.";
    case "invalid_scope": return "Enter a nonempty name (up to 256 characters, no control characters).";
    case "stale_epoch": return "The host changed. Reload before renaming.";
    case "scope_missing": return "The project changed or is archived. Refresh before renaming.";
    case "conflict": return "The project file changed. Cancel and start again after refreshing.";
    case "unsupported": return "This catalog entry cannot be renamed safely.";
    case "read_failure": return "The catalog could not be read. No rename was sent.";
    case "busy": return "Another project operation is in progress. Try again later.";
    case "closed": return "The host is closing; the rename was not admitted.";
    default: return "The rename may have completed. Refresh and inspect the project; no retry will be sent.";
  }
}

export function createProjectRename(read: Read, write: Write) {
  let active = false;
  const epochValid = (epoch: string | null | undefined) => /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/u.test(epoch ?? "");
  const valid = (value: string | null | undefined, max: number) => {
    if (!value || value.length > max || value !== value.trim() || /[\u0000-\u001f\u007f-\u009f]/u.test(value)) return false;
    for (let i = 0; i < value.length; i++) {
      const code = value.charCodeAt(i);
      if (code < 0xd800 || code > 0xdfff) continue;
      if (code > 0xdbff || ++i === value.length || value.charCodeAt(i) < 0xdc00 || value.charCodeAt(i) > 0xdfff) return false;
    }
    return true;
  };
  const validName = (name: string) => !!name.trim() && valid(name, 256);

  async function preflight(epoch: string | undefined, id: string, path: string, capability: Capability | undefined): Promise<Preflight> {
    if (!epoch || !capability?.canMutate()) return { kind: "error", code: "unconfigured" };
    if (!valid(id, 256) || !valid(path, 4096)) return { kind: "error", code: "invalid_scope" };
    if (active) return { kind: "error", code: "busy" };
    active = true;
    try {
      const response = await read({ expectedHostEpoch: epoch, projectId: id, projectPath: path }, { timeoutMilliseconds: 10_000 });
      if (!response || !epochValid(response.hostEpoch)) return { kind: "error", code: "read_failure" };
      capability.observe({ status: response.status, epoch: response.hostEpoch });
      if (!capability.canMutate() || response.hostEpoch !== epoch) return { kind: "error", code: "stale_epoch" };
      if (response.status === "invalid_scope") return { kind: "error", code: "invalid_scope" };
      if (response.projectId !== id || response.projectPath !== path) return { kind: "error", code: "read_failure" };
      if (response.status !== "ok") return { kind: "error", code: response.status };
      if (!valid(response.sourcePath, 4096) || !validName(response.displayName ?? "")
        || !/^[0-9A-F]{64}$/u.test(response.revision ?? "")) return { kind: "error", code: "read_failure" };
      return { kind: "ready", target: { epoch, id, path, source: response.sourcePath!, revision: response.revision!, name: response.displayName! } };
    } catch { return { kind: "error", code: "read_failure" }; }
    finally { active = false; }
  }

  async function rename(target: ProjectNameTarget, name: string, capability: Capability | undefined): Promise<Outcome> {
    if (!capability?.canMutate()) return { kind: "error", code: "unconfigured" };
    if (!validName(name)) return { kind: "error", code: "invalid_scope" };
    if (active) return { kind: "error", code: "busy" };
    active = true;
    const request: WorkspaceRenameProjectRequest = { expectedHostEpoch: target.epoch, projectId: target.id, projectPath: target.path,
      sourcePath: target.source, revision: target.revision, displayName: name };
    try {
      const response = await write(request, { timeoutMilliseconds: 10_000 });
      if (!response || !epochValid(response.hostEpoch)) return { kind: "error", code: "rename_unconfirmed" };
      capability.observe({ status: response.status, epoch: response.hostEpoch });
      if (!capability.canMutate() || response.hostEpoch !== target.epoch || response.status === "stale_epoch")
        return { kind: "error", code: "rename_unconfirmed" };
      if (response.status === "invalid_scope") return { kind: "error", code: "invalid_scope" };
      if (response.projectId !== target.id || response.projectPath !== target.path || response.sourcePath !== target.source
        || response.revision !== target.revision) return { kind: "error", code: "rename_unconfirmed" };
      if (response.status !== "ok") return { kind: "error", code: response.status };
      if (response.displayName !== name) return { kind: "error", code: "rename_unconfirmed" };
      return { kind: "renamed", target, name };
    } catch { return { kind: "error", code: "rename_unconfirmed" }; }
    finally { active = false; }
  }
  return { preflight, rename };
}
