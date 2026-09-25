import type { WorkspaceOpenProjectRequest, WorkspaceOpenProjectResponse } from "#neoastra";
import type { createMutationCapability } from "./sessionOperations";

type Capability = ReturnType<typeof createMutationCapability>;
type Result = { kind: "ready"; requestedPath: string; path: string } | { kind: "imported"; path: string; id: string }
  | { kind: "error"; code: string };
export type ImportEvidence = Readonly<{ kind: "pending" | "uncertain" | "imported"; epoch: string;
  requestedPath: string; path: string; projectId?: string }>;
type Invoke = (request: WorkspaceOpenProjectRequest, options: { timeoutMilliseconds: number }) => Promise<WorkspaceOpenProjectResponse>;

export function canImportCheckedFolder(ready: { requestedPath: string; path: string } | undefined,
  confirmed: boolean, busy: boolean, canMutate: boolean): boolean {
  return !!ready && confirmed && !busy && canMutate;
}

function text(value: unknown, maximum: number): value is string {
  if (typeof value !== "string" || !value || value.length > maximum || /[\u0000-\u001f\u007f-\u009f]/u.test(value)) return false;
  for (let i = 0; i < value.length; i++) {
    const ch = value.charCodeAt(i);
    if (ch < 0xd800 || ch > 0xdfff) continue;
    if (ch > 0xdbff || ++i === value.length || value.charCodeAt(i) < 0xdc00 || value.charCodeAt(i) > 0xdfff) return false;
  }
  return true;
}

export function createProjectOpening(invoke: Invoke) {
  let active = false;
  let evidence: ImportEvidence | null = null;
  const listeners = new Set<() => void>();
  function publish(value: ImportEvidence | null) {
    evidence = value;
    for (const listener of listeners) listener();
  }
  async function call(epoch: string | undefined, path: string, confirmed: boolean, capability: Capability | undefined,
    expectedPath?: string): Promise<Result> {
    if (!epoch || !capability?.canMutate()) return { kind: "error", code: "unconfigured" };
    if (!text(path, 4096) || path !== path.trim()) return { kind: "error", code: "invalid_request" };
    if (active) return { kind: "error", code: "busy" };
    active = true; // Admission precedes invoking the bridge; never retry an uncertain import.
    try {
      const result = await invoke({ expectedHostEpoch: epoch, directoryPath: path, confirmed }, { timeoutMilliseconds: 10000 });
      if (!result || !text(result.status, 64) || !/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/u.test(result.hostEpoch ?? ""))
        return { kind: "error", code: confirmed ? "import_unconfirmed" : "invalid_response" };
      capability.observe({ status: result.status, epoch: result.hostEpoch });
      if (!capability.canMutate() || result.hostEpoch !== epoch) return { kind: "error", code: "stale_epoch" };
      if (result.status === "stale_epoch") return { kind: "error", code: "stale_epoch" };
      if (confirmed && ["missing_directory", "invalid_request", "closed", "busy"].includes(result.status)
        && result.requestedPath === path && result.projectId === null) return { kind: "error", code: result.status };
      if (result.requestedPath !== path || !text(result.projectPath, 4096)) {
        return { kind: "error", code: confirmed ? "import_unconfirmed" : result.status === "confirmation_required" ? "invalid_response" : result.status };
      }
      if (!confirmed && result.status === "confirmation_required" && result.projectId === null)
        return { kind: "ready", requestedPath: path, path: result.projectPath };
      if (confirmed && result.status === "ok" && result.projectPath === expectedPath && text(result.projectId, 256))
        return { kind: "imported", path: result.projectPath, id: result.projectId };
      if (result.status === "ok") return { kind: "error", code: confirmed ? "import_unconfirmed" : "invalid_response" };
      return { kind: "error", code: result.status };
    } catch {
      return { kind: "error", code: confirmed ? "import_unconfirmed" : "read_failed" };
    } finally { active = false; }
  }
  return {
    subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; },
    getSnapshot: () => evidence,
    confirmOpened(epoch: string, path: string, id: string): boolean {
      if (evidence?.kind !== "imported" || evidence.epoch !== epoch || evidence.path !== path || evidence.projectId !== id) return false;
      publish(null); return true;
    },
    preview(epoch: string | undefined, path: string, capability: Capability | undefined): Promise<Result> {
      if (evidence) return Promise.resolve({ kind: "error", code: "busy" });
      return call(epoch, path, false, capability);
    },
    async import(epoch: string | undefined, ready: { requestedPath: string; path: string }, capability: Capability | undefined): Promise<Result> {
      if (evidence) return { kind: "error", code: "busy" };
      if (!epoch || !capability?.canMutate()) return { kind: "error", code: "unconfigured" };
      if (!ready || !text(ready.requestedPath, 4096) || ready.requestedPath !== ready.requestedPath.trim()
        || !text(ready.path, 4096) || active) return { kind: "error", code: active ? "busy" : "invalid_request" };
      const target = { epoch, requestedPath: ready.requestedPath, path: ready.path };
      publish({ ...target, kind: "pending" }); // Retained by the app instance, not by the dialog lifetime.
      const result = await call(epoch, ready.requestedPath, true, capability, ready.path);
      if (result.kind === "imported") publish({ ...target, kind: "imported", projectId: result.id });
      else if (result.kind === "error" && ["unconfigured", "invalid_request", "missing_directory", "closed", "busy"].includes(result.code)) publish(null);
      else publish({ ...target, kind: "uncertain" });
      return result;
    },
  };
}

export function projectOpeningMessage(code: string): string {
  switch (code) {
    case "unconfigured": return "Import requires an owned host; catalog-only browsing does not change projects.";
    case "invalid_request": return "Enter a full, absolute directory path (not a relative path, ~, or a file).";
    case "missing_directory": return "The directory does not exist or is not accessible. Nothing was imported.";
    case "stale_epoch": return "The host changed. Reload before importing a directory.";
    case "busy": return "An earlier directory request is still in progress.";
    case "closed": return "The host is closing. Nothing new was imported.";
    case "read_failed": return "Could not check this directory. No import request was sent.";
    case "invalid_response": return "Directory check returned invalid data. No import request was sent.";
    default: return "Import outcome is uncertain. Inspect the project list before attempting another import; there is no automatic retry.";
  }
}
