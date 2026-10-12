import type { SessionSelection } from "#neoastra";

export const newSessionPreferencesKey = "codealta.desktop.newSessionPreferences.v1";
const limit = 256;
const identifier = (value: unknown): value is string => typeof value === "string" && value.length > 0 && value.length <= 256
  && value === value.trim() && !/[\u0000-\u001f\u007f-\u009f]/u.test(value);

/** Local creation preferences, not session authority; catalogs must validate them before use. */
export function restoreNewSessionPreferences(read: () => string | null): ReadonlyMap<string, SessionSelection> {
  const preferences = new Map<string, SessionSelection>();
  try {
    const raw = read();
    if (!raw || raw.length > 1_000_000) return preferences;
    const value: unknown = JSON.parse(raw);
    if (!value || typeof value !== "object" || Array.isArray(value)) return preferences;
    for (const [scope, stored] of Object.entries(value)) {
      if (preferences.size === limit || scope.length > 8192 || !stored || typeof stored !== "object") continue;
      const { providerKey, agentPromptId, modelId, reasoningEffort } = stored as SessionSelection;
      if (!identifier(providerKey) || !identifier(agentPromptId) || modelId !== null && !identifier(modelId)
        || reasoningEffort !== null && !identifier(reasoningEffort) || modelId === null && reasoningEffort !== null) continue;
      preferences.set(scope, { providerKey, agentPromptId, modelId, reasoningEffort });
    }
  } catch { /* Missing or unreadable storage leaves catalog defaults in use. */ }
  return preferences;
}

/** Remembers the last validated choices of each project (or Chats), bounding local history. */
export function rememberNewSessionPreference(preferences: ReadonlyMap<string, SessionSelection>, scope: string, selection: SessionSelection,
  write: (value: string) => void): ReadonlyMap<string, SessionSelection> {
  const next = new Map(preferences);
  next.delete(scope);
  next.set(scope, { ...selection });
  while (next.size > limit) next.delete(next.keys().next().value!);
  try { write(JSON.stringify(Object.fromEntries(next))); } catch { /* Choices still survive for this window. */ }
  return next;
}
