import type { SessionSelection } from "#neoastra";
import { defaultModelId, defaultReasoningEffort } from "./newSessionDefaults";

export const newSessionPreferencesKey = "codealta.desktop.newSessionPreferences.v1";
const limit = 256;
const fields = ["providerKey", "agentPromptId", "modelId", "reasoningEffort"] as const;
const identifier = (value: unknown): value is string => typeof value === "string" && value.length > 0 && value.length <= 256
  && value === value.trim() && !/[\u0000-\u001f\u007f-\u009f]/u.test(value);

export type NewSessionChoiceField = typeof fields[number];
/**
 * What the user chose for the new sessions of a project (or of the chats). A choice that was never made is
 * absent: it follows the defaults of the settings, also when they change later.
 */
export type NewSessionPreference = Readonly<Partial<Record<NewSessionChoiceField, string>>>;

/** Local creation preferences, not session authority; catalogs must validate them before use. */
export function restoreNewSessionPreferences(read: () => string | null): ReadonlyMap<string, NewSessionPreference> {
  const preferences = new Map<string, NewSessionPreference>();
  try {
    const raw = read();
    if (!raw || raw.length > 1_000_000) return preferences;
    const value: unknown = JSON.parse(raw);
    if (!value || typeof value !== "object" || Array.isArray(value)) return preferences;
    for (const [scope, stored] of Object.entries(value as Record<string, Record<string, unknown> | null>)) {
      if (preferences.size === limit || scope.length > 8192 || !stored || typeof stored !== "object") continue;
      const chosen = fields.filter(field => stored[field] != null);
      // A model is one of a provider: without its provider it says nothing.
      if (chosen.length === 0 || chosen.some(field => !identifier(stored[field])) || chosen.includes("modelId") && !chosen.includes("providerKey")) continue;
      preferences.set(scope, Object.fromEntries(chosen.map(field => [field, stored[field]])));
    }
  } catch { /* Missing or unreadable storage leaves catalog defaults in use. */ }
  return preferences;
}

/** Remembers what was chosen for a project (or the chats), bounding local history. Nothing chosen forgets the scope. */
export function rememberNewSessionPreference(preferences: ReadonlyMap<string, NewSessionPreference>, scope: string, preference: NewSessionPreference,
  write: (value: string) => void): ReadonlyMap<string, NewSessionPreference> {
  const next = new Map(preferences);
  next.delete(scope);
  if (Object.keys(preference).length > 0) next.set(scope, { ...preference });
  while (next.size > limit) next.delete(next.keys().next().value!);
  try { write(JSON.stringify(Object.fromEntries(next))); } catch { /* Choices still survive for this window. */ }
  return next;
}

/**
 * What is remembered once the user changes one choice: that choice and what it stands on, never a default that
 * was only shown. A model is chosen in the provider in use; another provider or another model starts again from
 * its defaults, as the composer does. An empty choice is forgotten.
 */
export function chosenNewSessionPreference(saved: NewSessionPreference | undefined, provider: string, field: NewSessionChoiceField, next: string): NewSessionPreference {
  const kept = (...names: NewSessionChoiceField[]): NewSessionPreference => Object.fromEntries(names.filter(name => saved?.[name]).map(name => [name, saved![name]]));
  if (field === "providerKey") return { ...kept("agentPromptId"), ...(next ? { providerKey: next } : {}) };
  if (field === "modelId") return next && provider ? { ...kept("agentPromptId"), providerKey: provider, modelId: next } : kept("agentPromptId", "providerKey");
  const others = kept(...fields.filter(name => name !== field));
  return next ? { ...others, [field]: next } : others;
}

type Model = Readonly<{ id: string; efforts: readonly string[]; defaultEffort: string | null }>;

/** The provider new sessions use: the one that was chosen while it is enabled, else the default one, else the first. */
export function newSessionProvider(saved: NewSessionPreference | undefined, providers: readonly Readonly<{ id: string; isDefault: boolean }>[]): string {
  return providers.find(candidate => candidate.id === saved?.providerKey)?.id || (providers.find(provider => provider.isDefault) ?? providers[0])?.id || "";
}

/**
 * The choices a new session starts with: what the user chose where the catalogs still offer it, and for the rest
 * what the terminal would pick, the configured model of the provider and its effort.
 */
export function newSessionSelection(saved: NewSessionPreference | undefined, provider: string, prompts: readonly Readonly<{ id: string }>[],
  models: readonly Model[], configured?: Readonly<{ defaultModel?: string | null; defaultReasoning?: string | null }>,
  tier?: Readonly<{ providerKey: string; modelId: string; reasoning: string | null }> | null): SessionSelection {
  // The model of the standard tier, when the user chose no model of this provider and the provider lists it.
  const tierModel = tier?.providerKey === provider && models.some(model => model.id === tier.modelId) ? tier : null;
  const modelId = saved?.providerKey === provider && models.some(model => model.id === saved.modelId) ? saved.modelId!
    : tierModel?.modelId ?? defaultModelId(models, configured?.defaultModel);
  const model = models.find(value => value.id === modelId);
  return { providerKey: provider, agentPromptId: prompts.find(prompt => prompt.id === saved?.agentPromptId)?.id ?? "default", modelId,
    reasoningEffort: model?.efforts.find(effort => effort === saved?.reasoningEffort)
      ?? (tierModel && modelId === tierModel.modelId ? model?.efforts.find(effort => effort === tierModel.reasoning) : undefined)
      ?? defaultReasoningEffort(model, configured?.defaultReasoning) };
}

/**
 * The provider of a new session when the user chose none: the one of the model the standard tier gives, while it is
 * enabled; otherwise null, and the default provider is taken.
 */
export function newSessionTierProvider(saved: NewSessionPreference | undefined, providers: readonly Readonly<{ id: string }>[],
  tier: Readonly<{ providerKey: string }> | null | undefined): string | null {
  return !saved?.providerKey && tier && providers.some(provider => provider.id === tier.providerKey) ? tier.providerKey : null;
}
