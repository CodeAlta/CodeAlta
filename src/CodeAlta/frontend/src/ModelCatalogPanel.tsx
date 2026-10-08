import { HTMLSelect } from "@blueprintjs/core";
import { ModelIcon, ProviderIcon } from "./ProviderIcon";
import { Cell, Column, Regions, SelectionModes, Table2 } from "@blueprintjs/table";
import "@blueprintjs/table/lib/css/table.css";
import { useEffect, useRef, useState } from "react";
import type { ModelCatalogModelsRequest, ModelCatalogModelsResponse, ModelCatalogProvidersRequest, ModelCatalogProvidersResponse, SessionChoicesRequest, SessionChoicesResponse, SessionSelection } from "#neoastra";
import type { CatalogNextSendTarget, NextSendResult, createNextSendSelectionStore } from "./nextSendSelection";
import { useShellLanguage } from "./shellLanguage";
import { inventoryNotice, type InventoryNotice } from "./inventoryNotice";

type ProvidersRead = (request: ModelCatalogProvidersRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<ModelCatalogProvidersResponse>;
type ModelsRead = (request: ModelCatalogModelsRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<ModelCatalogModelsResponse>;
type ChoicesRead = (request: SessionChoicesRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<SessionChoicesResponse>;

export function ModelCatalogPanel({ epoch, readProviders, readModels, target, readChoices, selections, pendingSend, pendingSelection, onApply }: {
  epoch: string | null; readProviders: ProvidersRead; readModels: ModelsRead;
  target: { epoch: string; sessionId: string } | null; readChoices: ChoicesRead;
  selections: ReturnType<typeof createNextSendSelectionStore>; pendingSend: boolean; pendingSelection: SessionSelection | null;
  onApply: (target: CatalogNextSendTarget, signal: AbortSignal) => Promise<NextSendResult>;
}) {
  const { t, locale } = useShellLanguage();
  const [providers, setProviders] = useState<ModelCatalogProvidersResponse>();
  const [providersError, setProvidersError] = useState<InventoryNotice>("");
  const [providerId, setProviderId] = useState<string | null>(null);
  const [pages, setPages] = useState<ReadonlyMap<string, ModelCatalogModelsResponse>>(new Map());
  const [modelErrors, setModelErrors] = useState<ReadonlyMap<string, InventoryNotice>>(new Map());
  const [loadingModels, setLoadingModels] = useState(false);
  const [query, setQuery] = useState("");
  const [modelId, setModelId] = useState<string | null>(null);
  const [choices, setChoices] = useState<SessionChoicesResponse>();
  const [choicesError, setChoicesError] = useState<InventoryNotice>("");
  const [effort, setEffort] = useState("");
  const [applyError, setApplyError] = useState<InventoryNotice>("");
  const [applying, setApplying] = useState(false);
  const actionRef = useRef(0);
  const applyingRef = useRef(false);
  const applyController = useRef<AbortController | null>(null);
  useEffect(() => {
    setProviders(undefined); setProvidersError(""); setProviderId(null); setPages(new Map()); setModelErrors(new Map()); setModelId(null);
    if (!epoch) return;
    const controller = new AbortController();
    void readProviders({ expectedEpoch: epoch }, { signal: controller.signal, timeoutMilliseconds: 15000 }).then(value => {
      if (controller.signal.aborted) return;
      if (value.epoch !== epoch || value.status !== "ok") {
        setProvidersError(value.status === "stale_epoch" || value.epoch !== epoch ? "Host identity changed. Reload required." : { key: "Model providers unavailable ({status}).", status: value.status }); return;
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
    setPages(new Map()); setModelErrors(new Map()); setProviderId(null); setModelId(null);
    if (!epoch || providers?.epoch !== epoch) return;
    const enabled = providers.providers.filter(provider => provider.enabled);
    if (!enabled.length) return;
    const controller = new AbortController();
    setLoadingModels(true);
    // One bounded read per enabled provider, in parallel; each result or failure is shown as it arrives.
    void Promise.all(enabled.map(provider => readModels({ expectedEpoch: epoch, providerId: provider.id }, { signal: controller.signal, timeoutMilliseconds: 15000 }).then(value => {
      if (controller.signal.aborted) return;
      const fail = (notice: InventoryNotice) => setModelErrors(current => new Map(current).set(provider.id, notice));
      if (value.epoch !== epoch || value.status === "stale_epoch") { fail("Host identity changed. Reload required."); return; }
      if (value.status !== "ok") { fail({ key: "Model inventory unavailable ({status}).", status: value.availability === "Unknown" ? value.status : value.availability }); return; }
      if (value.providerId?.toLowerCase() !== provider.id.toLowerCase() || !Array.isArray(value.models) || value.models.length > 128
        || value.models.some(model => typeof model.id !== "string" || !model.id || model.id.length > 256 || typeof model.name !== "string")) {
        fail("Invalid model inventory. Reload required."); return;
      }
      setPages(current => new Map(current).set(provider.id, value));
    }).catch(() => { if (!controller.signal.aborted) setModelErrors(current => new Map(current).set(provider.id, "Model inventory could not be read.")); })))
      .finally(() => { if (!controller.signal.aborted) setLoadingModels(false); });
    return () => controller.abort();
  }, [epoch, providers, readModels]);
  const activeProviders = providers?.epoch === epoch ? providers : undefined;
  const terms = query.toLocaleLowerCase().trim().split(/\s+/).filter(Boolean);
  // Every provider's models in one list, provider order first, each provider's reported order kept.
  const rows = (activeProviders?.providers ?? []).flatMap(provider => (pages.get(provider.id)?.models ?? []).map(model => ({ provider, model })));
  const visible = rows.filter(({ provider, model }) => terms.every(term => `${provider.id} ${provider.name} ${model.id} ${model.name} ${model.description ?? ""}`.toLocaleLowerCase().includes(term)));
  const selectedRow = visible.findIndex(row => row.provider.id === providerId && row.model.id === modelId);
  const selected = selectedRow >= 0 ? visible[selectedRow].model : undefined;
  const truncated = [...pages.values()].some(value => value.truncated);

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
          : { key: "Session choices unavailable ({status}).", status: value.status }); return;
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
  const present = (value: number | string | null) => value == null ? t("Unknown") : String(value);
  const flag = (value: boolean | null) => t(value == null ? "Unknown" : value ? "Yes" : "No");
  const count = (value: number | string | null) => value == null ? "" : typeof value === "number" ? value.toLocaleString(locale) : String(value);
  const mark = (value: boolean | null) => value == null ? "" : value ? "✓" : "–";
  return <main className="configuration-page model-catalog-page" aria-label={t("Model catalog")}>
    <header className="page-heading"><span className="eyebrow">{t("Desktop / Models")}</span><h1>{t("Model catalog")}</h1>
      <p>{t("Models reported by your providers.")}</p></header>
    {!epoch ? <p role="status">{t("Catalog-only mode has no owned model inventory. Configured default model names are not a model list.")}</p>
      : <div className="model-catalog-all">
        <section className="model-catalog-results" aria-label={t("Provider models")}>
          {!activeProviders && !providersError && <p role="status">{t("Loading providers…")}</p>}
          {providersError && <p role="alert" className="error-text">{inventoryNotice(locale, providersError)}</p>}
          {activeProviders?.providers.length === 0 && <p role="status">{t("No providers are registered with this host.")}</p>}
          {activeProviders && <>
            <label>{t("Search models")}<input type="search" value={query} onChange={event => setQuery(event.target.value)} placeholder={t("Name, ID or description")} /></label>
            {[...modelErrors].map(([id, notice]) => <p key={id} role="alert" className="error-text"><strong>{activeProviders.providers.find(provider => provider.id === id)?.name ?? id}</strong>: {inventoryNotice(locale, notice)}</p>)}
            {loadingModels && rows.length === 0 && <p role="status">{t("Loading models…")}</p>}
            {!loadingModels && rows.length === 0 && modelErrors.size === 0 && <p role="status">{t("This provider reported no models. A configured default is not inventory.")}</p>}
            {rows.length > 0 && visible.length === 0 && <p role="status">{t("No models match this search.")}</p>}
            {visible.length > 0 && <div className="model-catalog-grid">
              <Table2 numRows={visible.length} enableRowHeader={false} enableMultipleSelection={false} defaultRowHeight={28}
                columnWidths={[130, 230, 230, 112, 100, 108, 88, 64, 92, 104]} selectionModes={SelectionModes.ROWS_AND_CELLS}
                selectedRegionTransform={region => region.rows ? Regions.row(region.rows[0]) : region}
                selectedRegions={selectedRow >= 0 ? [Regions.row(selectedRow)] : []}
                onSelection={regions => { const row = regions[0]?.rows?.[0]; if (row !== undefined && visible[row]) { setProviderId(visible[row].provider.id); setModelId(visible[row].model.id); } }}>
                <Column name={t("Provider")} cellRenderer={row => <Cell><span className="with-logo"><ProviderIcon providerKey={visible[row].provider.id} known={{ type: visible[row].provider.type, name: visible[row].provider.name }} size={14} />{visible[row].provider.name}</span></Cell>} />
                <Column name={t("Model")} cellRenderer={row => <Cell><span className="with-logo"><ModelIcon modelId={visible[row].model.id} providerKey={visible[row].provider.id} size={14} /><strong>{visible[row].model.name}</strong></span></Cell>} />
                <Column name={t("ID")} cellRenderer={row => <Cell className="bp6-monospace-text">{visible[row].model.id}</Cell>} />
                <Column name={t("Context tokens")} cellRenderer={row => <Cell>{count(visible[row].model.contextTokens)}</Cell>} />
                <Column name={t("Input tokens")} cellRenderer={row => <Cell>{count(visible[row].model.inputTokens)}</Cell>} />
                <Column name={t("Output tokens")} cellRenderer={row => <Cell>{count(visible[row].model.outputTokens)}</Cell>} />
                <Column name={t("Reasoning")} cellRenderer={row => <Cell>{mark(visible[row].model.reasoning)}</Cell>} />
                <Column name={t("Tools")} cellRenderer={row => <Cell>{mark(visible[row].model.tools)}</Cell>} />
                <Column name={t("Image input")} cellRenderer={row => <Cell>{mark(visible[row].model.imageInput)}</Cell>} />
                <Column name={t("Default effort")} cellRenderer={row => <Cell>{visible[row].model.defaultEffort ?? ""}</Cell>} />
              </Table2>
            </div>}
            {rows.length > 0 && <p role="status" className="bp6-text-muted">{t("{count} models", { count: visible.length })}{truncated ? ` · ${t("Some provider inventories are bounded; more models may exist.")}` : ""}</p>}
            {selected && <article className="model-catalog-detail" aria-label={t("Details for {name}", { name: selected.name })}><h3 className="with-logo"><ModelIcon modelId={selected.id} providerKey={providerId} size={18} />{selected.name}</h3><p><code>{providerId} / {selected.id}</code></p>
              <p>{selected.description ?? t("No description reported.")}</p><dl>
                <dt>{t("Context tokens")}</dt><dd>{present(selected.contextTokens)}</dd>
                <dt>{t("Input tokens")}</dt><dd>{present(selected.inputTokens)}</dd>
                <dt>{t("Output tokens")}</dt><dd>{present(selected.outputTokens)}</dd>
                <dt>{t("Reasoning")}</dt><dd>{flag(selected.reasoning)}</dd>
                <dt>{t("Tools")}</dt><dd>{flag(selected.tools)}</dd>
                <dt>{t("Structured output")}</dt><dd>{flag(selected.structuredOutput)}</dd>
                <dt>{t("Image input")}</dt><dd>{flag(selected.imageInput)}</dd>
                <dt>{t("Supported efforts")}</dt><dd>{selected.efforts.length ? selected.efforts.join(", ") : t("Unknown")}</dd>
                <dt>{t("Default effort")}</dt><dd>{present(selected.defaultEffort)}</dd>
                <dt>{t("Pricing")}</dt><dd>{t("Unknown (not exposed by the host model inventory)")}</dd>
              </dl>
              <section className="model-catalog-next" aria-label={t("Next Send model selection")}><h4>{t("Next Send for selected session")}</h4>
                {!target ? <p>{t("Unavailable without an exact selected owned session.")}</p> : <>
                  <p>{t("Session:")} <code>{target.sessionId}</code>. {t("Model changes affect its next Send only, not a running or queued turn.")}</p>
                  {!activeChoices && !choicesError && <p role="status">{t("Loading session choices…")}</p>}
                  {choicesError && <p role="alert" className="error-text">{inventoryNotice(locale, choicesError)}</p>}
                  {activeChoices && <>
                    <p>{t("Session-recorded model: {model} · provider: {provider}.", { model: activeChoices.current?.modelId ?? t("Unknown"), provider: activeChoices.current?.providerKey ?? t("Unknown") })}</p>
                    <p>{t("Next Send:")} {pendingSend ? t("Retained exact request ({selection})", { selection: pendingSelection?.modelId ?? t("host-selected model") })
                      : t("{model} · effort {effort} · prompt {prompt}", { model: nextSend?.modelId ?? t("Unknown"), effort: nextSend?.reasoningEffort ?? t("None"), prompt: nextSend?.agentPromptId ?? t("Unknown") })}.</p>
                    {!sameProvider && <p role="status">{t("This provider is not the selected session's provider. Changing provider is not supported here.")}</p>}
                    {sameProvider && !modelChoice && <p role="status">{t("This model is unavailable for this session's next Send.")}</p>}
                    {modelChoice && modelChoice.efforts.length > 0 && <label>{t("Reasoning effort for next Send")}<HTMLSelect value={effort || modelChoice.startEffort || ""} onChange={event => setEffort(event.target.value)}>
                      {modelChoice.efforts.map(value => <option key={value} value={value}>{value}</option>)}
                    </HTMLSelect></label>}
                    {pendingSend && <p role="status">{t("Finish or reconcile the exact pending Send before changing its next selection.")}</p>}
                    <button type="button" disabled={!sameProvider || !modelChoice || pendingSend || applying || !nextSend}
                      onClick={() => void applySelection()}>{t(applying ? "Validating next Send…" : "Use model for next Send")}</button>
                  </>}
                  {applyError && <p role="alert" className="error-text">{inventoryNotice(locale, applyError)}</p>}
                </>}
              </section></article>}
          </>}
        </section>
      </div>}
  </main>;
}
