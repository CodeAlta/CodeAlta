import type { SessionReceiptPage, SessionSendRequest, SessionSteerRequest } from "#neoastra";
import { createOwnerChangeSignal } from "./ownerChangeSignal";

type QueueImages = NonNullable<SessionSendRequest["images"]>;

/**
 * A prompt waiting above the composer. A queued prompt is sent, as a normal Send, when the session is idle;
 * a steering prompt goes to the running turn and stays listed until the agent takes it up.
 */
export type ComposerQueueItem = Readonly<{
  id: string; kind: "Queue" | "Steer"; epoch: string; sessionId: string; text: string;
  /** Images of a queued prompt; steering carries text only. */
  images: QueueImages | null;
  /** How many times a queued prompt is still to be sent. */
  count: number;
  /**
   * waiting: not sent, still editable. sending: its request is on its way. delivering: a steering prompt the
   * host accepted and the agent has not taken up yet. uncertain: the host did not confirm the request, which
   * is only ever retried with its own key. failed: refused for a reason that waiting does not solve.
   */
  state: "waiting" | "sending" | "delivering" | "uncertain" | "failed";
  /** The request of the current attempt, fixed once claimed: a retry cannot become a second prompt. */
  request?: Readonly<SessionSendRequest | SessionSteerRequest>;
  code?: string;
}>;

const maximumItems = 256;
const maximumText = 32768;
const maximumCount = 999;
const maximumImagePrompts = 8;

function validText(text: string, images: QueueImages | null): boolean {
  return text.length <= maximumText && (!!text.trim() || !!images?.length);
}

/** The count a repeat field may hold: whole, at least one. */
export function clampQueueCount(value: number): number {
  return Number.isFinite(value) ? Math.min(maximumCount, Math.max(1, Math.trunc(value))) : 1;
}

/** One line for a row: the prompt with its line breaks and runs of blanks folded, as the terminal UI shows it. */
export function queuePreview(text: string): string {
  return text.trim().replace(/\s+/gu, " ");
}

// App-owned and in memory only: the rows outlive a closed tab, not a reload.
export function createComposerQueue() {
  const change = createOwnerChangeSignal();
  let items: ComposerQueueItem[] = [];
  // Rows open in the editor are not sent under the user's hands.
  const held = new Set<string>();
  const current = (item: ComposerQueueItem) => items.includes(item);
  function replace(item: ComposerQueueItem, next: ComposerQueueItem | null) {
    items = next ? items.map(value => value === item ? Object.freeze(next) : value) : items.filter(value => value !== item);
    if (!next) held.delete(item.id);
    change.changed();
  }
  // After its last send a queued prompt leaves the list; before that it waits for the next idle moment.
  function sent(item: ComposerQueueItem) {
    if (item.kind === "Steer") replace(item, { ...item, state: "delivering", code: undefined });
    else if (item.count > 1) replace(item, { ...item, count: item.count - 1, state: "waiting", request: undefined, code: undefined });
    else replace(item, null);
  }
  // Steering that cannot reach a turn is not lost: it becomes the next prompt of the queue.
  function requeue(item: ComposerQueueItem) {
    const next = Object.freeze({ ...item, kind: "Queue" as const, state: "waiting" as const, count: 1, request: undefined, code: undefined });
    items = [next, ...items.filter(value => value !== item)];
    change.changed();
  }
  return {
    subscribe: change.subscribe, getSnapshot: change.getSnapshot,
    /** Never aborted: a request of the queue is not tied to a mounted composer, so its outcome always reaches the list. */
    signal: new AbortController().signal,
    /** The rows of a session, steering first: it reaches the agent before any queued prompt. */
    list(epoch: string, sessionId: string): readonly ComposerQueueItem[] {
      const own = items.filter(item => item.epoch === epoch && item.sessionId === sessionId);
      return [...own.filter(item => item.kind === "Steer"), ...own.filter(item => item.kind === "Queue")];
    },
    add(kind: ComposerQueueItem["kind"], epoch: string, sessionId: string, text: string, images: QueueImages | null = null): ComposerQueueItem | null {
      const attached = kind === "Queue" && images?.length ? images : null;
      if (items.length >= maximumItems || !validText(text, attached)
        || attached && items.filter(item => item.images).length >= maximumImagePrompts) return null;
      const item: ComposerQueueItem = Object.freeze({ id: crypto.randomUUID(), kind, epoch, sessionId, text, images: attached, count: 1, state: "waiting" });
      items = [...items, item];
      change.changed();
      return item;
    },
    edit(item: ComposerQueueItem, text: string, count = item.count): boolean {
      if (!current(item) || item.kind !== "Queue" || !["waiting", "failed"].includes(item.state) || !validText(text, item.images)
        || !Number.isSafeInteger(count) || count < 1 || count > maximumCount) return false;
      replace(item, { ...item, text, count });
      return true;
    },
    /** A sent steering prompt can only be taken off the list: the agent may already hold it. */
    remove(item: ComposerQueueItem): boolean {
      if (!current(item) || item.state === "sending" || item.state === "uncertain") return false;
      replace(item, null);
      return true;
    },
    /** Removes every queued prompt that has not left yet; steering stays. */
    clear(epoch: string, sessionId: string): void {
      const before = items.length;
      items = items.filter(item => !(item.epoch === epoch && item.sessionId === sessionId && item.kind === "Queue" && ["waiting", "failed"].includes(item.state)));
      if (items.length !== before) change.changed();
    },
    hold(item: ComposerQueueItem, editing: boolean): void {
      if (editing ? held.has(item.id) : !held.has(item.id)) return;
      if (editing) held.add(item.id); else held.delete(item.id);
      change.changed();
    },
    isHeld: (item: ComposerQueueItem) => held.has(item.id),
    claim(item: ComposerQueueItem, request: NonNullable<ComposerQueueItem["request"]>): ComposerQueueItem | null {
      if (!current(item) || item.state !== "waiting" || held.has(item.id)) return null;
      const next = Object.freeze({ ...item, state: "sending" as const, request, code: undefined });
      replace(item, next);
      return items.find(value => value.id === item.id) ?? null;
    },
    /** The host was busy: the row waits for the next idle moment with a fresh request. */
    release(id: string): void {
      const item = items.find(value => value.id === id);
      if (item && (item.state === "sending" || item.state === "failed")) replace(item, { ...item, state: "waiting", request: undefined, code: undefined });
    },
    sent(id: string): void { const item = items.find(value => value.id === id); if (item) sent(item); },
    delivered(id: string): void { const item = items.find(value => value.id === id); if (item?.kind === "Steer") replace(item, null); },
    uncertain(id: string, code: string): void {
      const item = items.find(value => value.id === id);
      if (item) replace(item, { ...item, state: "uncertain", code });
    },
    fail(id: string, code: string): void {
      const item = items.find(value => value.id === id);
      if (item) replace(item, { ...item, state: "failed", request: undefined, code });
    },
    requeue(id: string): void { const item = items.find(value => value.id === id); if (item?.kind === "Steer") requeue(item); },
    /** Sends a queued prompt to the running turn instead: one send of it leaves the queue. */
    steerNow(item: ComposerQueueItem): boolean {
      if (!current(item) || item.kind !== "Queue" || item.state !== "waiting" || item.images || !item.text.trim()) return false;
      const steer: ComposerQueueItem = Object.freeze({ id: crypto.randomUUID(), kind: "Steer", epoch: item.epoch, sessionId: item.sessionId,
        text: item.text, images: null, count: 1, state: "waiting" });
      items = item.count > 1 ? items.map(value => value === item ? Object.freeze({ ...item, count: item.count - 1 }) : value) : items.filter(value => value !== item);
      items = [...items, steer];
      change.changed();
      return true;
    },
    /** Receipts settle what an admission left open: an unconfirmed request, or steering the runtime refused. */
    reconcile(page: SessionReceiptPage): void {
      if (page.status !== "ok") return;
      for (const item of [...items]) {
        const request = item.request;
        if (!request || item.epoch !== page.epoch || item.state !== "uncertain" && item.state !== "delivering") continue;
        const row = page.rows.find(row => row.clientRequestId === request.clientRequestId && row.sessionId === item.sessionId
          && row.kind === (item.kind === "Queue" ? "Send" : "Steer"));
        if (!row) continue;
        if (item.kind === "Steer" && row.state === "terminal" && row.outcome !== "Completed") requeue(item);
        else if (item.state === "uncertain") sent(item);
      }
    },
  };
}
