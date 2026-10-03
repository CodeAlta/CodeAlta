import { useEffect, useState } from "react";
import { modelCatalog, workspace, type ConfigurationSnapshot, type ModelCatalogModel, type SessionPromptChoice, type SessionSelection } from "#neoastra";
import type { createMutationCapability } from "./sessionOperations";

// Catalogs only. Draft choices are preferences, never send or provider-switch authority.
export function useNewSessionChoices(epoch: string | undefined | null, projectId: string | null, projectPath: string | null,
  providerId: string, inventory: ConfigurationSnapshot | undefined, enabled: boolean,
  capability: ReturnType<typeof createMutationCapability> | undefined) {
  const providers = inventory?.providerRuntimeAvailable ? inventory.providers.filter(provider => provider.enabled) : [];
  const provider = providerId || (providers.find(provider => provider.isDefault) ?? providers[0])?.id || "";
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
  const value = preference?.key === key ? preference.value : { providerKey: provider, agentPromptId: "default", modelId: null, reasoningEffort: null };
  const ready = !!provider && prompts?.key === key && prompts.rows.some(prompt => prompt.id === value.agentPromptId)
    && (value.modelId === null ? value.reasoningEffort === null : models?.key === key && models.rows.some(model => model.id === value.modelId
      && (value.reasoningEffort === null || model.efforts.includes(value.reasoningEffort))));
  return {
    value, ready,
    prompts: prompts?.key === key ? prompts.rows : [], models: models?.key === key ? models.rows : [],
    loadingPrompts: enabled && (!prompts || prompts.key !== key || prompts.loading),
    loadingModels: enabled && !!provider && (!models || models.key !== key || models.loading),
    failed: prompts?.key === key && prompts.failed || models?.key === key && models.failed,
    change(field: "agentPromptId" | "modelId" | "reasoningEffort", next: string) {
      setPreference({ key, value: { ...value, [field]: next || null, ...(field === "modelId" ? { reasoningEffort: null } : {}) } });
    },
    refresh: () => setRevision(previous => previous + 1),
  };
}
