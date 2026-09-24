import { useEffect, useRef, useState } from "react";
import type { PromptCatalogRequest, PromptCatalogResponse, SessionChoicesRequest, SessionChoicesResponse, SessionSelection } from "#neoastra";
import type { NextSendResult, PromptNextSendTarget, createNextSendSelectionStore } from "./nextSendSelection";

export function PromptCatalogPanel({ epoch, target, readPrompts, readChoices, selections, pendingSend, pendingSelection, onApply }: {
  epoch: string | null; target: { epoch: string; sessionId: string } | null;
  readPrompts: (request: PromptCatalogRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<PromptCatalogResponse>;
  readChoices: (request: SessionChoicesRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<SessionChoicesResponse>;
  selections: ReturnType<typeof createNextSendSelectionStore>; pendingSend: boolean; pendingSelection: SessionSelection | null;
  onApply: (target: PromptNextSendTarget, signal: AbortSignal) => Promise<NextSendResult>;
}) {
  const [page, setPage] = useState<PromptCatalogResponse>();
  const [error, setError] = useState("");
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [choices, setChoices] = useState<SessionChoicesResponse>();
  const [choicesError, setChoicesError] = useState("");
  const [applyError, setApplyError] = useState("");
  const [applying, setApplying] = useState(false);
  const applyController = useRef<AbortController | null>(null);
  const generation = useRef(0);
  const applyingRef = useRef(false);
  useEffect(() => {
    setPage(undefined); setError(""); setSelectedId(null); setChoices(undefined); setChoicesError("");
    if (!target || target.epoch !== epoch) return;
    const controller = new AbortController();
    void readPrompts({ expectedEpoch: target.epoch, sessionId: target.sessionId },
      { signal: controller.signal, timeoutMilliseconds: 15000 }).then(value => {
      if (controller.signal.aborted) return;
      if (value.epoch !== target.epoch || value.status !== "ok" || value.sessionId !== target.sessionId) {
        setError(value.status === "stale_epoch" || value.epoch !== target.epoch ? "Host identity changed. Reload required."
          : `Prompt inventory unavailable (${value.status}).`); return;
      }
      if (!Array.isArray(value.prompts) || value.prompts.length > 64 || value.prompts.some(prompt =>
        typeof prompt.id !== "string" || !prompt.id || prompt.id.length > 256 || typeof prompt.name !== "string"
        || prompt.name.length > 256 || prompt.description != null && (typeof prompt.description !== "string" || prompt.description.length > 1024)
        || typeof prompt.builtIn !== "boolean" || typeof prompt.appended !== "boolean" || typeof prompt.bodyTruncated !== "boolean"
        || typeof prompt.body !== "string" || prompt.body.length > 2048
        || !["BuiltIn", "UserGlobal", "Project"].includes(prompt.scope))) {
        setError("Invalid prompt inventory. Reload required."); return;
      }
      setPage(value);
    }).catch(() => { if (!controller.signal.aborted) setError("Prompt inventory could not be read."); });
    return () => controller.abort();
  }, [epoch, target?.epoch, target?.sessionId, readPrompts]);
  const activePage = page?.epoch === epoch && page?.sessionId === target?.sessionId ? page : undefined;
  const selected = activePage?.prompts.find(prompt => prompt.id === selectedId);
  useEffect(() => {
    generation.current++; applyController.current?.abort(); applyingRef.current = false; setApplying(false); setApplyError("");
    return () => applyController.current?.abort();
  }, [epoch, target?.sessionId, selectedId]);
  useEffect(() => {
    setChoices(undefined); setChoicesError("");
    if (!target || target.epoch !== epoch || !selected) return;
    const controller = new AbortController();
    void readChoices({ expectedEpoch: target.epoch, sessionId: target.sessionId },
      { signal: controller.signal, timeoutMilliseconds: 15000 }).then(value => {
      if (controller.signal.aborted) return;
      if (value.epoch !== target.epoch || value.status !== "ok" || value.sessionId !== target.sessionId || !value.current
        || !Array.isArray(value.prompts) || value.prompts.length > 64 || !Array.isArray(value.models) || value.models.length > 128) {
        setChoicesError(value.status === "stale_epoch" || value.epoch !== target.epoch ? "Host identity changed. Reload required."
          : "Session choices unavailable. No selection was applied."); return;
      }
      setChoices(value);
    }).catch(() => { if (!controller.signal.aborted) setChoicesError("Session choices could not be read."); });
    return () => controller.abort();
  }, [epoch, target?.epoch, target?.sessionId, selected?.id, readChoices]);
  const activeChoices = choices?.epoch === target?.epoch && choices?.sessionId === target?.sessionId && choices?.status === "ok" ? choices : undefined;
  const nextSend = activeChoices && target ? selections.get(target.epoch, target.sessionId, activeChoices) ?? activeChoices.current : null;
  const available = !!activeChoices?.prompts.some(prompt => prompt.id === selected?.id);
  async function apply() {
    if (!target || !selected || !available || pendingSend || applyingRef.current) return;
    applyingRef.current = true; setApplying(true); setApplyError("");
    const action = ++generation.current;
    const controller = new AbortController(); applyController.current = controller;
    try {
      const result = await onApply({ epoch: target.epoch, sessionId: target.sessionId, promptId: selected.id }, controller.signal);
      if (action !== generation.current) return;
      if (result !== "applied") setApplyError(result === "pending" ? "An exact Send request is retained; its selection cannot be replaced."
        : result === "selection_changed" ? "Selected session changed. Choose a prompt for the current session instead."
        : result === "stale_epoch" ? "Host identity changed. Reload required."
        : "Prompt or existing model/effort is unavailable for the next Send. Reopen the catalog to refresh choices.");
    } catch { if (action === generation.current) setApplyError("Next Send selection could not be validated. No change was applied."); }
    finally { if (action === generation.current) { applyController.current = null; applyingRef.current = false; setApplying(false); } }
  }
  return <main className="configuration-page prompt-catalog-page" aria-label="Agent prompts">
    <header className="page-heading"><span className="eyebrow">Desktop / Agent prompts</span><h1>Agent prompts</h1>
      <p>Effective host-discovered prompts for the selected session. Inspection is read-only; selection affects only its next Send.</p></header>
    {!epoch ? <p role="status">Catalog-only mode has no owned prompt inventory.</p>
      : !target ? <p role="status">Select an owned session to inspect its prompt scope.</p>
      : <div className="model-catalog-layout"><section className="model-catalog-providers" aria-label="Prompt inventory"><h2>Prompts</h2>
        {!activePage && !error && <p role="status">Loading prompts.</p>}
        {error && <p role="alert" className="error-text">{error}</p>}
        {activePage?.prompts.length === 0 && <p role="status">No effective prompts were discovered in this session's scope.</p>}
        {activePage?.prompts.map(prompt => <button type="button" key={prompt.id} aria-pressed={selectedId === prompt.id}
          onClick={() => setSelectedId(prompt.id)}><strong>{prompt.name}</strong><small>{prompt.id} · {prompt.scope}</small></button>)}
        {activePage?.truncated && <p role="status">Showing {activePage.prompts.length} prompts; others were omitted by the bounded inventory.</p>}
      </section><section className="model-catalog-results" aria-label="Prompt details"><h2>Details</h2>
        {!selected ? <p>Select a prompt to inspect its effective content.</p> : <article className="model-catalog-detail">
          <h3>{selected.name}</h3><p>ID: <code>{selected.id}</code></p><p>{selected.description ?? "No description supplied."}</p>
          <p>Scope: {selected.scope}. {selected.builtIn ? "Built-in, read-only." : "Read-only here; edit in the TUI."}
            {selected.appended && " Effective content may compose lower-precedence sources; the full source chain is not shown."}</p>
          <h4>Effective agent prompt body</h4><pre className="prompt-catalog-body">{selected.body}</pre>
          {selected.bodyTruncated && <p role="status">Content truncated to 2,048 characters. This is not the full prompt.</p>}
          <section className="model-catalog-next" aria-label="Next Send prompt selection"><h4>Next Send for selected session</h4>
            <p>Session: <code>{target.sessionId}</code>. No running or retained turn changes.</p>
            {!activeChoices && !choicesError && <p role="status">Loading session choices.</p>}
            {choicesError && <p role="alert" className="error-text">{choicesError}</p>}
            {activeChoices && <><p>Session-recorded prompt: {activeChoices.current?.agentPromptId ?? "Unknown"}.</p>
              <p>Next Send: {pendingSend ? `Retained exact request (${pendingSelection?.agentPromptId ?? "host-selected prompt"})`
                : `${nextSend?.agentPromptId ?? "Unknown"} · model ${nextSend?.modelId ?? "provider default"} · effort ${nextSend?.reasoningEffort ?? "model default"}`}.</p>
              {!available && <p role="status">This prompt is unavailable for this session's next Send.</p>}
              {pendingSend && <p role="status">Finish or reconcile the exact pending Send before changing its next selection.</p>}
              <button type="button" disabled={!available || pendingSend || applying || !nextSend} onClick={() => void apply()}>
                {applying ? "Validating next Send." : "Use prompt for next Send"}</button></>}
            {applyError && <p role="alert" className="error-text">{applyError}</p>}
          </section></article>}
      </section></div>}
  </main>;
}
