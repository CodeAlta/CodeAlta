import { useEffect, useRef, useState } from "react";
import { ProviderIcon } from "./ProviderIcon";
import type { ConfigurationProvider, ModelCatalogProbeRequest, ModelCatalogProbeResponse, ModelCatalogProvidersRequest, ModelCatalogProvidersResponse } from "#neoastra";
import { useShellLanguage } from "./shellLanguage";
import type { MessageKey } from "./localization";

type Read = (request: ModelCatalogProvidersRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<ModelCatalogProvidersResponse>;
type Probe = (request: ModelCatalogProbeRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<ModelCatalogProbeResponse>;

export function ProvidersPanel({ epoch, read, probe, catalogProviders, holds, onOpenModels }: {
  epoch: string | null; read: Read; probe: Probe; catalogProviders?: readonly ConfigurationProvider[];
  holds?: Set<string>; onOpenModels: () => void;
}) {
  const { t } = useShellLanguage();
  const [inventory, setInventory] = useState<ModelCatalogProvidersResponse>();
  const [error, setError] = useState<MessageKey | "">("");
  const [providerId, setProviderId] = useState<string | null>(null);
  const [probing, setProbing] = useState(false);
  const [probeResult, setProbeResult] = useState<ModelCatalogProbeResponse>();
  const [probeError, setProbeError] = useState<MessageKey | "">("");
  const active = useRef<AbortController | null>(null);
  const pending = useRef(false);
  const localHolds = useRef<Set<string>>(new Set());
  const uncertain = holds ?? localHolds.current;
  const currentProvider = useRef<string | null>(null);
  const [held, setHeld] = useState(false);
  useEffect(() => {
    for (const key of uncertain) if (!epoch || !key.startsWith(`${epoch}:`)) uncertain.delete(key);
    setHeld(false);
    setInventory(undefined); setError(""); setProviderId(null); setProbeResult(undefined); setProbeError("");
    if (!epoch) return;
    const controller = new AbortController();
    void read({ expectedEpoch: epoch }, { signal: controller.signal, timeoutMilliseconds: 15000 }).then(value => {
      if (controller.signal.aborted) return;
      if (value.epoch !== epoch || value.status !== "ok") {
        setError(value.epoch !== epoch || value.status === "stale_epoch" ? "Host identity changed. Reload required." : "Provider inventory unavailable."); return;
      }
      if (!Array.isArray(value.providers) || value.providers.length > 32 || value.providers.some(provider =>
        typeof provider.id !== "string" || !provider.id || provider.id.length > 256 || typeof provider.name !== "string"
        || typeof provider.type !== "string" || typeof provider.enabled !== "boolean" || typeof provider.isDefault !== "boolean"
        || typeof provider.availability !== "string")) {
        setError("Invalid provider inventory. Reload required."); return;
      }
      setInventory(value);
    }).catch(() => { if (!controller.signal.aborted) setError("Provider inventory could not be read."); });
    return () => {
      controller.abort();
      if (active.current && currentProvider.current) uncertain.add(`${epoch}:${currentProvider.current}`);
      active.current?.abort(); active.current = null; pending.current = false;
    };
  }, [epoch, read]);
  const selected = inventory?.epoch === epoch ? inventory.providers.find(value => value.id === providerId) : undefined;
  function select(id: string) {
    if (id === providerId) return;
    if (active.current && providerId) uncertain.add(`${epoch}:${providerId}`);
    active.current?.abort(); active.current = null; pending.current = false;
    currentProvider.current = id;
    setProbing(false); setHeld(uncertain.has(`${epoch}:${id}`)); setProbeError(""); setProbeResult(undefined); setProviderId(id);
  }
  async function testProvider() {
    if (!epoch || !selected || !selected.enabled || pending.current || uncertain.has(`${epoch}:${selected.id}`)) return;
    pending.current = true; setProbing(true); setProbeError(""); setProbeResult(undefined);
    const controller = new AbortController(); active.current = controller;
    try {
      const reply = await probe({ expectedEpoch: epoch, providerId: selected.id }, { signal: controller.signal, timeoutMilliseconds: 45000 });
      if (controller.signal.aborted) return;
      if (reply.epoch !== epoch || reply.providerId !== selected.id || reply.status === "stale_epoch") {
        if (reply.status !== "stale_epoch") { uncertain.add(`${epoch}:${selected.id}`); setHeld(true); }
        setProbeError("Host or provider identity changed. Reload required."); return;
      }
      if (reply.status !== "ok") { setProbeError(reply.status === "busy" ? "A provider test is already running. Try again after it settles."
        : reply.status === "closed" ? "Host is closing; no new provider test can start." : "Provider test could not be confirmed."); return; }
      if (!["Ready", "Failed", "Disabled", "Unsupported", "Unknown", "Probing"].includes(reply.availability)) {
        setProbeError("Invalid provider status. Reload required."); return;
      }
      setProbeResult(reply);
    } catch { if (!controller.signal.aborted) {
      uncertain.add(`${epoch}:${selected.id}`); setHeld(true);
      setProbeError("Provider test outcome is uncertain. No retry is offered for this host and provider.");
    } }
    finally { if (active.current === controller) { active.current = null; pending.current = false; setProbing(false); } }
  }
  return <main className="configuration-page model-catalog-page" aria-label={t("Provider management")}>
    <header className="page-heading"><span className="eyebrow">{t("Desktop / Providers")}</span><h1>{t("Providers")}</h1>
      <p>{t("Configured host providers and cached initialization state. Test only the selected provider; no settings or credentials change here.")}</p></header>
    {!epoch ? <section aria-label={t("Read-only provider configuration")}><p role="status">{t("Catalog-only mode: provider configuration is read-only. No runtime test is available.")}</p>
        {catalogProviders?.slice(0, 32).map(provider => <p key={provider.id}><strong>{provider.name}</strong> ({provider.id}) · {provider.type} ·
          {" "}{t(provider.enabled ? "Enabled" : "Disabled")} · {t(provider.isDefault ? "Configured default" : "Not default")} · {t("model:")} {provider.defaultModel ?? t("Not configured")}</p>)}
        {!catalogProviders && <p role="status">{t("Configuration inventory is unavailable.")}</p>}
      </section>
      : <div className="model-catalog-layout">
        <section className="model-catalog-providers" aria-label={t("Configured providers")}><h2>{t("Configured providers")}</h2>
          {!inventory && !error && <p role="status">{t("Loading configured providers.")}</p>}
          {error && <p role="alert" className="error-text">{t(error)}</p>}
          {inventory?.providers.length === 0 && <p role="status">{t("No providers registered.")}</p>}
          {inventory?.providers.map(provider => <button type="button" key={provider.id} aria-pressed={providerId === provider.id}
            onClick={() => select(provider.id)}><strong className="with-logo"><ProviderIcon providerKey={provider.id} known={{ type: provider.type, name: provider.name }} size={15} />{provider.name}</strong><small>{provider.id} · {provider.enabled ? provider.availability : t("Disabled")}</small></button>)}
          {inventory?.truncated && <p role="status">{t("Showing 32 providers; others are omitted.")}</p>}
        </section>
        <section className="model-catalog-results" aria-label={t("Provider details")}><h2>{t("Provider details")}</h2>
          {!selected ? <p role="status">{t("Choose a provider to see its cached status. Choosing does not run a test.")}</p> : <article className="model-catalog-detail">
            <h3 className="with-logo"><ProviderIcon providerKey={selected.id} known={{ type: selected.type, name: selected.name }} size={18} />{selected.name}</h3><dl>
              <dt>{t("ID")}</dt><dd><code>{selected.id}</code></dd><dt>{t("Adapter type")}</dt><dd>{selected.type}</dd>
              <dt>{t("Enabled")}</dt><dd>{t(selected.enabled ? "Yes" : "No")}</dd><dt>{t("Configured default provider")}</dt><dd>{t(selected.isDefault ? "Yes" : "No")}</dd>
              <dt>{t("Configured default model")}</dt><dd>{selected.defaultModel ?? t("Not configured (not a discovered model)")}</dd>
              <dt>{t("Cached availability")}</dt><dd>{selected.availability}</dd>
              <dt>{t("Observed")}</dt><dd>{selected.observedAt ?? t("Not probed in this host")}</dd>
            </dl>
            <p>{t("Cached status is not a new test or authentication check. Authentication requirements are not classified by this host projection.")}</p>
            <button type="button" disabled={!selected.enabled || probing || held} onClick={() => void testProvider()}>
              {t(probing ? "Testing selected provider…" : "Test selected provider")}</button>
            {held && <p role="status">{t("This provider's probe may still be running. Its outcome cannot be recovered here; no retry is offered for this host.")}</p>}
            <button type="button" className="quiet-button" onClick={onOpenModels}>{t("Browse models")}</button>
            {probeResult && <p role="status">{t("Completed test for {id}: {availability}. This is provider initialization, not an authentication guarantee.", { id: selected.id, availability: probeResult.availability })}</p>}
            {probeError && <p role="alert" className="error-text">{t(probeError)}</p>}
          </article>}
        </section>
      </div>}
  </main>;
}
