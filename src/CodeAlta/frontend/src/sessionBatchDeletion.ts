import type { WorkspaceDeleteSessionRequest, WorkspaceDeleteSessionResponse, WorkspaceSnapshot } from "#neoastra";
import { resolveSessionTab, type SessionTab } from "./sessionTabs";
import type { createMutationCapability } from "./sessionOperations";

export const sessionDeleteBatchLimit = 32;
type Request = Readonly<WorkspaceDeleteSessionRequest>;
type Capability = ReturnType<typeof createMutationCapability>;
export type BatchDeleteItem = Readonly<{ request: Request; outcome: "not-started" | "pending" | "deleted" | "refused" | "uncertain"; code?: string }>;
type State = Readonly<{ phase: "idle" | "review" | "running" | "settled"; items: readonly BatchDeleteItem[]; message: string }>;
const exact = (value: unknown, max: number): value is string => typeof value === "string" && value.length > 0 && value.length <= max
  && value === value.trim() && !/[\u0000-\u001f\u007f-\u009f]/u.test(value)
  && !/[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/.test(value);

export function batchDeleteCandidate(snapshot: WorkspaceSnapshot, tab: SessionTab, epoch: string): Request | null {
  if (!snapshot.configured || snapshot.sessionsTruncated || snapshot.projectsTruncated || snapshot.displayTextTruncated) return null;
  const row = resolveSessionTab(snapshot, tab);
  if (!row || snapshot.sessions.filter(item => item.id.toLowerCase() === row.id.toLowerCase()).length !== 1
    || row.fullTitleTruncated || !exact(row.fullTitle, 256) || !exact(row.id, 256) || !exact(tab.path, 4096)) return null;
  if (tab.projectId !== null && (snapshot.projects.filter(project => project.id.toLowerCase() === tab.projectId!.toLowerCase()).length !== 1
    || snapshot.projects.filter(project => project.id === tab.projectId && project.path === tab.path && !project.archived).length !== 1)) return null;
  return Object.freeze({ expectedHostEpoch: epoch, scope: tab.projectId === null ? "global" : "project", projectId: tab.projectId,
    projectPath: tab.path, sessionId: row.id, confirmedTitle: row.fullTitle });
}

// Replaces selection with the result over *visible eligible* rows only. Over-cap means refusal, never truncation.
export function selectVisibleSessions(selected: readonly string[], visible: readonly string[], invert: boolean): string[] | null {
  const next = [...new Set(visible)].filter(id => !invert || !selected.includes(id));
  return next.length > sessionDeleteBatchLimit ? null : next;
}

export function createSessionBatchDeletion(invoke: (request: WorkspaceDeleteSessionRequest, options: { timeoutMilliseconds: number }) => Promise<WorkspaceDeleteSessionResponse>) {
  let state: State = { phase: "idle", items: [], message: "No batch requested." };
  let capture: { current: () => boolean; allowed: boolean; capability: Capability; epoch: string } | undefined;
  let notificationFailure: unknown;
  const listeners = new Set<() => void>();
  const publish = (next: State) => {
    state = next;
    for (const listener of [...listeners]) { try { listener(); } catch (error) { notificationFailure ??= error; } }
  };
  const current = (value: typeof capture) => { try { return !!value?.allowed && value.capability.canSubmit({ expectedEpoch: value.epoch }) && value.current(); } catch { return false; } };
  function invalidate() {
    const wasAllowed = capture?.allowed;
    if (capture) capture.allowed = false;
    if (state.phase === "review") publish({ phase: "idle", items: [], message: "Review invalidated. Select and review again; nothing requested." });
    else if (state.phase === "running" && wasAllowed) publish({ ...state, message: "Capture ended. The pending original is retained; no further requests will start." });
  }
  return {
    notificationFailure: () => notificationFailure,
    getSnapshot: () => state,
    subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; },
    locked: () => state.phase !== "idle",
    invalidate,
    review(requests: readonly Request[], isCurrent: () => boolean, capability: Capability) {
      if (state.phase !== "idle" || !requests.length || requests.length > sessionDeleteBatchLimit || !isCurrent()) return false;
      const first = requests[0];
      if (!capability?.canSubmit({ expectedEpoch: first.expectedHostEpoch })) return false;
      if (!/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/u.test(first.expectedHostEpoch)
        || new Set(requests.map(row => row.sessionId.toLowerCase())).size !== requests.length
        || requests.some(row => row.expectedHostEpoch !== first.expectedHostEpoch || row.scope !== first.scope || row.projectId !== first.projectId
          || row.projectPath !== first.projectPath || !exact(row.projectPath, 4096) || !exact(row.sessionId, 256) || !exact(row.confirmedTitle, 256)
          || row.scope !== "global" && row.scope !== "project" || row.scope === "global" && row.projectId !== null
          || row.scope === "project" && !exact(row.projectId, 256))) return false;
      capture = { current: isCurrent, allowed: true, capability, epoch: first.expectedHostEpoch };
      publish({ phase: "review", items: requests.map(request => ({ request: Object.freeze({ ...request }), outcome: "not-started" })), message: "Review every exact title, ID and path. No cascade; files outside session history are not deleted." });
      return true;
    },
    async confirm(confirmation: string) {
      if (state.phase !== "review" || confirmation !== `DELETE ${state.items.length}`) return;
      const original = capture;
      if (!current(original)) { invalidate(); return; }
      publish({ ...state, phase: "running", message: "Sequential deletion in progress. Dismissal does not cancel admitted work." });
      for (let index = 0; index < state.items.length; index++) {
        if (!current(original)) break;
        const request = state.items[index].request;
        const update = (outcome: BatchDeleteItem["outcome"], code?: string) => publish({ ...state,
          items: state.items.map((item, i) => i === index ? { request, outcome, code } : item) });
        update("pending");
        if (!current(original)) { update("not-started"); break; }
        try {
          const reply = await invoke(request, { timeoutMilliseconds: 10_000 });
          // Correlate before promoting host evidence. This contract has no runtime identity/status evidence.
          const correlated = reply?.scope === request.scope && reply.projectId === request.projectId
            && reply.projectPath === request.projectPath && reply.sessionId === request.sessionId;
          const hostEvidence = correlated && typeof reply.hostEpoch === "string"
            && /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/u.test(reply.hostEpoch)
            && reply.hostEpoch !== "00000000-0000-0000-0000-000000000000"
            && ["ok", "session_in_use", "has_children", "session_missing", "scope_missing", "busy", "closed", "stale_epoch", "delete_unconfirmed"].includes(reply.status);
          if (hostEvidence) {
            // Retain the original capability, never a current/replacement owner. Commit the stop before notification.
            if (reply.status === "stale_epoch" || reply.hostEpoch !== request.expectedHostEpoch) original!.allowed = false;
            try { original!.capability.observe({ status: reply.status, epoch: reply.hostEpoch }); }
            catch (error) { notificationFailure ??= error; }
          }
          if (hostEvidence && reply.status === "stale_epoch") { update("refused", "stale_epoch"); break; }
          const exactReply = reply?.hostEpoch === request.expectedHostEpoch && reply.scope === request.scope
            && reply.projectId === request.projectId && reply.projectPath === request.projectPath && reply.sessionId === request.sessionId;
          if (!exactReply) {
            if (reply?.hostEpoch === request.expectedHostEpoch && ["invalid_scope", "unconfigured"].includes(reply.status)
              && reply.scope === null && reply.projectId === null && reply.projectPath === null && reply.sessionId === null)
              update("refused", reply.status);
            else update("uncertain", "uncorrelated_response");
            break;
          }
          if (!hostEvidence) { update("uncertain", "uncorrelated_response"); break; }
          if (reply.status === "ok") update("deleted", "ok");
          else if (["session_in_use", "has_children", "session_missing", "scope_missing", "busy", "closed", "stale_epoch"].includes(reply.status)) {
            update("refused", reply.status);
            if (!["session_in_use", "has_children", "session_missing"].includes(reply.status)) break;
          } else { update("uncertain", "delete_unconfirmed"); break; }
        } catch { update("uncertain", "delete_unconfirmed"); break; }
      }
      publish({ ...state, phase: "settled", message: "Batch stopped. Results are per original request, not an atomic or durable transaction. No retry, continuation or automatic catalog refresh." });
    },
    clearSettled() {
      if (state.phase !== "settled" || state.items.some(item => item.outcome === "uncertain")) return false;
      capture = undefined; publish({ phase: "idle", items: [], message: "Settled report explicitly cleared. Any new deletion needs a new review." }); return true;
    },
  };
}
export type SessionBatchDeletion = ReturnType<typeof createSessionBatchDeletion>;
