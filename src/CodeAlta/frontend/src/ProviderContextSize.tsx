import { useEffect, useState } from "react";
import { FormGroup } from "@blueprintjs/core";
import type { modelCatalog, ModelCatalogModel } from "#neoastra";
import { ContextSizeSelect } from "./ContextSizeSelect";
import { useShellLanguage } from "./shellLanguage";

/** Catalog reads are provider-scoped; the parent remounts on config revision. Preferences change only on Save and apply. */
export function ProviderContextSize({ epoch, providerId, modelId, preferences, disabled, readModels, onChange }: {
  epoch: string; providerId: string | null; modelId: string; preferences: Readonly<Record<string, number>>;
  disabled: boolean; readModels: typeof modelCatalog.models; onChange: (model: string, tokens: number | null) => void;
}) {
  const { t } = useShellLanguage();
  const key = JSON.stringify([epoch, providerId]);
  const [state, setState] = useState<{ key: string; models?: readonly ModelCatalogModel[]; loading: boolean }>();
  useEffect(() => {
    const controller = new AbortController();
    setState({ key, loading: !!providerId });
    if (!providerId) return () => controller.abort();
    void readModels({ expectedEpoch: epoch, providerId }, { signal: controller.signal, timeoutMilliseconds: 15000 }).then(reply => {
      if (controller.signal.aborted) return;
      const valid = reply.status === "ok" && reply.epoch === epoch && reply.providerId === providerId && reply.availability === "Ready";
      setState({ key, loading: false, models: valid ? reply.models : undefined });
    }).catch(() => { if (!controller.signal.aborted) setState({ key, loading: false }); });
    return () => controller.abort();
  }, [key, epoch, providerId, readModels]);
  const current = state?.key === key ? state : undefined;
  const model = modelId ? current?.models?.find(model => model.id === modelId) : current?.models?.[0];
  const chosen = modelId || model?.id || "";
  return <FormGroup label={t("Context size")} labelFor="provider-context">
    <ContextSizeSelect id="provider-context" value={preferences[chosen] ?? null}
      model={model ? { contextSizes: model.contextSizes } : undefined}
      loading={!!providerId && (!current || current.loading)} disabled={disabled}
      onChange={tokens => { if (chosen) onChange(chosen, tokens); }} />
  </FormGroup>;
}
