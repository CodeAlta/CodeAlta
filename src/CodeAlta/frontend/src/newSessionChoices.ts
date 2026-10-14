import { useEffect, useState } from "react";
import { modelCatalog, workspace, type ConfigurationSnapshot, type ModelCatalogModel, type SessionPromptChoice } from "#neoastra";
import type { createMutationCapability } from "./sessionOperations";
import { chosenNewSessionPreference, newSessionPreferencesKey, newSessionProvider, newSessionSelection, providersInSpace, newSessionTierProvider, rememberNewSessionPreference,
  restoreNewSessionPreferences, tierNewSessionPreference, type NewSessionChoiceField } from "./newSessionPreferences";
import { useTiers } from "./modelTiers";

// Catalogs only. Draft choices are preferences, never send or provider-switch authority.
export function useNewSessionChoices(epoch: string | undefined | null, projectId: string | null, projectPath: string | null,
  inventory: ConfigurationSnapshot | undefined, enabled: boolean,
  capability: ReturnType<typeof createMutationCapability> | undefined,
  /** The space the new session is created in: its providers, its agent prompts and its tiers are those of the space. */
  spaceId: string | null = null) {
  // A new session is in the space shown: it starts with a provider of that space.
  const providers = inventory?.providerRuntimeAvailable ? providersInSpace(inventory.providers.filter(provider => provider.enabled), spaceId) : [];
  // What the user chose for the new sessions of this project (or of the chats), kept with the window.
  const scope = JSON.stringify([projectId, projectPath]);
  const [remembered, setRemembered] = useState(() => restoreNewSessionPreferences(() => localStorage.getItem(newSessionPreferencesKey)));
  const saved = remembered.get(scope);
  // With tiers in the settings, a new session the user chose nothing for starts on the standard tier.
  const tiers = useTiers(epoch, spaceId ? { spaceId } : null);
  const standard = tiers?.choices.standard ?? null;
  const provider = newSessionTierProvider(saved, providers, standard) ?? newSessionProvider(saved, providers);
  // A new session is in the space shown: its agent prompts, and the one its new sessions start with.
  const key = JSON.stringify([epoch, projectId, projectPath, provider, spaceId]);
  const [revision, setRevision] = useState(0);
  const [prompts, setPrompts] = useState<{ key: string; rows: readonly SessionPromptChoice[]; defaultId: string | null; loading: boolean; failed: boolean }>();
  const [models, setModels] = useState<{ key: string; rows: readonly ModelCatalogModel[]; loading: boolean; failed: boolean }>();
  useEffect(() => {
    if (!enabled || !epoch || !capability?.canMutate()) return;
    const controller = new AbortController();
    setPrompts({ key, rows: [], defaultId: null, loading: true, failed: false });
    void workspace.draftPrompts({ expectedHostEpoch: epoch, projectId, projectPath, spaceId }, { signal: controller.signal, timeoutMilliseconds: 15000 })
      .then(reply => {
        capability.observe({ status: reply.status, epoch: reply.hostEpoch });
        if (controller.signal.aborted || !capability.canMutate()) return;
        const valid = reply.status === "ok" && reply.hostEpoch === epoch && reply.projectId === projectId && reply.projectPath === projectPath;
        setPrompts({ key, rows: valid ? reply.prompts : [], defaultId: valid ? reply.defaultPromptId : null, loading: false, failed: !valid });
      }).catch(() => { if (!controller.signal.aborted) setPrompts({ key, rows: [], defaultId: null, loading: false, failed: true }); });
    return () => controller.abort();
  }, [epoch, projectId, projectPath, spaceId, key, enabled, capability, revision]);
  useEffect(() => {
    if (!enabled || !epoch || !provider || !capability?.canMutate()) return;
    const controller = new AbortController();
    setModels({ key, rows: [], loading: true, failed: false });
    void modelCatalog.models({ expectedEpoch: epoch, providerId: provider }, { signal: controller.signal, timeoutMilliseconds: 15000 })
      .then(reply => {
        capability.observe(reply);
        if (controller.signal.aborted || !capability.canMutate()) return;
        const valid = reply.status === "ok" && reply.epoch === epoch && reply.providerId === provider && reply.availability === "Ready";
        setModels({ key, rows: valid ? reply.models : [], loading: false, failed: !valid });
      }).catch(() => { if (!controller.signal.aborted) setModels({ key, rows: [], loading: false, failed: true }); });
    return () => controller.abort();
  }, [epoch, provider, key, enabled, capability, revision]);
  // Until the user chooses, the draft names what the terminal would pick: the provider's model and its effort.
  // What was chosen is used while the catalogs still offer it.
  const configured = providers.find(candidate => candidate.id === provider);
  const value = newSessionSelection(saved, provider, prompts?.key === key ? prompts.rows : [], models?.key === key ? models.rows : [], configured, standard,
    prompts?.key === key ? prompts.defaultId : null);
  // The session runs on the tier chosen with its model, else on the standard tier when it starts with the model that tier gives.
  const tier = saved?.tier && saved.providerKey === value.providerKey && saved.modelId === value.modelId ? saved.tier
    : !saved?.providerKey && standard && value.providerKey === standard.providerKey && value.modelId === standard.modelId ? "standard" : null;
  const ready = !!provider && prompts?.key === key && prompts.rows.some(prompt => prompt.id === value.agentPromptId)
    && models?.key === key && !models.loading && !models.failed
    && (value.modelId === null ? models.rows.length === 0 && value.reasoningEffort === null : models.rows.some(model => model.id === value.modelId
      && (value.reasoningEffort === null || model.efforts.includes(value.reasoningEffort))));
  return {
    value, ready, tier,
    prompts: prompts?.key === key ? prompts.rows : [], models: models?.key === key ? models.rows : [],
    loadingPrompts: enabled && (!prompts || prompts.key !== key || prompts.loading),
    loadingModels: enabled && !!provider && (!models || models.key !== key || models.loading),
    failed: prompts?.key === key && prompts.failed || models?.key === key && models.failed,
    // Only a choice of the user is remembered: a default that is shown stays the default of the settings.
    change(field: NewSessionChoiceField, next: string) {
      setRemembered(rememberNewSessionPreference(remembered, scope, chosenNewSessionPreference(saved, provider, field, next),
        stored => localStorage.setItem(newSessionPreferencesKey, stored)));
    },
    /** The tiers of the settings, with the model each gives now; null until they are read. */
    tiers,
    /** Takes the model a tier gives now, and the tier with it; false when the tier gives none. */
    chooseTier(name: string) {
      const choice = tiers?.choices[name as keyof typeof tiers.choices];
      if (!choice) return false;
      setRemembered(rememberNewSessionPreference(remembered, scope, tierNewSessionPreference(saved, name, choice),
        stored => localStorage.setItem(newSessionPreferencesKey, stored)));
      return true;
    },
    refresh: () => setRevision(previous => previous + 1),
  };
}
