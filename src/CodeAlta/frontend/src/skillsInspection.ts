import type { SkillsScanRequest, SkillsScanResponse, SkillsCandidate } from "#neoastra";
import type { createMutationCapability } from "./sessionOperations";

type Capability = ReturnType<typeof createMutationCapability>;
export type SkillsTarget = Omit<SkillsScanRequest, "requestId" | "rootKind">;
export type SkillsCapture = { target: SkillsTarget; current: () => boolean; capability: Capability };
type State = Readonly<{ busy: boolean; message: string; rows: readonly SkillsCandidate[]; source: string | null }>;
const statuses = Object.freeze(["ok", "invalid_request", "stale_epoch", "busy", "metadata_unavailable", "scope_mismatch", "project_unverified", "archived_project", "canceled", "deadline", "read_failed"]);
const metadataStatuses = Object.freeze(["parsed", "invalid", "unsupported", "too_large", "invalid_path", "missing", "not_directory", "linked", "denied", "read_error", "invalid_encoding", "omitted"]);
const guid = (value: unknown): value is string => typeof value === "string" && /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/u.test(value) && value !== "00000000-0000-0000-0000-000000000000";
const text = (value: unknown, max: number): value is string => typeof value === "string" && value.length > 0 && value.length <= max
  && !/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f-\u009f\u202a-\u202e\u2066-\u2069]/u.test(value)
  && !/[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/u.test(value);
const count = (value: number, maximum: number) => Number.isSafeInteger(value) && value >= 0 && value <= maximum;
const identity = (value: unknown, maximum: number): value is string => text(value, maximum) && value === value.trim() && !/[\r\n\t]/u.test(value);
function valid(reply: SkillsScanResponse, request: SkillsScanRequest): boolean {
  const echo = reply?.request;
  if (!echo || !guid(reply.hostEpoch) || !statuses.includes(reply.status) ||
    (Object.keys(request) as Array<keyof SkillsScanRequest>).some(key => echo[key] !== request[key])) return false;
  if (!Array.isArray(reply.candidates) || reply.candidates.length > 16 || !count(reply.entriesVisited, 257) || !count(reply.directoriesOpened, 64)
    || !count(reply.metadataBytesRead, 4 * (256 * 1024 + 1)) || !count(reply.responseOmitted, 16)
    || typeof reply.diagnostics !== "string" || reply.diagnostics.length > 512) return false;
  const rawFlags = ["None", "EntryLimit", "DirectoryLimit", "PendingDirectoryLimit", "DepthLimit", "PathLimit", "CandidateLimit", "CandidateCharactersLimit", "LinkedEntry", "MetadataDirectory", "ReadFailure", "LinkedRoot", "RootComponentLimit", "none"];
  if (reply.diagnostics.split(", ").some(flag => !rawFlags.includes(flag))) return false;
  if (reply.status !== "ok") return reply.candidates.length === 0 && reply.traversalStatus === null && reply.diagnostics === "none"
    && reply.entriesVisited === 0 && reply.directoriesOpened === 0 && reply.metadataBytesRead === 0 && reply.responseOmitted === 0;
  if (!["complete", "incomplete", "invalid", "missing", "not_directory", "denied", "read_error"].includes(reply.traversalStatus ?? "")) return false;
  const diagnostics = ["none", "frontmatter_missing", "syntax", "required_field", "field", "yaml_feature", "structure", "duplicate_key", "text_limit", "frontmatter_limit", "scalar_limit", "work_limit", "output_limit", "unsafe_path", "read_budget", "display_budget"];
  return reply.candidates.filter(row => row?.status === "parsed").length <= 4
    && reply.candidates.every((row: SkillsCandidate, index: number) => !!row && row.id === `${index}` && metadataStatuses.includes(row.status)
    && diagnostics.includes(row.diagnostic)
    && (row.relativePath === null && row.status === "omitted" || text(row.relativePath, 1024)
      && !/[\\:\r\n\t]/u.test(row.relativePath) && !row.relativePath.startsWith("/") && !row.relativePath.split("/").some(part => part === ".." || part === "." || !part))
    && (row.status === "parsed" ? text(row.name, 64) && text(row.description, 1024) : row.name === null && row.description === null))
    && reply.candidates.reduce((sum: number, row: SkillsCandidate) => sum + (row.relativePath?.length ?? 0), 0) <= 8192;
}

// App-owned: dismissing the panel never drops the single pending original or admits a second read.
export function createSkillsInspection(invoke: (request: SkillsScanRequest, options: { timeoutMilliseconds: number }) => Promise<SkillsScanResponse>) {
  let revision = 0;
  let original: Readonly<SkillsScanRequest> | null = null;
  let state: State = { busy: false, rows: [], source: null, message: "Not scanned. Choose a root and explicitly scan raw file candidates." };
  const listeners = new Set<() => void>();
  const publish = (next: State) => { state = next; for (const listener of [...listeners]) { try { listener(); } catch { /* One view must not interrupt read ownership. */ } } };
  const invalidate = () => { revision++; if (state.rows.length || state.source !== null) publish({ ...state, rows: [], source: null,
    message: state.busy ? "Original read retained; changed capture will discard metadata. OS calls may still block." : "Capture changed. Not scanned for this selection." }); };
  return {
    getSnapshot: () => state,
    pendingRequest: () => original,
    subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; },
    invalidate,
    async scan(target: SkillsTarget & { rootKind: string }, current: () => boolean, capability: Capability) {
      if (original || state.busy || !current() || !capability.canSubmit({ expectedEpoch: target.expectedHostEpoch }) || !guid(target.expectedHostEpoch)
        || !identity(target.sessionId, 256) || !identity(target.createdAt, 64) || !Number.isFinite(Date.parse(target.createdAt))
        || !["global", "project"].includes(target.scope)
        || target.scope === "global" && (target.projectId !== null || target.projectPath !== null)
        || target.scope === "project" && (!identity(target.projectId, 64) || !identity(target.projectPath, 4096))
        || !["user_alta", "project_alta"].includes(target.rootKind) || target.rootKind === "project_alta" && target.scope !== "project") return;
      const version = ++revision;
      const request = Object.freeze({ expectedHostEpoch: target.expectedHostEpoch, sessionId: target.sessionId, createdAt: target.createdAt,
        scope: target.scope, projectId: target.projectId, projectPath: target.projectPath, rootKind: target.rootKind, requestId: crypto.randomUUID() });
      original = request;
      const stillCurrent = () => { try { return version === revision && capability.canSubmit({ expectedEpoch: request.expectedHostEpoch }) && current(); } catch { return false; } };
      publish({ busy: true, rows: [], source: request.rootKind, message: "Reading one explicit raw root. Closing does not release the original read." });
      if (!stillCurrent()) { original = null; publish({ busy: false, rows: [], source: null, message: "Capture ended before dispatch. Nothing scanned." }); return; }
      try {
        const result = await invoke(request, { timeoutMilliseconds: 30_000 });
        const correlated = valid(result, request);
        if (correlated) {
          // Only the original captured capability receives validated evidence, even after dismissal.
          if (result.status === "stale_epoch" || result.hostEpoch !== request.expectedHostEpoch) revision++;
          capability.observe({ status: result.status, epoch: result.hostEpoch });
        }
        const release = { busy: false, rows: [], source: null };
        original = null;
        if (!correlated) publish({ ...release, message: "Invalid/uncorrelated response; metadata not accepted. Explicit new scan required." });
        else if (!stillCurrent()) publish({ ...release, message: "Original read settled after capture changed. Metadata discarded; no automatic scan." });
        else if (result.status !== "ok") publish({ ...release, message: `Unknown raw candidates: ${result.status}. No effective inventory inference.` });
        else publish({ busy: false, rows: result.candidates.map(row => Object.freeze({ id: row.id, relativePath: row.relativePath,
          status: row.status, diagnostic: row.diagnostic, name: row.name, description: row.description })), source: request.rootKind,
          message: `Raw traversal: ${result.traversalStatus}; ${result.entriesVisited} entries, ${result.directoriesOpened} directories, ${result.metadataBytesRead} reported metadata bytes. Omissions: ${result.diagnostics}; ${result.responseOmitted} response rows omitted. ${result.candidates.filter(row => row.status === "omitted").length} metadata rows omitted. Not effective discovery; zero rows never proves absent skills.` });
      } catch {
        // A transport failure/timeout cannot prove that a blocking host read stopped. Keep the original slot.
        publish({ busy: true, rows: [], source: null, message: "Original read outcome unknown (transport failure/timeout). Further scans blocked in this App; OS work may still be pending. No retry." });
      }
    },
  };
}
export type SkillsInspection = ReturnType<typeof createSkillsInspection>;
