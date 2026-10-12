import { useEffect, useState } from "react";
import { modelCatalog, workspace, type ConfigurationSnapshot, type ModelCatalogModel, type SessionPromptChoice, type SessionSelection } from "#neoastra";
import { defaultModelId, defaultReasoningEffort } from "./newSessionDefaults";
import type { createMutationCapability } from "./sessionOperations";
import { newSessionPreferencesKey, rememberNewSessionPreference, restoreNewSessionPreferences } from "./newSessionPreferences";

// Catalogs only. Draft choices are preferences, never send or provider-switch authority.
export function useNewSessionChoices(epoch: string | undefined | null, projectId: string | null, projectPath: string | null,
  providerId: string, inventory: ConfigurationSnapshot | undefined, enabled: boolean,
  capability: ReturnType<typeof createMutationCapability> | undefined) {
  const providers = inventory?.providerRuntimeAvailable ? inventory.providers.filter(provider => provider.enabled) : [];
  const scope = JSON.stringify([projectId, projectPath]);
  const [remembered, setRemembered] = useState(() => restoreNewSessionPreferences(() => localStorage.getItem(newSessionPreferencesKey)));
  const saved = remembered.get(scope);
  const requested = providerId || saved?.providerKey;
  const provider = providers.find(candidate => candidate.id === requested)?.id || (providers.find(provider => provider.isDefault) ?? providers[0])?.id || "";
  const key = JSON.stringify([epoch, projectId, projectPath, provider]);
  const [revision, setRevision] = useState(0);
  const [prompts, setPrompts] = useState<{ key: string; rows: readonly SessionPromptChoice[]; loading: boolean; failed: boolean }>();
  const [models, setModels] = useState<{ key: string; rows: readonly ModelCatalogModel[]; loading: boolean; failed: boolean }>();
  const [preference, setPreference] = useState<{ key: string; value: SessionSelection }>();
  useEffect(() => {
    if (!enabled || !epoch || !capability?.canMutate()) return;
    const controller = new AbortController();
    setPrompts({ key, rows: [], loading: true, failed: false });
    void workspace.draftPrompts({ expectedHostEpoch: epoch, projectId, projectPath }, { signal: controller.signal, timeoutMilliseconds: 15000 })
      .then(reply => {
        capability.observe({ status: reply.status, epoch: reply.hostEpoch });
        if (controller.signal.aborted || !capability.canMutate()) return;
        const valid = reply.status === "ok" && reply.hostEpoch === epoch && reply.projectId === projectId && reply.projectPath === projectPath;
        setPrompts({ key, rows: valid ? reply.prompts : [], loading: false, failed: !valid });
      }).catch(() => { if (!controller.signal.aborted) setPrompts({ key, rows: [], loading: false, failed: true }); });
    return () => controller.abort();
  }, [epoch, projectId, projectPath, key, enabled, capability, revision]);
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
  // Restore remembered choices only when offered; otherwise use the provider's configured defaults.
  const configured = providers.find(candidate => candidate.id === provider);
  const offered = models?.key === key ? models.rows : [];
  const savedModel = saved?.providerKey === provider && offered.some(model => model.id === saved.modelId) ? saved.modelId : null;
  const startModel = savedModel ?? defaultModelId(offered, configured?.defaultModel);
  const savedEffort = savedModel && offered.find(model => model.id === savedModel)?.efforts.find(effort => effort === saved?.reasoningEffort);
  const value = preference?.key === key ? preference.value : { providerKey: provider,
    agentPromptId: prompts?.key === key && prompts.rows.some(prompt => prompt.id === saved?.agentPromptId) ? saved!.agentPromptId : "default", modelId: startModel,
    reasoningEffort: savedEffort ?? defaultReasoningEffort(offered.find(model => model.id === startModel), configured?.defaultReasoning) };
  const ready = !!provider && prompts?.key === key && prompts.rows.some(prompt => prompt.id === value.agentPromptId)
    && models?.key === key && !models.loading && !models.failed
    && (value.modelId === null ? models.rows.length === 0 && value.reasoningEffort === null : models.rows.some(model => model.id === value.modelId
      && (value.reasoningEffort === null || model.efforts.includes(value.reasoningEffort))));
  const loadingModels = enabled && !!provider && (!models || models.key !== key || models.loading);
  useEffect(() => {
    if (!enabled || !ready || loadingModels) return;
    setRemembered(current => {
      if (JSON.stringify(current.get(scope)) === JSON.stringify(value)) return current;
      return rememberNewSessionPreference(current, scope, value, stored => localStorage.setItem(newSessionPreferencesKey, stored));
    });
  }, [enabled, ready, loadingModels, scope, value.providerKey, value.agentPromptId, value.modelId, value.reasoningEffort]);
  return {
    value, ready,
    prompts: prompts?.key === key ? prompts.rows : [], models: models?.key === key ? models.rows : [],
    loadingPrompts: enabled && (!prompts || prompts.key !== key || prompts.loading),
    loadingModels,
    failed: prompts?.key === key && prompts.failed || models?.key === key && models.failed,
    change(field: "agentPromptId" | "modelId" | "reasoningEffort", next: string) {
      setPreference({ key, value: { ...value, [field]: next || null,
        ...(field === "modelId" ? { reasoningEffort: defaultReasoningEffort(offered.find(model => model.id === next), configured?.defaultReasoning) } : {}) } });
    },
    refresh: () => setRevision(previous => previous + 1),
  };
}
