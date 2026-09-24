import { useEffect, useRef, useState } from "react";
import type { ModelCatalogModelsRequest, ModelCatalogModelsResponse, ModelCatalogProvidersRequest, ModelCatalogProvidersResponse, SessionChoicesRequest, SessionChoicesResponse, SessionSelection } from "#neoastra";
import type { CatalogNextSendTarget, NextSendResult, createNextSendSelectionStore } from "./nextSendSelection";

type ProvidersRead = (request: ModelCatalogProvidersRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<ModelCatalogProvidersResponse>;
type ModelsRead = (request: ModelCatalogModelsRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<ModelCatalogModelsResponse>;
type ChoicesRead = (request: SessionChoicesRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<SessionChoicesResponse>;

export function ModelCatalogPanel({ epoch, readProviders, readModels, target, readChoices, selections, pendingSend, pendingSelection, onApply }: {
  epoch: string | null; readProviders: ProvidersRead; readModels: ModelsRead;
  target: { epoch: string; sessionId: string } | null; readChoices: ChoicesRead;
  selections: ReturnType<typeof createNextSendSelectionStore>; pendingSend: boolean; pendingSelection: SessionSelection | null;
  onApply: (target: CatalogNextSendTarget, signal: AbortSignal) => Promise<NextSendResult>;
}) {
  const [providers, setProviders] = useState<ModelCatalogProvidersResponse>();
  const [providersError, setProvidersError] = useState("");
  const [providerId, setProviderId] = useState<string | null>(null);
  const [page, setPage] = useState<ModelCatalogModelsResponse>();
  const [modelsError, setModelsError] = useState("");
  const [query, setQuery] = useState("");
  const [modelId, setModelId] = useState<string | null>(null);
  const [choices, setChoices] = useState<SessionChoicesResponse>();
  const [choicesError, setChoicesError] = useState("");
  const [effort, setEffort] = useState("");
  const [applyError, setApplyError] = useState("");
  const [applying, setApplying] = useState(false);
  const actionRef = useRef(0);
  const applyingRef = useRef(false);
  const applyController = useRef<AbortController | null>(null);
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
  useEffect(() => {
    actionRef.current++; applyController.current?.abort(); applyingRef.current = false; setApplying(false);
    return () => applyController.current?.abort();
  }, [epoch, target?.sessionId, providerId, selected?.id]);
  useEffect(() => {
    setChoices(undefined); setChoicesError(""); setEffort(""); setApplyError("");
    if (!target || target.epoch !== epoch || !selected) return;
    const controller = new AbortController();
    void readChoices({ expectedEpoch: target.epoch, sessionId: target.sessionId },
      { signal: controller.signal, timeoutMilliseconds: 15000 }).then(value => {
      if (controller.signal.aborted) return;
      if (value.epoch !== target.epoch || value.status !== "ok" || value.sessionId !== target.sessionId || !value.current) {
        setChoicesError(value.status === "stale_epoch" || value.epoch !== target.epoch ? "Host identity changed. Reload required."
          : `Session choices unavailable (${value.status}).`); return;
      }
      if (typeof value.current.providerKey !== "string" || typeof value.current.agentPromptId !== "string"
        || !Array.isArray(value.prompts) || value.prompts.length > 64 || !Array.isArray(value.models) || value.models.length > 128
        || value.models.some(model => typeof model.id !== "string" || model.id.length > 256 || !Array.isArray(model.efforts)
          || model.efforts.length > 8 || model.efforts.some((effort: unknown) => typeof effort !== "string"))) {
        setChoicesError("Invalid session choices. No selection was applied."); return;
      }
      setChoices(value);
    }).catch(() => { if (!controller.signal.aborted) setChoicesError("Session choices could not be read."); });
    return () => controller.abort();
  }, [epoch, target?.epoch, target?.sessionId, selected?.id, readChoices]);
  const activeChoices = choices?.epoch === target?.epoch && choices?.sessionId === target?.sessionId && choices?.status === "ok" ? choices : undefined;
  const nextSend = activeChoices && target ? selections.get(target.epoch, target.sessionId, activeChoices) ?? activeChoices.current : null;
  const modelChoice = activeChoices?.models.find(value => value.id === selected?.id);
  const sameProvider = !!(activeChoices?.current && providerId === activeChoices.current.providerKey);
  async function applySelection() {
    if (!target || !selected || !sameProvider || !modelChoice || applyingRef.current || pendingSend) return;
    applyingRef.current = true; setApplying(true); setApplyError("");
    const action = ++actionRef.current;
    const controller = new AbortController();
    applyController.current = controller;
    try {
      const outcome = await onApply({ epoch: target.epoch, sessionId: target.sessionId, providerKey: providerId!, modelId: selected.id, reasoningEffort: effort || null }, controller.signal);
      if (action !== actionRef.current) return;
      if (outcome !== "applied") setApplyError(outcome === "pending" ? "An exact Send request is retained; its selection cannot be replaced."
        : outcome === "selection_changed" ? "Selected session changed. Choose a model for the current session instead."
        : outcome === "different_provider" ? "This model belongs to a different provider than the session."
        : outcome === "stale_epoch" ? "Host identity changed. Reload required."
        : "Model, effort or session prompt is unavailable for the next Send. Reopen the catalog to refresh choices.");
    } catch {
      if (action === actionRef.current) setApplyError("Next Send selection could not be validated. No change was applied.");
    } finally { if (action === actionRef.current) { applyController.current = null; applyingRef.current = false; setApplying(false); } }
  }
  const present = (value: number | string | null) => value == null ? "Unknown" : String(value);
  const flag = (value: boolean | null) => value == null ? "Unknown" : value ? "Yes" : "No";
  return <main className="configuration-page model-catalog-page" aria-label="Model catalog">
    <header className="page-heading"><span className="eyebrow">Desktop / Models</span><h1>Model catalog</h1>
      <p>Host-reported models. Only the selected session's next Send model can change here; authentication and global defaults cannot.</p></header>
    {!epoch ? <p role="status">Catalog-only mode has no owned model inventory. Configured default model names are not a model list.</p>
      : <div className="model-catalog-layout">
        <section className="model-catalog-providers" aria-label="Model providers"><h2>Providers</h2>
          {!activeProviders && !providersError && <p role="status">Loading providers…</p>}
          {providersError && <p role="alert" className="error-text">{providersError}</p>}
          {activeProviders?.providers.length === 0 && <p role="status">No providers are registered with this host.</p>}
          {activeProviders?.providers.map(provider => <button type="button" key={provider.id} aria-pressed={providerId === provider.id}
            onClick={() => { if (providerId === provider.id) return; setProviderId(provider.id); setPage(undefined); setModelId(null); setModelsError(""); }}>
            <strong>{provider.name}</strong><small>{provider.id} · {provider.enabled ? provider.availability : "Disabled"}</small></button>)}
          {activeProviders?.truncated && <p role="status">Showing {activeProviders.providers.length} providers; others are omitted by the bounded inventory.</p>}
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
              {activePage.truncated && <p role="status">Showing {activePage.models.length} reported models; others are omitted by the bounded inventory.</p>}
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
              </dl>
              <section className="model-catalog-next" aria-label="Next Send model selection"><h4>Next Send for selected session</h4>
                {!target ? <p>Unavailable without an exact selected owned session.</p> : <>
                  <p>Session: <code>{target.sessionId}</code>. Model changes affect its next Send only, not a running or queued turn.</p>
                  {!activeChoices && !choicesError && <p role="status">Loading session choices…</p>}
                  {choicesError && <p role="alert" className="error-text">{choicesError}</p>}
                  {activeChoices && <>
                    <p>Session-recorded model: {activeChoices.current?.modelId ?? "Unknown"} · provider: {activeChoices.current?.providerKey ?? "Unknown"}.</p>
                    <p>Next Send: {pendingSend ? `Retained exact request (${pendingSelection?.modelId ?? "host-selected model"})`
                      : `${nextSend?.modelId ?? "provider default"} · effort ${nextSend?.reasoningEffort ?? "model default"} · prompt ${nextSend?.agentPromptId ?? "Unknown"}`}.</p>
                    {!sameProvider && <p role="status">This provider is not the selected session's provider. Changing provider is not supported here.</p>}
                    {sameProvider && !modelChoice && <p role="status">This model is unavailable for this session's next Send.</p>}
                    {modelChoice && <label>Reasoning effort for next Send<select value={effort} onChange={event => setEffort(event.target.value)}>
                      <option value="">Model default</option>{modelChoice.efforts.map(value => <option key={value} value={value}>{value}</option>)}
                    </select></label>}
                    {pendingSend && <p role="status">Finish or reconcile the exact pending Send before changing its next selection.</p>}
                    <button type="button" disabled={!sameProvider || !modelChoice || pendingSend || applying || !nextSend}
                      onClick={() => void applySelection()}>{applying ? "Validating next Send…" : "Use model for next Send"}</button>
                  </>}
                  {applyError && <p role="alert" className="error-text">{applyError}</p>}
                </>}
              </section></article>}
          </>}
        </section>
      </div>}
  </main>;
}
