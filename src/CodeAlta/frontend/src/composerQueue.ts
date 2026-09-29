import type { SessionQueueRequest, SessionSteerRequest, SessionReceiptPage, SessionReceiptView } from "#neoastra";
import { createOwnerChangeSignal } from "./ownerChangeSignal";

export type ComposerQueueItem = Readonly<{
  id: string; kind: "Queue" | "Steer"; request: Readonly<SessionQueueRequest | SessionSteerRequest>;
  count: number; state: "waiting" | "sending" | "submitted" | "uncertain" | "failed"; code?: string;
}>;

// App-owned unsent drafts. Once claimed, text/target/key are immutable until a correlated
// receipt settles them. Closing a panel cannot turn an uncertain request into a fresh send.
export function createComposerQueue() {
  const change = createOwnerChangeSignal();
  const items = new Map<string, ComposerQueueItem>();
  function put(item: ComposerQueueItem) { items.set(item.id, Object.freeze(item)); change.changed(); }
  function settle(item: ComposerQueueItem, row: SessionReceiptView) {
    if (row.clientRequestId !== item.request.clientRequestId || row.sessionId !== item.request.sessionId || row.kind !== item.kind) return;
    if (row.state !== "terminal") { if (item.state !== "submitted") put({ ...item, state: "submitted" }); return; }
    if (row.outcome === "Completed") {
      if (item.kind === "Queue" && item.count > 1) put({ ...item, count: item.count - 1, state: "waiting",
        request: Object.freeze({ ...item.request, clientRequestId: crypto.randomUUID() }) });
      else { items.delete(item.id); change.changed(); }
    } else if (row.outcome === "Cancelled") { items.delete(item.id); change.changed(); }
    else if (item.state !== "failed") put({ ...item, state: "failed", code: row.code ?? "failed" });
  }
  return {
    subscribe: change.subscribe, getSnapshot: change.getSnapshot,
    list: (epoch: string, sessionId: string) => [...items.values()].filter(item => item.request.expectedEpoch === epoch && item.request.sessionId === sessionId),
    add(kind: ComposerQueueItem["kind"], request: SessionQueueRequest | SessionSteerRequest) {
      if (items.size >= 256 || !request.text.trim() || request.text.length > 32768 || items.has(request.clientRequestId)) return false;
      put({ id: request.clientRequestId, kind, request: Object.freeze({ ...request }), count: 1, state: "waiting" }); return true;
    },
    edit(original: ComposerQueueItem, text: string, count = original.count) {
      if (items.get(original.id) !== original || original.state !== "waiting" || !text.trim() || text.length > 32768
        || !Number.isSafeInteger(count) || count < 1 || count > 2147483647) return false;
      put({ ...original, request: Object.freeze({ ...original.request, text }), count }); return true;
    },
    remove(original: ComposerQueueItem) {
      if (items.get(original.id) !== original || !["waiting", "failed"].includes(original.state)) return false;
      items.delete(original.id); change.changed(); return true;
    },
    claim(original: ComposerQueueItem) {
      if (items.get(original.id) !== original || original.state !== "waiting") return false;
      put({ ...original, state: "sending" }); return true;
    },
    fail(original: ComposerQueueItem, code: string) {
      if (items.get(original.id) === original) put({ ...original, state: "failed", code });
    },
    outcome(id: string, result: { status: string; epoch: string | null; receipt: SessionReceiptView | null }) {
      const item = items.get(id); if (!item) return;
      if (result.epoch === item.request.expectedEpoch && ["accepted", "replay"].includes(result.status) && result.receipt) settle(item, result.receipt);
      else put({ ...item, state: ["conflict", "busy", "capacity", "closed", "invalid_request"].includes(result.status) ? "failed" : "uncertain", code: result.status });
    },
    reconcile(page: SessionReceiptPage) {
      if (page.status !== "ok") return;
      for (const item of [...items.values()]) {
        if (item.request.expectedEpoch !== page.epoch || item.state === "waiting" || item.state === "failed") continue;
        const row = page.rows.find(row => row.clientRequestId === item.request.clientRequestId && row.sessionId === item.request.sessionId && row.kind === item.kind);
        if (row) settle(item, row);
      }
    },
  };
}
