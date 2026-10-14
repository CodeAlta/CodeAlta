import type { ConversationHeldEntry, ConversationHeldHandle, ConversationHeldListRequest, ConversationHeldPage,
  ConversationHeldResolveRequest, ConversationHeldResult } from "#neoastra";
import type { MessageKey } from "../localization";

/** What the user can answer when the conversation of a run is held by another process, in the order of the card. */
export type HeldChoice = "wait" | "continueAsCopy" | "openAnyway" | "cancel";
export type HeldState =
  | { kind: "loading" }
  | { kind: "ready"; entries: readonly ConversationHeldEntry[]; notice: HeldNotice | null }
  | { kind: "resolving"; choice: HeldChoice }
  | { kind: "error"; code: string; reloadRequired: boolean };
/** Why the list was read again after an answer: the answer was not taken, or nobody knows whether it was. */
export type HeldNotice = "rejected" | "uncertain" | "invalid_request";
type CallOptions = { signal: AbortSignal; timeoutMilliseconds: number };
type List = (request: ConversationHeldListRequest, options: CallOptions) => Promise<ConversationHeldPage>;
type Resolve = (request: ConversationHeldResolveRequest, options: CallOptions) => Promise<ConversationHeldResult>;

const choices: readonly HeldChoice[] = ["wait", "continueAsCopy", "openAnyway", "cancel"];

/**
 * The questions of the runs of a session whose Claude Code conversation another process holds (VS Code, a terminal).
 * A selection reads them for one host epoch and session; an answer names the exact question it was shown. An answer
 * that is not taken, or whose fate is not known, is never sent again: the list is read again and shows what still waits.
 * A host epoch that changed asks for a reload, for good.
 */
export function createConversationHeldReviewer(list: List, resolve: Resolve) {
  let selection = 0;
  let reloadCode: string | null = null;
  return {
    forSelection(epoch: string, sessionId: string, signal: AbortSignal, publish: (state: HeldState) => void) {
      const selected = ++selection;
      const request = Object.freeze({ expectedHostEpoch: epoch, sessionId });
      let generation = 0;
      let entries: readonly ConversationHeldEntry[] = [];
      let answering = false;
      const active = () => !signal.aborted && selected === selection;
      const fail = (code: string) => {
        if (code === "stale_epoch") reloadCode = code;
        entries = [];
        if (active()) publish({ kind: "error", code: reloadCode ?? code, reloadRequired: reloadCode !== null });
      };
      async function refresh(notice: HeldNotice | null = null): Promise<void> {
        if (!active() || answering) return;
        if (reloadCode) { fail(reloadCode); return; }
        const current = ++generation;
        publish({ kind: "loading" });
        try {
          const page = await list(request, { signal, timeoutMilliseconds: 8_000 });
          if (!active() || current !== generation) return;
          if (page?.status === "stale_epoch" || page?.hostEpoch !== epoch) { fail("stale_epoch"); return; }
          if (page.status !== "ok") { fail(page.status); return; }
          if (page.sessionId !== sessionId || !Array.isArray(page.entries) || page.entries.length > 4
            || page.entries.some(entry => !validEntry(entry, sessionId))) { fail("invalid_response"); return; }
          entries = Object.freeze(page.entries.map(entry => Object.freeze({ ...entry, handle: Object.freeze({ ...entry.handle }) })));
          publish({ kind: "ready", entries, notice });
        } catch { if (active() && current === generation) fail("read_failed"); }
      }
      return {
        refresh: () => refresh(),
        /** Answers a shown question once. Whatever the reply, the list is read again; nothing is sent twice. */
        async answer(entry: ConversationHeldEntry, choice: HeldChoice): Promise<void> {
          if (!active() || answering || reloadCode || !entries.includes(entry) || !offered(entry).includes(choice)) return;
          answering = true;
          ++generation;
          entries = [];
          publish({ kind: "resolving", choice });
          let notice: HeldNotice | null = null;
          try {
            const result = await resolve({ expectedHostEpoch: epoch, handle: entry.handle, choice }, { signal, timeoutMilliseconds: 8_000 });
            if (result?.status === "stale_epoch" || (result && result.hostEpoch !== epoch)) reloadCode = "stale_epoch";
            else if (result?.status === "resolved" && sameHandle(result.handle, entry.handle)) notice = null;
            else notice = result?.status === "rejected" || result?.status === "invalid_request" ? result.status : "uncertain";
          } catch { notice = "uncertain"; }
          answering = false;
          await refresh(notice);
        },
      };
    },
  };
}

/** The choices a question offers that this page knows, in its order, Cancel last. */
export function offered(entry: ConversationHeldEntry): HeldChoice[] {
  const known = entry.choices.filter((choice): choice is HeldChoice => (choices as readonly string[]).includes(choice));
  return [...known.filter(choice => choice !== "cancel"), ...(known.includes("cancel") ? ["cancel" as const] : [])];
}

/** The label of each choice, as its button says it. */
export const choiceLabels: Readonly<Record<HeldChoice, MessageKey>> = Object.freeze({
  wait: "Wait until it is free",
  continueAsCopy: "Continue as a copy",
  openAnyway: "Open here anyway",
  cancel: "Cancel",
});

/**
 * Where the other process runs, as the card says it: a known origin in the language of the window, otherwise the
 * words of the host (which name an unknown application by how it was started).
 */
export function originKey(origin: string, originText: string): MessageKey | null {
  switch (origin) {
    case "vsCode": return "VS Code";
    case "terminal": return "a terminal";
    case "backgroundSession": return "a background session of Claude Code";
    case "otherApplication": return originText === "another application" ? "another application" : null;
    default: return originText === "another Claude process" ? "another Claude process" : null;
  }
}

/** The same for the wait, which only knows the words of the host. */
export function originKeyFromText(originText: string): MessageKey | null {
  const known: readonly MessageKey[] = ["VS Code", "a terminal", "a background session of Claude Code", "another application", "another Claude process"];
  return known.find(key => key === originText) ?? null;
}

/** What the card says below its question: the process and its folder, when the registry names them. */
export function holderDetail(entry: ConversationHeldEntry): { processId: number | null; workingDirectory: string | null } {
  return { processId: entry.holder.processId, workingDirectory: entry.holder.workingDirectory?.trim() || null };
}

function sameHandle(a: ConversationHeldHandle | null | undefined, b: ConversationHeldHandle): boolean {
  return !!a && a.runtimeInstanceId === b.runtimeInstanceId && a.attachmentGeneration === b.attachmentGeneration
    && a.sessionId === b.sessionId && a.runId === b.runId && a.requestId === b.requestId && a.attemptId === b.attemptId;
}
function guid(value: unknown): value is string {
  return typeof value === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(value)
    && value !== "00000000-0000-0000-0000-000000000000";
}
function identity(value: unknown): value is string {
  return typeof value === "string" && value.length > 0 && value.length <= 128 && value === value.trim() && !/[\u0000-\u001f\u007f-\u009f]/.test(value);
}
function validEntry(entry: ConversationHeldEntry, sessionId: string): boolean {
  const handle = entry?.handle;
  return !!handle && handle.sessionId === sessionId && guid(handle.runtimeInstanceId) && guid(handle.attemptId)
    && identity(handle.requestId) && (handle.runId === null || identity(handle.runId))
    && typeof handle.attachmentGeneration === "string" && /^[1-9][0-9]{0,15}$/.test(handle.attachmentGeneration)
    && identity(entry.providerId) && identity(entry.conversationId) && !!entry.holder && typeof entry.holder.originText === "string"
    && typeof entry.holder.origin === "string" && Array.isArray(entry.choices) && offered(entry).length > 0;
}
