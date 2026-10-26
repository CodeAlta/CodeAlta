// The sandbox a provider, a model, a space or a project chooses: a select saved at once, where that place is edited.
import { useEffect, useState } from "react";
import { Button, HTMLSelect } from "@blueprintjs/core";
// The service of the host is given, never imported here: the row of a project uses these controls in windows and
// tests that load no client of the host.
import type { ModelCatalogModel, ModelCatalogModelsRequest, ModelCatalogModelsResponse, SandboxSettingsResponse } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { showToast } from "../appToaster";
import { useShellLanguage } from "../shellLanguage";
import { modelSandboxes, notSandboxed, scopeSandbox, type SandboxApi, type SandboxScopeKind } from "./sandboxChoices";

/** What the scope controls read and write. */
export type SandboxScopeApi = Pick<SandboxApi, "read" | "setScope">;

/**
 * The profiles and the scopes of the host, read once for a control and again after each change it makes; null while
 * they are not known, or where the host has no sandbox (a window that does not own one).
 */
export function useSandboxScopes(epoch: string | null | undefined, api: SandboxScopeApi) {
  const { t } = useShellLanguage();
  const [listing, setListing] = useState<SandboxSettingsResponse | null>(null);
  const [generation, setGeneration] = useState(0);
  const [busy, setBusy] = useState(false);
  useEffect(() => {
    if (!epoch) { setListing(null); return; }
    const controller = new AbortController();
    void api.read({ expectedEpoch: epoch }, { signal: controller.signal, timeoutMilliseconds: 15_000 })
      .then(reply => { if (!controller.signal.aborted) setListing(reply.status === "ok" && reply.availability ? reply : null); })
      .catch(() => { if (!controller.signal.aborted) setListing(null); });
    return () => controller.abort();
  }, [epoch, api, generation]);
  /** Chooses the sandbox of a scope: a profile, `off`, or null to leave it to the next scope. */
  async function choose(kind: SandboxScopeKind, id: string, modelId: string | null, sandbox: string | null) {
    if (!epoch) return false;
    setBusy(true);
    try {
      const reply = await api.setScope({ expectedEpoch: epoch, kind, id, modelId, sandbox }, { timeoutMilliseconds: 30_000 }).catch(() => null);
      if (reply?.status !== "ok") showToast({ message: reply?.message ?? t("The sandbox was not changed ({status}).", { status: reply?.status ?? "no_answer" }),
        intent: "warning", icon: "warning-sign", timeout: 6000 });
      return reply?.status === "ok";
    } finally { setBusy(false); setGeneration(value => value + 1); }
  }
  const profiles = listing?.profiles.map(profile => profile.name) ?? [];
  return { listing, profiles, busy, choose, value: (kind: SandboxScopeKind, id: string, modelId: string | null = null) => listing ? scopeSandbox(listing.scopes, kind, id, modelId) : null };
}

/**
 * The sandbox of a place: "Follow the settings" leaves it to the next place (the project, the space, every session),
 * "Not sandboxed" turns it off there, and a profile sandboxes its sessions with it.
 */
export function SandboxScopeSelect({ id, value, profiles, disabled = false, label, onChange }: {
  id?: string; value: string | null; profiles: readonly string[]; disabled?: boolean;
  /** What the select is about, for a screen reader. */
  label: string;
  onChange: (value: string | null) => void;
}) {
  const { t } = useShellLanguage();
  const known = value === null || value.toLowerCase() === notSandboxed || profiles.some(profile => profile.toLowerCase() === value.toLowerCase());
  return <HTMLSelect id={id} className="sandbox-scope-select" aria-label={label} value={value ?? ""} disabled={disabled} data-chosen={value !== null || undefined}
    onChange={event => { const next = event.currentTarget.value; onChange(next === "" ? null : next); }}>
    <option value="">{t("Follow the settings")}</option>
    <option value={notSandboxed}>{t("Not sandboxed")}</option>
    {profiles.map(profile => <option key={profile} value={profile}>{profile}</option>)}
    {!known && <option value={value!}>{value}</option>}
  </HTMLSelect>;
}

/**
 * The sandbox of a provider and of some of its models, in the editor of the provider: the provider's select, then a
 * row for each model that has a sandbox of its own, and the models that can get one.
 */
export function ProviderSandboxField({ epoch, providerKey, providerName, disabled = false, api, readModels }: {
  epoch: string; providerKey: string; providerName: string; disabled?: boolean; api: SandboxScopeApi;
  /** The models of the provider, offered to get a sandbox of their own; without it, only those that have one are listed. */
  readModels?: (request: ModelCatalogModelsRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<ModelCatalogModelsResponse>;
}) {
  const { t } = useShellLanguage();
  const scopes = useSandboxScopes(epoch, api);
  const [models, setModels] = useState<readonly ModelCatalogModel[]>([]);
  const [adding, setAdding] = useState(false);
  // The models are read to name the rows of those that have a sandbox, and to offer the others.
  const listed = !!scopes.listing && modelSandboxes(scopes.listing.scopes, providerKey).length > 0;
  useEffect(() => {
    if (!readModels || !adding && !listed) return;
    const controller = new AbortController();
    void readModels({ expectedEpoch: epoch, providerId: providerKey }, { signal: controller.signal, timeoutMilliseconds: 15_000 })
      .then(reply => { if (!controller.signal.aborted && reply.status === "ok") setModels(reply.models); }).catch(() => { /* Only the models with a sandbox are listed. */ });
    return () => controller.abort();
  }, [epoch, providerKey, readModels, adding, listed]);
  if (!scopes.listing) return null;
  const own = modelSandboxes(scopes.listing.scopes, providerKey);
  const name = (modelId: string) => models.find(model => model.id.toLowerCase() === modelId.toLowerCase())?.name ?? modelId;
  const addable = models.filter(model => !own.some(entry => entry.modelId.toLowerCase() === model.id.toLowerCase()));
  const off = disabled || scopes.busy;
  return <div className="provider-sandbox">
    <SandboxScopeSelect id="provider-sandbox" value={scopes.value("provider", providerKey)} profiles={scopes.profiles} disabled={off}
      label={t("Sandbox of {name}", { name: providerName })} onChange={value => void scopes.choose("provider", providerKey, null, value)} />
    {own.length > 0 && <ul className="provider-sandbox-models" aria-label={t("Sandbox by model")}>
      {own.map(entry => <li key={entry.modelId}>
        <span className="provider-sandbox-model" title={entry.modelId}><AppIcon name="model" size={13} />{name(entry.modelId)}</span>
        <SandboxScopeSelect value={entry.profile} profiles={scopes.profiles} disabled={off} label={t("Sandbox of {name}", { name: name(entry.modelId) })}
          onChange={value => void scopes.choose("model", providerKey, entry.modelId, value)} />
        <Button variant="minimal" size="small" icon={<AppIcon name="close" size={13} />} disabled={off} aria-label={t("Remove the sandbox of {name}", { name: name(entry.modelId) })}
          title={t("Remove the sandbox of {name}", { name: name(entry.modelId) })} onClick={() => void scopes.choose("model", providerKey, entry.modelId, null)} />
      </li>)}
    </ul>}
    {readModels && (adding
      ? <HTMLSelect className="provider-sandbox-add" aria-label={t("Model with a sandbox of its own")} value="" disabled={off} autoFocus
          onBlur={() => { if (!scopes.busy) setAdding(false); }}
          onChange={event => {
            const modelId = event.currentTarget.value;
            if (!modelId) { setAdding(false); return; }
            // A model starts with the sandbox its provider gives, or the first profile; the user changes it in its row.
            const start = scopes.value("provider", providerKey) ?? scopes.profiles[0] ?? notSandboxed;
            void scopes.choose("model", providerKey, modelId, start).then(() => setAdding(false));
          }}>
          <option value="">{t(models.length ? "Choose a model…" : "Loading…")}</option>
          {addable.map(model => <option key={model.id} value={model.id}>{model.name}</option>)}
        </HTMLSelect>
      : <Button variant="minimal" size="small" className="provider-sandbox-add" icon={<AppIcon name="plus" size={13} />} disabled={off}
          onClick={() => setAdding(true)}>{t("Sandbox by model")}</Button>)}
  </div>;
}
