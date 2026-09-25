import { useEffect, useLayoutEffect, useRef, useState } from "react";
import type { ConfigurationSnapshot } from "#neoastra";
import { createDraftIndicators, persistDraft, restoreDraft } from "./promptDraft";
import { AppIcon } from "./AppIcon";

export function ReadOnlyComposer({ sessionId, provider, configuration, onOpenConfiguration, draftIndicators, reason }: {
  sessionId: string; provider: string | null; configuration?: ConfigurationSnapshot; onOpenConfiguration: () => void;
  draftIndicators: ReturnType<typeof createDraftIndicators>; reason?: string;
}) {
  const [draft, setDraft] = useState(() => ({ text: restoreDraft(key => localStorage.getItem(key), sessionId), editGeneration: null as number | null }));
  const text = draft.text;
  const restoredText = useRef(text);
  useLayoutEffect(() => { draftIndicators.clear(sessionId); }, [draftIndicators, sessionId]);
  const [message, setMessage] = useState(reason ?? "Draft locally; sending requires an explicitly owned desktop host.");
  useEffect(() => { setMessage(reason ?? "Draft locally; sending requires an explicitly owned desktop host."); }, [reason]);
  useEffect(() => { draftIndicators.persisted(sessionId, draft.editGeneration,
    persistDraft((key, value) => localStorage.setItem(key, value), key => localStorage.removeItem(key), sessionId, draft.text));
  }, [sessionId, draft, draftIndicators]);
  return <section className="composer catalog-composer" aria-label="Message composer">
    <div className="prompt-options" aria-label="Session configuration">
      <label><span>Agent prompt</span><select aria-label="Agent prompt" value="recorded" disabled><option value="recorded">Recorded by session</option></select></label>
      <label><span>Model</span><select aria-label="Model" value="recorded" disabled><option value="recorded">Recorded by session</option></select></label>
      <label><span>Reasoning</span><select aria-label="Reasoning" value="recorded" disabled><option value="recorded">Recorded by session</option></select></label>
      <button type="button" className="prompt-state" onClick={onOpenConfiguration} aria-label="Open provider configuration" title={provider ?? "Provider not recorded"}><AppIcon name="settings" size={13} /><strong>{configuration?.providers.length ?? 0} providers</strong></button>
      <span className="prompt-state"><span>Context / MCP</span><strong>Requires runtime</strong></span>
    </div>
    <textarea id="catalog-prompt" aria-label="Message" maxLength={32768} value={text} onChange={event => {
      const value = event.target.value;
      setDraft({ text: value, editGeneration: draftIndicators.edit(sessionId, value, restoredText.current) });
    }}
      onKeyDown={event => { if (event.key === "Enter" && !event.shiftKey) { event.preventDefault(); setMessage(reason ?? "This explicit catalog-only launch is read-only; your draft remains saved."); } }}
      placeholder="Draft a prompt for this session…" />
    <div className="composer-footer"><span role="status">{message}</span><button type="button" disabled={!text.trim()} onClick={() => setMessage(reason ?? "This explicit catalog-only launch is read-only; your draft remains saved.")}>Send <AppIcon name="send" size={14} /></button></div>
  </section>;
}
