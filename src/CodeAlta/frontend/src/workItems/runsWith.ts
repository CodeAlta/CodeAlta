// What a session started for a work item runs with: the provider, the model and the reasoning effort.
import { useEffect, useState } from "react";
import { defaultModelId, defaultReasoningEffort } from "../newSessionDefaults";

/** An enabled provider, as the configuration of the window lists it. */
export type RunProvider = Readonly<{ id: string; name: string; isDefault: boolean; defaultModel: string | null; defaultReasoning: string | null }>;
/** A model a provider offers. */
export type RunModel = Readonly<{ id: string; name?: string; efforts: readonly string[]; defaultEffort: string | null }>;
/** What an item was proposed with, or what the user chose. */
export type RunsWith = Readonly<{ providerId: string; modelId: string | null; reasoningEffort: string | null }>;
/** What the user changed: a part that is not there follows the item, then the defaults. */
export type RunChoice = Readonly<{ providerId?: string; modelId?: string; reasoningEffort?: string }>;
/** Lists the models of a provider; an empty list when they cannot be read. */
export type RunModelsLoader = (providerId: string, signal: AbortSignal) => Promise<readonly RunModel[]>;

/** The provider new work starts with: the default one, else the first enabled one. */
export function defaultRunProvider(providers: readonly RunProvider[]): RunProvider | null {
  return providers.find(provider => provider.isDefault) ?? providers[0] ?? null;
}

/**
 * The provider of a start: the one the user chose, then the one the item was proposed with, then the default
 * one. A provider that is no longer enabled is passed over.
 */
export function runProvider(providers: readonly RunProvider[], recorded: RunsWith | null | undefined, choice: RunChoice): RunProvider | null {
  const find = (id: string | null | undefined) => id ? providers.find(provider => provider.id.toLowerCase() === id.toLowerCase()) : undefined;
  return find(choice.providerId) ?? find(recorded?.providerId) ?? defaultRunProvider(providers);
}

/**
 * The model and the effort of a start, among what the provider offers: what the user chose, then what the item
 * was proposed with when the provider is the same, then what a new session of that provider starts with.
 */
export function runModel(provider: RunProvider | null, models: readonly RunModel[], recorded: RunsWith | null | undefined, choice: RunChoice):
  Readonly<{ modelId: string | null; reasoningEffort: string | null }> {
  if (!provider || models.length === 0) return { modelId: null, reasoningEffort: null };
  const listed = (id: string | null | undefined) => id ? models.find(model => model.id === id) : undefined;
  const proposed = recorded && recorded.providerId.toLowerCase() === provider.id.toLowerCase() ? recorded : null;
  const model = listed(choice.modelId) ?? listed(proposed?.modelId) ?? listed(defaultModelId(models, provider.defaultModel));
  if (!model) return { modelId: null, reasoningEffort: null };
  const supported = (effort: string | null | undefined) => effort ? model.efforts.find(value => value.toLowerCase() === effort.toLowerCase()) : undefined;
  return { modelId: model.id, reasoningEffort: supported(choice.reasoningEffort) ?? (proposed?.modelId === model.id ? supported(proposed.reasoningEffort) : undefined)
    ?? defaultReasoningEffort(model, provider.defaultReasoning) };
}

/** Reads the models of a provider, again when the provider changes. */
export function useRunModels(providerId: string | null, load: RunModelsLoader | null | undefined) {
  const [state, setState] = useState<{ providerId: string; models: readonly RunModel[] } | null>(null);
  useEffect(() => {
    if (!providerId || !load) return;
    const abort = new AbortController();
    void load(providerId, abort.signal).then(models => { if (!abort.signal.aborted) setState({ providerId, models }); })
      .catch(() => { if (!abort.signal.aborted) setState({ providerId, models: [] }); });
    return () => abort.abort();
  }, [providerId, load]);
  const current = providerId !== null && state?.providerId === providerId;
  return { models: current ? state.models : [], loading: !!providerId && !!load && !current };
}

/**
 * What a start runs with, and the lists to choose from. The selection is null while the models of the provider
 * are being read: the host then decides, with what the item was proposed with.
 */
export function useRunsWith(providers: readonly RunProvider[], recorded: RunsWith | null | undefined, load: RunModelsLoader | null | undefined) {
  const [choice, setChoice] = useState<RunChoice>({});
  const provider = runProvider(providers, recorded, choice);
  const { models, loading } = useRunModels(provider?.id ?? null, load);
  const { modelId, reasoningEffort } = runModel(provider, models, recorded, choice);
  const selection: RunsWith | null = provider && !loading ? { providerId: provider.id, modelId, reasoningEffort: modelId ? reasoningEffort : null } : null;
  return {
    provider, models, loading, modelId, reasoningEffort, selection,
    efforts: models.find(model => model.id === modelId)?.efforts ?? [],
    chooseProvider: (providerId: string) => setChoice({ providerId }),
    chooseModel: (model: string) => setChoice(current => ({ providerId: current.providerId, modelId: model })),
    chooseEffort: (effort: string) => setChoice(current => ({ ...current, modelId: current.modelId ?? modelId ?? undefined, reasoningEffort: effort })),
  };
}
