import type { SessionChoicesResponse, SessionSelection } from "#neoastra";
import { changeSelection, restoreSelection, validSelection } from "./sessionSelection";

export type CatalogNextSendTarget = Readonly<{ epoch: string; sessionId: string; providerKey: string; modelId: string; reasoningEffort: string | null }>;
export type NextSendAdmission = Readonly<{ epoch: string | null; sessionId: string | null; active: boolean; canMutate: boolean; pending: boolean }>;
export type NextSendResult = "applied" | "selection_changed" | "pending" | "unavailable" | "different_provider" | "stale_epoch";

// Owned by the app instance: preferences survive a screen unmount and storage failures, not a host/provider identity change.
export function createNextSendSelectionStore(load: (key: string) => string | null, save: (key: string, value: string) => void) {
  const entries = new Map<string, { epoch: string; selection: SessionSelection }>();
  const key = (sessionId: string) => `codealta.desktop.selection.${sessionId}`;
  return {
    get(epoch: string, sessionId: string, choices: SessionChoicesResponse): SessionSelection | null {
      if (choices.status !== "ok" || choices.epoch !== epoch || choices.sessionId !== sessionId || !choices.current) return null;
      const entry = entries.get(sessionId);
      if (entry?.epoch === epoch) return validSelection(choices, entry.selection) ? entry.selection : null;
      return restoreSelection(() => load(key(sessionId)), choices);
    },
    set(epoch: string, sessionId: string, choices: SessionChoicesResponse, selection: SessionSelection): boolean {
      if (choices.status !== "ok" || choices.epoch !== epoch || choices.sessionId !== sessionId || !choices.current
        || !validSelection(choices, selection)) return false;
      if (entries.size >= 128 && !entries.has(sessionId)) entries.delete(entries.keys().next().value!);
      entries.set(sessionId, { epoch, selection });
      try { save(key(sessionId), JSON.stringify(selection)); } catch { /* Keep the scoped selection in this app instance. */ }
      return true;
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
  const current = selections.get(target.epoch, target.sessionId, choices) ?? choices.current;
  const next = changeSelection(choices, current, "modelId", target.modelId);
  const selected = next && target.reasoningEffort ? changeSelection(choices, next, "reasoningEffort", target.reasoningEffort) : next;
  if (!selected || !selections.set(target.epoch, target.sessionId, choices, selected)) return "unavailable";
  return "applied";
}
