import { useEffect, useState } from "react";
import type { ModelCatalogModelsRequest, ModelCatalogModelsResponse, ModelCatalogProvidersRequest, ModelCatalogProvidersResponse } from "#neoastra";

type ProvidersRead = (request: ModelCatalogProvidersRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<ModelCatalogProvidersResponse>;
type ModelsRead = (request: ModelCatalogModelsRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<ModelCatalogModelsResponse>;

export function ModelCatalogPanel({ epoch, readProviders, readModels }: { epoch: string | null; readProviders: ProvidersRead; readModels: ModelsRead }) {
  const [providers, setProviders] = useState<ModelCatalogProvidersResponse>();
  const [providersError, setProvidersError] = useState("");
  const [providerId, setProviderId] = useState<string | null>(null);
  const [page, setPage] = useState<ModelCatalogModelsResponse>();
  const [modelsError, setModelsError] = useState("");
  const [query, setQuery] = useState("");
  const [modelId, setModelId] = useState<string | null>(null);
  useEffect(() => {
    setProviders(undefined); setProvidersError(""); setProviderId(null); setPage(undefined); setModelsError(""); setModelId(null);
    if (!epoch) return;
    const controller = new AbortController();
    void readProviders({ expectedEpoch: epoch }, { signal: controller.signal, timeoutMilliseconds: 15000 }).then(value => {
      if (controller.signal.aborted) return;
      if (value.epoch !== epoch || value.status !== "ok") {
        setProvidersError(value.status === "stale_epoch" || value.epoch !== epoch ? "Host identity changed. Reload required." : `Model providers unavailable (${value.status}).`); return;
      }
      if (!Array.isArray(value.providers) || value.providers.length > 32 || value.providers.some(provider =>
        typeof provider.id !== "string" || !provider.id || provider.id.length > 256 || typeof provider.name !== "string")) {
        setProvidersError("Invalid provider inventory. Reload required."); return;
      }
      setProviders(value);
    }).catch(() => { if (!controller.signal.aborted) setProvidersError("Model provider inventory could not be read."); });
    return () => controller.abort();
  }, [epoch, readProviders]);
  useEffect(() => {
    setPage(undefined); setModelsError(""); setModelId(null);
    if (!epoch || !providerId || providers?.epoch !== epoch || !providers.providers.some(provider => provider.id === providerId)) return;
    const controller = new AbortController();
    void readModels({ expectedEpoch: epoch, providerId }, { signal: controller.signal, timeoutMilliseconds: 15000 }).then(value => {
      if (controller.signal.aborted) return;
      if (value.epoch !== epoch || value.status === "stale_epoch") {
        setModelsError("Host identity changed. Reload required."); return;
      }
      if (value.status !== "ok") {
        setModelsError(`Model inventory unavailable (${value.availability === "Unknown" ? value.status : value.availability}).`); return;
      }
      if (value.providerId?.toLowerCase() !== providerId.toLowerCase() || !Array.isArray(value.models) || value.models.length > 128
        || value.models.some(model => typeof model.id !== "string" || !model.id || model.id.length > 256 || typeof model.name !== "string")) {
        setModelsError("Invalid model inventory. Reload required."); return;
      }
      setPage(value);
    }).catch(() => { if (!controller.signal.aborted) setModelsError("Model inventory could not be read."); });
    return () => controller.abort();
  }, [epoch, providerId, providers, readModels]);
  const activeProviders = providers?.epoch === epoch ? providers : undefined;
  const activePage = page?.epoch === epoch && page.providerId?.toLowerCase() === providerId?.toLowerCase() ? page : undefined;
  const terms = query.toLocaleLowerCase().trim().split(/\s+/).filter(Boolean);
  const visible = (activePage?.models ?? []).filter(model => terms.every(term => `${model.id} ${model.name} ${model.description ?? ""}`.toLocaleLowerCase().includes(term)));
  const selected = visible.find(model => model.id === modelId);
  const present = (value: number | string | null) => value == null ? "Unknown" : String(value);
  const flag = (value: boolean | null) => value == null ? "Unknown" : value ? "Yes" : "No";
  return <main className="configuration-page model-catalog-page" aria-label="Model catalog">
    <header className="page-heading"><span className="eyebrow">Desktop / Models</span><h1>Model catalog</h1>
      <p>Read-only host-reported models. Provider authentication, session selection and defaults are not managed here.</p></header>
    {!epoch ? <p role="status">Catalog-only mode has no owned model inventory. Configured default model names are not a model list.</p>
      : <div className="model-catalog-layout">
        <section className="model-catalog-providers" aria-label="Model providers"><h2>Providers</h2>
          {!activeProviders && !providersError && <p role="status">Loading providers…</p>}
          {providersError && <p role="alert" className="error-text">{providersError}</p>}
          {activeProviders?.providers.length === 0 && <p role="status">No providers are registered with this host.</p>}
          {activeProviders?.providers.map(provider => <button type="button" key={provider.id} aria-pressed={providerId === provider.id}
            onClick={() => { if (providerId === provider.id) return; setProviderId(provider.id); setPage(undefined); setModelId(null); setModelsError(""); }}>
            <strong>{provider.name}</strong><small>{provider.id} · {provider.enabled ? provider.availability : "Disabled"}</small></button>)}
          {activeProviders?.truncated && <p role="status">Showing the first 32 providers; others are omitted.</p>}
        </section>
        <section className="model-catalog-results" aria-label="Provider models"><h2>Models</h2>
          {!providerId ? <p role="status">Choose a provider to load its reported models.</p> : <>
            <label>Search models<input type="search" value={query} onChange={event => setQuery(event.target.value)} placeholder="Name, ID or description" /></label>
            {!activePage && !modelsError && <p role="status">Loading models for {providerId}…</p>}
            {modelsError && <p role="alert" className="error-text">{modelsError}</p>}
            {activePage && <>
              {activePage.models.length === 0 && <p role="status">This provider reported no models. A configured default is not inventory.</p>}
              {activePage.models.length > 0 && visible.length === 0 && <p role="status">No models match this search.</p>}
              <div className="model-catalog-list">{visible.map(model => <button type="button" key={model.id} aria-pressed={selected?.id === model.id}
                onClick={() => setModelId(model.id)}><strong>{model.name}</strong><small>{model.id}</small></button>)}</div>
              {activePage.truncated && <p role="status">Showing the first 128 reported models; others are omitted.</p>}
            </>}
            {selected && <article className="model-catalog-detail" aria-label={`Details for ${selected.name}`}><h3>{selected.name}</h3><p><code>{selected.id}</code></p>
              <p>{selected.description ?? "No description reported."}</p><dl>
                <dt>Context tokens</dt><dd>{present(selected.contextTokens)}</dd>
                <dt>Input tokens</dt><dd>{present(selected.inputTokens)}</dd>
                <dt>Output tokens</dt><dd>{present(selected.outputTokens)}</dd>
                <dt>Reasoning</dt><dd>{flag(selected.reasoning)}</dd>
                <dt>Tools</dt><dd>{flag(selected.tools)}</dd>
                <dt>Structured output</dt><dd>{flag(selected.structuredOutput)}</dd>
                <dt>Image input</dt><dd>{flag(selected.imageInput)}</dd>
                <dt>Supported efforts</dt><dd>{selected.efforts.length ? selected.efforts.join(", ") : "Unknown"}</dd>
                <dt>Default effort</dt><dd>{present(selected.defaultEffort)}</dd>
                <dt>Pricing</dt><dd>Unknown (not exposed by the host model inventory)</dd>
              </dl></article>}
          </>}
        </section>
      </div>}
  </main>;
}
