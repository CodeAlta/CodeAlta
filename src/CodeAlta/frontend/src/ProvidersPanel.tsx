import { useEffect, useRef, useState } from "react";
import type { ConfigurationProvider, ModelCatalogProbeRequest, ModelCatalogProbeResponse, ModelCatalogProvidersRequest, ModelCatalogProvidersResponse } from "#neoastra";

type Read = (request: ModelCatalogProvidersRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<ModelCatalogProvidersResponse>;
type Probe = (request: ModelCatalogProbeRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<ModelCatalogProbeResponse>;

export function ProvidersPanel({ epoch, read, probe, catalogProviders, holds, onOpenModels }: {
  epoch: string | null; read: Read; probe: Probe; catalogProviders?: readonly ConfigurationProvider[];
  holds?: Set<string>; onOpenModels: () => void;
}) {
  const [inventory, setInventory] = useState<ModelCatalogProvidersResponse>();
  const [error, setError] = useState("");
  const [providerId, setProviderId] = useState<string | null>(null);
  const [probing, setProbing] = useState(false);
  const [probeResult, setProbeResult] = useState<ModelCatalogProbeResponse>();
  const [probeError, setProbeError] = useState("");
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
  return <main className="configuration-page model-catalog-page" aria-label="Provider management">
    <header className="page-heading"><span className="eyebrow">Desktop / Providers</span><h1>Providers</h1>
      <p>Configured host providers and cached initialization state. Test only the selected provider; no settings or credentials change here.</p></header>
    {!epoch ? <section aria-label="Read-only provider configuration"><p role="status">Catalog-only mode: provider configuration is read-only. No runtime test is available.</p>
        {catalogProviders?.slice(0, 32).map(provider => <p key={provider.id}><strong>{provider.name}</strong> ({provider.id}) · {provider.type} ·
          {provider.enabled ? " Enabled" : " Disabled"} · {provider.isDefault ? "Configured default" : "Not default"} · model: {provider.defaultModel ?? "Not configured"}</p>)}
        {!catalogProviders && <p role="status">Configuration inventory is unavailable.</p>}
      </section>
      : <div className="model-catalog-layout">
        <section className="model-catalog-providers" aria-label="Configured providers"><h2>Configured providers</h2>
          {!inventory && !error && <p role="status">Loading configured providers.</p>}
          {error && <p role="alert" className="error-text">{error}</p>}
          {inventory?.providers.length === 0 && <p role="status">No providers registered.</p>}
          {inventory?.providers.map(provider => <button type="button" key={provider.id} aria-pressed={providerId === provider.id}
            onClick={() => select(provider.id)}><strong>{provider.name}</strong><small>{provider.id} · {provider.enabled ? provider.availability : "Disabled"}</small></button>)}
          {inventory?.truncated && <p role="status">Showing 32 providers; others are omitted.</p>}
        </section>
        <section className="model-catalog-results" aria-label="Provider details"><h2>Provider details</h2>
          {!selected ? <p role="status">Choose a provider to see its cached status. Choosing does not run a test.</p> : <article className="model-catalog-detail">
            <h3>{selected.name}</h3><dl>
              <dt>ID</dt><dd><code>{selected.id}</code></dd><dt>Adapter type</dt><dd>{selected.type}</dd>
              <dt>Enabled</dt><dd>{selected.enabled ? "Yes" : "No"}</dd><dt>Configured default provider</dt><dd>{selected.isDefault ? "Yes" : "No"}</dd>
              <dt>Configured default model</dt><dd>{selected.defaultModel ?? "Not configured (not a discovered model)"}</dd>
              <dt>Cached availability</dt><dd>{selected.availability}</dd>
              <dt>Observed</dt><dd>{selected.observedAt ?? "Not probed in this host"}</dd>
            </dl>
            <p>Cached status is not a new test or authentication check. Authentication requirements are not classified by this host projection.</p>
            <button type="button" disabled={!selected.enabled || probing || held} onClick={() => void testProvider()}>
              {probing ? "Testing selected provider…" : "Test selected provider"}</button>
            {held && <p role="status">This provider's probe may still be running. Its outcome cannot be recovered here; no retry is offered for this host.</p>}
            <button type="button" className="quiet-button" onClick={onOpenModels}>Browse models</button>
            {probeResult && <p role="status">Completed test for {selected.id}: {probeResult.availability}. This is provider initialization, not an authentication guarantee.</p>}
            {probeError && <p role="alert" className="error-text">{probeError}</p>}
          </article>}
        </section>
      </div>}
  </main>;
}
