// The mode of Code Mode a provider or a model chooses: a select saved at once, in the editor of the provider.
import { useEffect, useState } from "react";
import { Button, HTMLSelect } from "@blueprintjs/core";
// The service of the host is given, never imported here: the tests give their own.
import type { GlobalConfigCodeModeProviderResponse, globalConfig, ModelCatalogModel, ModelCatalogModelsRequest, ModelCatalogModelsResponse } from "#neoastra";
import { AppIcon } from "./AppIcon";
import { showToast } from "./appToaster";
import type { MessageKey } from "./localization";
import { useShellLanguage } from "./shellLanguage";

/** What the Code Mode of a provider is read and changed with. */
export type CodeModeScopeApi = Pick<typeof globalConfig, "codeModeProvider" | "saveCodeModeScope">;

/** The name of each mode, for the selects. */
export const codeModeLabels: Readonly<Record<string, MessageKey>> = Object.freeze({ off: "Off", on: "On", only: "Only" });

/** The modes a select offers, in their order. */
export const codeModeChoices = Object.freeze(["off", "on", "only"] as const);

/**
 * The mode of a place: the first choice leaves it to the next place (what that gives is `inherited`), the others
 * choose a mode there.
 */
export function CodeModeSelect({ id, value, inherited, disabled = false, label, onChange }: {
  id?: string; value: string | null;
  /** What the place has without a mode of its own. */
  inherited: string;
  disabled?: boolean;
  /** What the select is about, for a screen reader. */
  label: string;
  onChange: (value: string | null) => void;
}) {
  const { t } = useShellLanguage();
  const name = (mode: string) => mode in codeModeLabels ? t(codeModeLabels[mode]) : mode;
  return <HTMLSelect id={id} className="code-mode-scope-select" aria-label={label} value={value ?? ""} disabled={disabled} data-chosen={value !== null || undefined}
    onChange={event => { const next = event.currentTarget.value; onChange(next === "" ? null : next); }}>
    <option value="">{t("Follow the settings ({mode})", { mode: name(inherited) })}</option>
    {codeModeChoices.map(mode => <option key={mode} value={mode}>{name(mode)}</option>)}
    {value !== null && !(codeModeChoices as readonly string[]).includes(value) && <option value={value}>{value}</option>}
  </HTMLSelect>;
}

/**
 * The mode of Code Mode of a provider and of some of its models, in the editor of the provider: the provider's select,
 * then a row for each model that has a mode of its own, and the models that can get one.
 */
export function ProviderCodeModeField({ epoch, providerKey, providerName, disabled = false, api, readModels }: {
  epoch: string; providerKey: string; providerName: string; disabled?: boolean; api: CodeModeScopeApi;
  /** The models of the provider, offered to get a mode of their own; without it, only those that have one are listed. */
  readModels?: (request: ModelCatalogModelsRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<ModelCatalogModelsResponse>;
}) {
  const { t } = useShellLanguage();
  const [listing, setListing] = useState<GlobalConfigCodeModeProviderResponse | null>(null);
  const [models, setModels] = useState<readonly ModelCatalogModel[]>([]);
  const [generation, setGeneration] = useState(0);
  const [busy, setBusy] = useState(false);
  const [adding, setAdding] = useState(false);
  const own = listing?.models ?? [];
  // The models are read to name the rows of those that have a mode, and to offer the others.
  const wanted = adding || own.length > 0;
  useEffect(() => {
    if (!readModels || !wanted) return;
    const controller = new AbortController();
    void readModels({ expectedEpoch: epoch, providerId: providerKey }, { signal: controller.signal, timeoutMilliseconds: 15_000 })
      .then(reply => { if (!controller.signal.aborted && reply.status === "ok") setModels(reply.models); }).catch(() => { /* Only the models with a mode are listed. */ });
    return () => controller.abort();
  }, [epoch, providerKey, readModels, wanted]);
  useEffect(() => {
    const controller = new AbortController();
    void api.codeModeProvider({ expectedEpoch: epoch, providerKey }, { signal: controller.signal, timeoutMilliseconds: 15_000 })
      .then(reply => { if (!controller.signal.aborted) setListing(reply.status === "ok" ? reply : null); })
      .catch(() => { if (!controller.signal.aborted) setListing(null); });
    return () => controller.abort();
  }, [epoch, providerKey, api, generation]);
  async function choose(modelId: string | null, mode: string | null) {
    setBusy(true);
    try {
      const reply = await api.saveCodeModeScope({ expectedEpoch: epoch, providerKey, modelId, mode }, { timeoutMilliseconds: 30_000 }).catch(() => null);
      if (reply?.status !== "ok") showToast({ message: reply?.message ?? t("Code Mode was not changed ({status}).", { status: reply?.status ?? "no_answer" }),
        intent: "warning", icon: "warning-sign", timeout: 6000 });
      return reply?.status === "ok";
    } finally { setBusy(false); setGeneration(value => value + 1); }
  }
  if (!listing) return null;
  // A model without a mode of its own has the one of its provider, else the one of every session.
  const allSessions = listing.allSessions ?? "on";
  const inherited = listing.provider ?? allSessions;
  const name = (modelId: string) => models.find(model => model.id.toLowerCase() === modelId.toLowerCase())?.name ?? modelId;
  const addable = models.filter(model => !own.some(entry => entry.modelId.toLowerCase() === model.id.toLowerCase()));
  const off = disabled || busy;
  return <div className="provider-code-mode">
    <CodeModeSelect id="provider-code-mode" value={listing.provider} inherited={allSessions} disabled={off} label={t("Code Mode of {name}", { name: providerName })}
      onChange={value => void choose(null, value)} />
    {own.length > 0 && <ul className="provider-code-mode-models" aria-label={t("Code Mode by model")}>
      {own.map(entry => <li key={entry.modelId}>
        <span className="provider-code-mode-model" title={entry.modelId}><AppIcon name="model" size={13} />{name(entry.modelId)}</span>
        <CodeModeSelect value={entry.mode} inherited={inherited} disabled={off} label={t("Code Mode of {name}", { name: name(entry.modelId) })}
          onChange={value => void choose(entry.modelId, value)} />
        <Button variant="minimal" size="small" icon={<AppIcon name="close" size={13} />} disabled={off} aria-label={t("Remove the Code Mode of {name}", { name: name(entry.modelId) })}
          title={t("Remove the Code Mode of {name}", { name: name(entry.modelId) })} onClick={() => void choose(entry.modelId, null)} />
      </li>)}
    </ul>}
    {readModels && (adding
      ? <HTMLSelect className="provider-code-mode-add" aria-label={t("Model with a Code Mode of its own")} value="" disabled={off} autoFocus
          onBlur={() => { if (!busy) setAdding(false); }}
          onChange={event => {
            const modelId = event.currentTarget.value;
            if (!modelId) { setAdding(false); return; }
            // A model starts with the mode it has now; the user changes it in its row.
            void choose(modelId, inherited).then(() => setAdding(false));
          }}>
          <option value="">{t(models.length ? "Choose a model…" : "Loading…")}</option>
          {addable.map(model => <option key={model.id} value={model.id}>{model.name}</option>)}
        </HTMLSelect>
      : <Button variant="minimal" size="small" className="provider-code-mode-add" icon={<AppIcon name="plus" size={13} />} disabled={off}
          onClick={() => setAdding(true)}>{t("Code Mode by model")}</Button>)}
  </div>;
}
