import { draftStorageKey } from "./promptDraft";
import { restoreSessionTabs, sessionTabsKey } from "./sessionTabs";

/** The read-only part of Storage; the accessor itself may throw when storage is denied. */
export type RetentionStorage = Pick<Storage, "length" | "key" | "getItem">;
export type RetentionReading = Readonly<{ sessionIds: readonly string[]; complete: boolean }>;
/** sessionRetention v1; kept structural until the host generates this contract. */
export type RetentionPublication = Readonly<{
  expectedEpoch: string; pageId: string; revision: number; sessionIds: string[]; ready: boolean;
}>;
export type RetentionReply = Readonly<{ accepted: boolean; reason: string | null }>;
const identifier = (value: unknown): value is string => typeof value === "string" && value.trim().length > 0 && value.length <= 256;

/**
 * Read every persisted space, including ones absent from the current catalog. Presentation restore helpers
 * deliberately conflate malformed and missing state; retention must distinguish them and fail closed.
 * Images are not persisted today: their owner supplies all in-memory keys, including offscreen drafts/reads.
 * Scope-local drafts (text or images) have no saved session ID until the explicit creation handoff.
 */
export function collectSessionRetention(getStorage: () => RetentionStorage, imageKeys: readonly string[] = []): RetentionReading {
  const ids = new Set<string>();
  let complete = true;
  const add = (id: unknown) => { if (identifier(id)) ids.add(id); else complete = false; };
  try {
    const store = getStorage();
    const count = store.length;
    const keys = new Set<string>();
    const values = new Map<string, string | null>();
    const draftPrefix = draftStorageKey("");
    const localPrefix = "codealta.desktop.localPrompt.";
    for (let index = 0; index < count; index++) {
      const key = store.key(index);
      if (key === null || keys.has(key)) { complete = false; continue; }
      keys.add(key);
      const tabState = key === sessionTabsKey || key.startsWith(`${sessionTabsKey}.`);
      const draft = key.startsWith(draftPrefix), localDraft = key.startsWith(localPrefix);
      if (!tabState && !draft && !localDraft) continue;
      try {
        const raw = store.getItem(key);
        values.set(key, raw);
        if (raw === null) { complete = false; continue; }
        if (tabState) {
          const restored = restoreSessionTabs(() => raw);
          if (restored) for (const tab of restored.open) add(tab.sessionId);
          else {
            complete = false;
            // Preserve recoverable identities even when another field makes restoration ambiguous.
            if (raw.length <= 65536) {
              const partial: unknown = JSON.parse(raw);
              if (partial && typeof partial === "object" && "open" in partial && Array.isArray(partial.open))
                for (const tab of partial.open) if (tab && typeof tab === "object" && "sessionId" in tab) add(tab.sessionId);
            }
          }
        } else {
          if (raw.length > 32768) complete = false;
          if (draft) {
            const id = key.slice(draftPrefix.length);
            if (!identifier(id)) complete = false;
            else if (raw.length) ids.add(id);
          }
          else {
            const scope: unknown = JSON.parse(key.slice(localPrefix.length));
            if (scope !== null && !identifier(scope)) complete = false;
          }
        }
      } catch { complete = false; }
    }
    // A concurrent window changing storage during enumeration is not a complete reading.
    if (store.length !== count) complete = false;
    for (let index = 0; index < count; index++) if (!keys.delete(store.key(index) ?? "")) complete = false;
    for (const [key, raw] of values) if (store.getItem(key) !== raw) complete = false;
  } catch { complete = false; }
  for (const key of imageKeys) {
    try {
      const parts: unknown = JSON.parse(key);
      if (!Array.isArray(parts) || parts.length !== 4 || !identifier(parts[0])
        || parts[2] !== null && !identifier(parts[2]) || parts[3] !== null && typeof parts[3] !== "string") { complete = false; continue; }
      if (parts[0] !== "local-draft") add(parts[1]);
      else if (parts[1] !== null && !identifier(parts[1])) complete = false;
    } catch { complete = false; }
  }
  return { sessionIds: [...ids].sort(), complete };
}

/**
 * One page-lifetime owner. Publications are ordered and claims only grow: losing storage, closing a tab or
 * disconnecting never releases IDs. A new epoch invalidates acknowledgements, not the accumulated set.
 *
 * Restoration is a separate admission: a complete inventory alone cannot finish it. The shell supplies
 * its initial selection and exposes it only after that publication is accepted. The host independently
 * registers windows before loading pages; an unload cannot reliably deliver a final RPC.
 */
export function createSessionRetention(publish: (request: RetentionPublication) => Promise<RetentionReply>,
  read: () => RetentionReading, pageId: string = crypto.randomUUID()) {
  const ids = new Set<string>();
  const acknowledged = new Set<string>();
  const listeners = new Set<() => void>();
  let snapshot = 0;
  const changed = () => { snapshot++; for (const listener of [...listeners]) listener(); };
  let epoch: string | null = null, generation = 0, revision = 0, complete = true;
  let suspended = false;
  let restored = false;
  let pending: Promise<unknown> = Promise.resolve();
  const collect = () => {
    try {
      const reading = read();
      complete &&= reading.complete;
      for (const id of reading.sessionIds) { if (identifier(id)) ids.add(id); else complete = false; }
    } catch { complete = false; }
  };
  // Capture before any presentation restore/reconciliation can overwrite ambiguous persisted state.
  collect();
  const invalidate = () => { suspended = true; restored = false; generation++; epoch = null; acknowledged.clear(); changed(); };
  function enqueue(expectedEpoch: string, wanted: readonly string[], current: () => boolean, expose: () => void, restoration = false): Promise<boolean> {
    if (suspended || !identifier(expectedEpoch) || wanted.some(id => !identifier(id))) return Promise.resolve(false);
    if (epoch !== expectedEpoch) { generation++; restored = false; epoch = expectedEpoch; acknowledged.clear(); changed(); }
    const captured = generation;
    for (const id of wanted) ids.add(id);
    const result = pending.then(async () => {
      if (captured !== generation || !current()) return false;
      collect();
      const sessionIds = [...ids].sort();
      let reply: RetentionReply;
      try {
        reply = await publish({ expectedEpoch, pageId, revision: ++revision, sessionIds, ready: complete && (restored || restoration) });
      } catch {
        // A failed publication adds nothing, and takes nothing back: the host's claims only grow, so what it
        // acknowledged before still holds. Clearing them would hold every pane after one failed call.
        return false;
      }
      if (reply?.accepted !== true || captured !== generation || !current()) return false;
      if (restoration) restored = true;
      const claimed = acknowledged.size;
      for (const id of sessionIds) acknowledged.add(id);
      // The window renders again when a claim is added: a publication that adds none, as most do, changes nothing it shows.
      if (acknowledged.size !== claimed) changed();
      // Subscribers may synchronously unmount/change the originating lifetime.
      if (captured !== generation || !current()) return false;
      expose();
      return true;
    });
    pending = result.catch(() => {});
    return result;
  }
  return {
    pageId,
    subscribe: (listener: () => void) => { listeners.add(listener); return () => { listeners.delete(listener); }; },
    getSnapshot: () => snapshot,
    invalidate,
    /** Only an observed host connection can resume after loss; a draft/storage notification cannot. */
    connect: (expectedEpoch: string) => { suspended = false; return enqueue(expectedEpoch, [], () => true, () => {}); },
    storageComplete: () => complete,
    hasClaim: (expectedEpoch: string, id: string) => epoch === expectedEpoch && acknowledged.has(id),
    publish: (expectedEpoch: string) => enqueue(expectedEpoch, [], () => true, () => {}),
    /** Initial restoration (or reconnect) supplies its selection before certifying a complete inventory. */
    restore: (expectedEpoch: string, wanted: readonly string[], current: () => boolean, expose: () => void) => enqueue(expectedEpoch, wanted, current, expose, true),
    /** No tab, draft handoff or work is exposed if retirement won or the lifetime changed while waiting. */
    admit: enqueue,
  };
}

export type SessionRetention = ReturnType<typeof createSessionRetention>;
