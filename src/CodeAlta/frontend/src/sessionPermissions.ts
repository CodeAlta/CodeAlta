import type { SessionPermissionCommand, SessionPermissionCommandHandle, SessionPermissionResolution,
  SessionPermissionResolveRequest, SessionPermissionsPage, SessionPermissionsRequest } from "#neoastra";

export type CommandDecision = "allow_once" | "deny" | "cancel";
export type PermissionReviewState =
  | { kind: "loading" | "resolving" }
  | { kind: "ready"; entries: readonly SessionPermissionCommand[]; hasMore: boolean }
  | { kind: "result"; code: "resolved" | "rejected" }
  | { kind: "error"; code: string; reloadRequired: boolean };
type CallOptions = { signal: AbortSignal; timeoutMilliseconds: number };

// App-owned identity and uncertain-response latch; selection-owned manual requests, no polling or event bus.
export function createPermissionReviewer(
  list: (request: SessionPermissionsRequest, options: CallOptions) => Promise<SessionPermissionsPage>,
  resolve: (request: SessionPermissionResolveRequest, options: CallOptions) => Promise<SessionPermissionResolution>,
) {
  let selection = 0;
  let runtime: string | null = null;
  let reloadCode: string | null = null;
  let resolving = false;
  return {
    forSelection(request: SessionPermissionsRequest, signal: AbortSignal, publish: (value: PermissionReviewState) => void) {
      const selected = ++selection;
      if (resolving) reloadCode ??= "uncertain";
      let generation = 0;
      let entries: readonly SessionPermissionCommand[] = [];
      const active = () => !signal.aborted && selected === selection;
      const error = (code: string) => {
        entries = [];
        if (["stale_epoch", "stale_runtime", "uncertain"].includes(code)) reloadCode ??= code;
        if (active()) publish({ kind: "error", code: reloadCode ?? code, reloadRequired: reloadCode !== null });
      };
      signal.addEventListener("abort", () => {
        if (selected === selection && resolving) reloadCode ??= "uncertain";
        entries = [];
      }, { once: true });
      if (reloadCode) error(reloadCode);
      return {
        async refresh(): Promise<void> {
          if (!active()) return;
          if (reloadCode) { error(reloadCode); return; }
          if (resolving) return;
          const current = ++generation;
          entries = [];
          publish({ kind: "loading" });
          try {
            const page = await list(request, { signal, timeoutMilliseconds: 8_000 });
            if (!active() || current !== generation) return;
            if (reloadCode) { error(reloadCode); return; }
            if (page.status === "stale_epoch" || page.hostEpoch !== request.expectedHostEpoch) { error("stale_epoch"); return; }
            if (page.status !== "ok") { error(page.status); return; }
            if (page.sessionId !== request.sessionId || !Array.isArray(page.entries) || page.entries.length > 4
              || typeof page.hasMore !== "boolean" || page.entries.some(entry => !validCommand(entry, request.sessionId))
              || new Set(page.entries.map(entry => entry.handle.attemptId)).size !== page.entries.length) {
              error("invalid_response"); return;
            }
            for (const entry of page.entries) {
              if (runtime !== null && runtime !== entry.handle.runtimeInstanceId) { error("stale_runtime"); return; }
              runtime = entry.handle.runtimeInstanceId;
            }
            entries = Object.freeze(page.entries.map(entry => Object.freeze({ ...entry, handle: Object.freeze({ ...entry.handle }) })));
            publish({ kind: "ready", entries, hasMore: page.hasMore });
          } catch { if (active() && current === generation) error("read_failed"); }
        },
        async decide(entry: SessionPermissionCommand, decision: CommandDecision): Promise<void> {
          if (!active() || resolving || reloadCode || !entries.includes(entry) || !["allow_once", "deny", "cancel"].includes(decision)) return;
          // Retire the entire review window before dispatch. Old closures/refreshes cannot resubmit it.
          ++generation;
          entries = [];
          resolving = true;
          publish({ kind: "resolving" });
          const handle = { ...entry.handle };
          try {
            const response = await resolve({ expectedHostEpoch: request.expectedHostEpoch, handle, decision }, { signal, timeoutMilliseconds: 8_000 });
            if (!active()) { reloadCode ??= "uncertain"; return; }
            if (response.status === "stale_epoch" || response.hostEpoch !== request.expectedHostEpoch) { error("stale_epoch"); return; }
            if (!sameHandle(response.handle, handle)) { error("uncertain"); return; }
            if (response.status === "resolved" || response.status === "rejected") publish({ kind: "result", code: response.status });
            else error(response.status === "disabled" ? "disabled" : "uncertain");
          } catch { error("uncertain"); }
          finally { resolving = false; }
        },
      };
    },
  };
}

function sameHandle(a: SessionPermissionCommandHandle | null, b: SessionPermissionCommandHandle): boolean {
  return !!a && a.operationId === b.operationId && a.runtimeInstanceId === b.runtimeInstanceId
    && a.attachmentGeneration === b.attachmentGeneration && a.sessionId === b.sessionId
    && a.runId === b.runId && a.interactionId === b.interactionId && a.attemptId === b.attemptId;
}
function guid(value: unknown): value is string {
  return typeof value === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(value)
    && value !== "00000000-0000-0000-0000-000000000000";
}
function text(value: unknown, limit: number, required: boolean): value is string {
  if (typeof value !== "string" || value.length > limit || value.includes("\0") || (required && !value.trim())) return false;
  for (let i = 0; i < value.length; i++) {
    const code = value.charCodeAt(i);
    if (code >= 0xd800 && code <= 0xdbff) {
      const low = value.charCodeAt(++i);
      if (!(low >= 0xdc00 && low <= 0xdfff)) return false;
    } else if (code >= 0xdc00 && code <= 0xdfff) return false;
  }
  return true;
}
function identity(value: unknown): value is string {
  return text(value, 128, true) && value === value.trim() && !/[\u0000-\u001f\u007f-\u009f]/.test(value);
}
function validCommand(entry: SessionPermissionCommand, session: string): boolean {
  const h = entry?.handle;
  return !!h && h.sessionId === session && identity(h.sessionId) && identity(h.interactionId)
    && (h.runId === null || identity(h.runId)) && guid(h.operationId) && guid(h.runtimeInstanceId) && guid(h.attemptId)
    && typeof h.attachmentGeneration === "string" && /^[1-9][0-9]{0,18}$/.test(h.attachmentGeneration)
    && BigInt(h.attachmentGeneration) <= 9223372036854775807n && identity(entry.providerId)
    && text(entry.command, 4096, true) && text(entry.workingDirectory, 1024, true)
    && (entry.reason === null || text(entry.reason, 1024, false));
}
