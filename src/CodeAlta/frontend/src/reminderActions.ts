import type { ReminderCreateRequest, ReminderDeleteRequest, ReminderMutationResponse } from "#neoastra";

export type ReminderTarget = { epoch: string; sessionId: string };
type Mutation = { pending: boolean; hold: boolean; message: string;
  request?: ReminderCreateRequest | ReminderDeleteRequest; kind?: "create" | "delete" };

// Retained for the lifetime of the desktop view, including session switches and unmounts.
// An uncertain admission is never resubmitted automatically; a later list is only observation.
export function createReminderActions(
  create: (request: ReminderCreateRequest, options: { timeoutMilliseconds: number }) => Promise<ReminderMutationResponse>,
  remove: (request: ReminderDeleteRequest, options: { timeoutMilliseconds: number }) => Promise<ReminderMutationResponse>,
  observe?: (target: ReminderTarget, result: ReminderMutationResponse) => void,
) {
  const entries = new Map<string, Mutation>();
  const listeners = new Set<() => void>();
  const key = (target: ReminderTarget) => JSON.stringify([target.epoch, target.sessionId]);
  const publish = (target: ReminderTarget, entry: Mutation) => { entries.set(key(target), entry); listeners.forEach(listener => listener()); };
  return {
    subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; },
    get(target: ReminderTarget) { return entries.get(key(target)); },
    isFull(target: ReminderTarget) { return !entries.has(key(target)) && entries.size >= 256 &&
      [...entries.values()].every(item => item.pending || item.hold); },
    async submit(target: ReminderTarget, request: ReminderCreateRequest | ReminderDeleteRequest, kind: "create" | "delete") {
      if (entries.get(key(target))?.pending || entries.get(key(target))?.hold) return false;
      if (!entries.has(key(target)) && entries.size >= 256) {
        const settled = [...entries].find(([, item]) => !item.pending && !item.hold);
        if (settled) entries.delete(settled[0]);
        else return false; // All slots contain exact pending/uncertain evidence; do not evict it.
      }
      publish(target, { pending: true, hold: false, request, kind, message: `${kind} admission pending for this session.` });
      try {
        const result = kind === "create"
          ? await create(request as ReminderCreateRequest, { timeoutMilliseconds: 15000 })
          : await remove(request as ReminderDeleteRequest, { timeoutMilliseconds: 15000 });
        try { observe?.(target, result); } catch { /* An observer cannot turn committed admission into a retry. */ }
        if (result.epoch !== target.epoch || result.sessionId !== target.sessionId ||
          result.status === "unconfirmed" || result.status === "ok" && !result.reminderId ||
          kind === "delete" && result.status === "ok" && result.reminderId !== (request as ReminderDeleteRequest).reminderId) {
          publish(target, { pending: false, hold: true, request, kind, message: "Admission is uncertain. Inspect the exact session's list; no automatic retry." });
          return false;
        }
        const ok = result.status === "ok";
        publish(target, { pending: false, hold: false, message: ok
          ? `Reminder ${kind === "create" ? "created" : "deleted"} (${result.reminderId}). Refresh to see current counts.`
          : `${kind} was not accepted (${result.status}).` });
        return ok;
      } catch {
        publish(target, { pending: false, hold: true, request, kind, message: "Admission response was lost. Inspect the exact session's list; no automatic retry." });
        return false;
      }
    },
  };
}
