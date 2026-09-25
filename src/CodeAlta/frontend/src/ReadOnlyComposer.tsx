import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { createDraftIndicators, persistDraft, restoreDraft } from "./promptDraft";
import { promptEditorHeight } from "./workspacePresentation";
import { dispatchTransientComposerKey } from "./composerKeyboard";

export function ReadOnlyComposer({ sessionId, provider, draftIndicators, reason, onOpenHelp, onOpenPalette }: {
  sessionId: string; provider: string | null;
  draftIndicators: ReturnType<typeof createDraftIndicators>; reason?: string;
  onOpenHelp?: () => void; onOpenPalette?: () => void;
}) {
  const [draft, setDraft] = useState(() => ({ text: restoreDraft(key => localStorage.getItem(key), sessionId), editGeneration: null as number | null }));
  const text = draft.text;
  const restoredText = useRef(text);
  const promptInput = useRef<HTMLTextAreaElement>(null);
  useLayoutEffect(() => { draftIndicators.clear(sessionId); }, [draftIndicators, sessionId]);
  useEffect(() => { draftIndicators.persisted(sessionId, draft.editGeneration,
    persistDraft((key, value) => localStorage.setItem(key, value), key => localStorage.removeItem(key), sessionId, draft.text));
  }, [sessionId, draft, draftIndicators]);
  useLayoutEffect(() => {
    const input = promptInput.current;
    if (!input) return;
    const measure = () => {
      input.style.height = "auto";
      input.style.height = `${promptEditorHeight(input.scrollHeight, window.innerHeight)}px`;
    };
    measure();
    const workspace = input.closest<HTMLElement>(".session-workspace");
    let width = workspace?.clientWidth;
    const observer = new ResizeObserver(() => {
      if (workspace && workspace.clientWidth !== width) { width = workspace.clientWidth; measure(); }
    });
    if (workspace) observer.observe(workspace);
    window.addEventListener("resize", measure);
    return () => { observer.disconnect(); window.removeEventListener("resize", measure); };
  }, [text]);
  return <section className="composer catalog-composer" aria-label="Message composer">
    <label className="sr-only" htmlFor="catalog-prompt">Message draft</label>
    <textarea id="catalog-prompt" ref={promptInput} className="prompt-input" rows={1} aria-describedby="catalog-draft-status"
      maxLength={32768} value={text} onChange={event => {
        const value = event.target.value;
        setDraft({ text: value, editGeneration: draftIndicators.edit(sessionId, value, restoredText.current) });
      }} onKeyDown={event => {
        if (dispatchTransientComposerKey({ key: event.key, ctrlKey: event.ctrlKey, shiftKey: event.shiftKey,
          altKey: event.altKey, metaKey: event.metaKey, isComposing: event.nativeEvent.isComposing,
          keyCode: event.nativeEvent.keyCode, repeat: event.repeat, defaultPrevented: event.defaultPrevented },
        event.currentTarget, onOpenHelp, onOpenPalette)) event.preventDefault();
      }} placeholder="Draft a prompt for this session…" />
    <div className="composer-toolbar">
      <p id="catalog-draft-status" role="status">Draft only — {reason ?? "No owned desktop host; sending is unavailable. Drafts stay local when storage permits."}</p>
      <div className="history-controls">
        <button type="button" className="primary-button send-button" disabled aria-describedby="catalog-draft-status">Send unavailable</button>
      </div>
    </div>
    <p className="catalog-diagnostics">Provider {provider ?? "not recorded"}; model, prompt and reasoning not available without an owned runtime.</p>
  </section>;
}
