import type { SessionChoicesResponse, SessionSelection } from "#neoastra";
import { changeSelection, completeSelection, restoreSelection, validSelection } from "./sessionSelection";

export type CatalogNextSendTarget = Readonly<{ epoch: string; sessionId: string; providerKey: string; modelId: string; reasoningEffort: string | null }>;
export type NextSendAdmission = Readonly<{ epoch: string | null; sessionId: string | null; active: boolean; canMutate: boolean; pending: boolean }>;
export type NextSendResult = "applied" | "selection_changed" | "pending" | "unavailable" | "different_provider" | "stale_epoch";

/** What the host ran the session with when a selection was chosen. */
type SelectionBase = Readonly<{ agentPromptId: string; modelId: string | null; reasoningEffort: string | null; contextTokens?: number | null }>;

function baseOf(current: SessionSelection): SelectionBase {
  return { agentPromptId: current.agentPromptId, modelId: current.modelId, reasoningEffort: current.reasoningEffort, contextTokens: current.context?.tokens ?? null };
}

/**
 * Whether the host changed the session since a selection was chosen: the agent switched its own agent prompt (`alta
 * session set_agent`), a Send applied the selection, a tier gave another model. The session as the host has it then
 * wins over the selection, which is forgotten. A model or an effort the host did not name yet is no change.
 */
function hostChanged(base: SelectionBase, current: SessionSelection): boolean {
  return base.agentPromptId !== current.agentPromptId || base.modelId !== null && base.modelId !== current.modelId
    || base.reasoningEffort !== null && base.reasoningEffort !== current.reasoningEffort;
}

function sameSelection(a: SessionSelection, b: SessionSelection): boolean {
  return a.providerKey === b.providerKey && a.agentPromptId === b.agentPromptId && a.modelId === b.modelId
    && a.reasoningEffort === b.reasoningEffort && (a.permissionMode ?? null) === (b.permissionMode ?? null)
    && (a.context == null) === (b.context == null) && (a.context?.tokens ?? null) === (b.context?.tokens ?? null);
}

function readStored(raw: string | null): { selection: SessionSelection; base: SelectionBase; contextEdited: boolean } | null {
  const value = JSON.parse(raw ?? "null") as { selection?: SessionSelection; base?: SelectionBase; contextEdited?: boolean } | null;
  // A selection stored without what the host had then (an earlier version) cannot tell a change of the host: it is
  // not restored.
  return value && typeof value === "object" && value.selection && value.base && typeof value.base.agentPromptId === "string"
    ? { selection: value.selection, base: value.base, contextEdited: value.contextEdited === true } : null;
}

// Owned by the app instance: preferences survive a screen unmount and storage failures, not a host/provider identity change.
export function createNextSendSelectionStore(load: (key: string) => string | null, save: (key: string, value: string) => void) {
  const entries = new Map<string, { epoch: string; selection: SessionSelection; base: SelectionBase; contextEdited: boolean }>();
  const listeners = new Set<(value: { epoch: string; sessionId: string; choices: SessionChoicesResponse; selection: SessionSelection }) => void>();
  const key = (sessionId: string) => `codealta.desktop.selection.${sessionId}`;
  const forget = (sessionId: string) => {
    entries.delete(sessionId);
    try { save(key(sessionId), "null"); } catch { /* The host's change is still seen when it is restored. */ }
  };
  // Context alone may change in another window. Keep a deliberate unsent choice, but refresh a settled one.
  const reconcileContext = (entry: { selection: SessionSelection; base: SelectionBase; contextEdited: boolean }, current: SessionSelection) => {
    if ((entry.base.contextTokens ?? null) === (current.context?.tokens ?? null)) return entry;
    return { ...entry, base: baseOf(current), selection: entry.contextEdited ? entry.selection
      : { ...entry.selection, context: { tokens: current.context?.tokens ?? null } } };
  };
  return {
    subscribe(listener: (value: { epoch: string; sessionId: string; choices: SessionChoicesResponse; selection: SessionSelection }) => void) {
      listeners.add(listener);
      return () => { listeners.delete(listener); };
    },
    current(epoch: string, sessionId: string): SessionSelection | null {
      const entry = entries.get(sessionId);
      return entry?.epoch === epoch ? entry.selection : null;
    },
    get(epoch: string, sessionId: string, choices: SessionChoicesResponse): SessionSelection | null {
      if (choices.status !== "ok" || choices.epoch !== epoch || choices.sessionId !== sessionId || !choices.current) return null;
      let entry = entries.get(sessionId);
      if (entry?.epoch === epoch && entry.selection.providerKey !== choices.current.providerKey) {
        // The host moved the session to another provider (a fallback after a failure): a choice made for the former
        // one can never be sent, and is forgotten.
        entries.delete(sessionId);
        return null;
      }
      if (entry?.epoch === epoch) {
        if (hostChanged(entry.base, choices.current)) { forget(sessionId); return null; }
        const reconciled = reconcileContext(entry, choices.current);
        if (reconciled !== entry) {
          entry = { ...reconciled, epoch };
          entries.set(sessionId, entry);
          try { save(key(sessionId), JSON.stringify(entry)); } catch { /* The live selection remains authoritative. */ }
        }
        // Kept while the provider listed no model: it names one as soon as the provider does.
        const kept = completeSelection(choices, entry.selection);
        if (!validSelection(choices, kept)) return null;
        if (kept !== entry.selection) entries.set(sessionId, { ...entry, selection: kept });
        return kept;
      }
      let stored: ReturnType<typeof readStored> = null;
      try { stored = readStored(load(key(sessionId))); } catch { /* Nothing is restored. */ }
      if (!stored) return null;
      if (hostChanged(stored.base, choices.current)) { forget(sessionId); return null; }
      stored = reconcileContext(stored, choices.current);
      const restored = restoreSelection(() => JSON.stringify(stored.selection), choices);
      const tokens = stored.selection.context?.tokens;
      const unverified = !restored && (validSelection(choices, { ...stored.selection, context: { tokens: null } })
        || sameSelection({ ...stored.selection, context: choices.current.context }, choices.current))
        && (tokens === null || tokens !== undefined && Number.isSafeInteger(tokens) && tokens > 0) ? stored.selection : null;
      if (restored || unverified) {
        if (entries.size >= 128 && !entries.has(sessionId)) entries.delete(entries.keys().next().value!);
        entries.set(sessionId, { epoch, selection: (restored ?? unverified)!, base: stored.base, contextEdited: stored.contextEdited });
      }
      return restored;
    },
    set(epoch: string, sessionId: string, choices: SessionChoicesResponse, selection: SessionSelection): boolean {
      if (choices.status !== "ok" || choices.epoch !== epoch || choices.sessionId !== sessionId || !choices.current) return false;
      const previous = entries.get(sessionId);
      const existing = previous?.epoch === epoch ? previous.selection : choices.current;
      // Clearing just context is safe without model discovery. It is retained as an unsent choice, not authority to Send.
      const clearingContext = selection.context?.tokens === null && selection.providerKey === choices.current.providerKey
        && sameSelection({ ...selection, context: existing.context }, existing);
      if (!clearingContext && !validSelection(choices, selection)) return false;
      if (entries.size >= 128 && !entries.has(sessionId)) entries.delete(entries.keys().next().value!);
      const base = baseOf(choices.current);
      const contextEdited = previous?.epoch === epoch && previous.contextEdited
        || (selection.context?.tokens ?? null) !== (choices.current.context?.tokens ?? null);
      entries.set(sessionId, { epoch, selection, base, contextEdited });
      try { save(key(sessionId), JSON.stringify({ selection, base, contextEdited })); } catch { /* Keep the scoped selection in this app instance. */ }
      for (const listener of listeners) {
        try { listener({ epoch, sessionId, choices, selection }); } catch { /* A disconnected view cannot veto a validated selection. */ }
      }
      return true;
    },
    /**
     * A Send the host took with the selection kept for the session: the session runs with it now, and a later change
     * of the host is one made after that Send. Another selection (one chosen since) is left as it is.
     */
    sent(epoch: string, sessionId: string, selection: SessionSelection | null | undefined) {
      const entry = entries.get(sessionId);
      if (!selection || entry?.epoch !== epoch || entry.selection.providerKey !== selection.providerKey) return;
      const same = sameSelection(entry.selection, selection);
      // A context Send may finish after the user picked another context. Rebase that newer choice, do not erase it.
      if (!same && (hostChanged(entry.base, selection) || selection.context == null)) return;
      const base = baseOf(selection);
      const next = { ...entry, base, contextEdited: same ? false : entry.contextEdited };
      entries.set(sessionId, next);
      try { save(key(sessionId), JSON.stringify(next)); } catch { /* Kept in this app instance. */ }
    },
  };
}

export async function applyCatalogNextSend(target: CatalogNextSendTarget,
  admission: () => NextSendAdmission, readChoices: (epoch: string, sessionId: string) => Promise<SessionChoicesResponse>,
  selections: ReturnType<typeof createNextSendSelectionStore>): Promise<NextSendResult> {
  const check = (): NextSendResult | null => {
    const state = admission();
    if (!state.active) return "selection_changed";
    if (state.epoch !== target.epoch || !state.canMutate) return "stale_epoch";
    if (state.sessionId !== target.sessionId) return "selection_changed";
    if (state.pending) return "pending";
    return null;
  };
  const denied = check();
  if (denied) return denied;
  let choices: SessionChoicesResponse;
  try { choices = await readChoices(target.epoch, target.sessionId); }
  catch { return check() ?? "unavailable"; }
  const changed = check();
  if (changed) return changed;
  if (choices.status === "stale_epoch" || choices.epoch !== target.epoch) return "stale_epoch";
  if (choices.status !== "ok" || choices.sessionId !== target.sessionId || !choices.current) return "unavailable";
  if (choices.current.providerKey !== target.providerKey) return "different_provider";
  const stored = selections.get(target.epoch, target.sessionId, choices);
  // A newly reported catalog must not silently replace an already chosen model/prompt with host defaults.
  if (!stored && selections.current(target.epoch, target.sessionId)) return "selection_changed";
  const current = stored ?? choices.current;
  const next = changeSelection(choices, current, "modelId", target.modelId);
  const selected = next && target.reasoningEffort ? changeSelection(choices, next, "reasoningEffort", target.reasoningEffort) : next;
  if (!selected || !selections.set(target.epoch, target.sessionId, choices, selected)) return "unavailable";
  return "applied";
}
