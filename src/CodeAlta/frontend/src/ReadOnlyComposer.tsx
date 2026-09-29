import { useEffect, useLayoutEffect, useRef, useState, type ReactNode, type ClipboardEvent } from "react";
import { createDraftIndicators, persistDraft, restoreDraft } from "./promptDraft";
import { PromptEditor, type PromptInput } from "./PromptEditor";
import { dispatchTransientComposerKey } from "./composerKeyboard";
import { ExpandedPromptEditor } from "./ExpandedPromptEditor";
import { ProjectReferencePicker } from "./ProjectReferencePicker";
import { useShellLanguage } from "./shellLanguage";

export function ReadOnlyComposer({ sessionId, provider, draftIndicators, reason, infoControl, onOpenHelp, onOpenPalette, localDraft, localImages }: {
  sessionId: string; provider: string | null;
  draftIndicators: ReturnType<typeof createDraftIndicators>; reason?: string;
  infoControl?: ReactNode;
  onOpenHelp?: () => void; onOpenPalette?: () => void;
  localDraft?: { text: string; edit: (text: string) => void; action: ReactNode };
  localImages?: { paste: (event: ClipboardEvent<HTMLElement>) => void; attachments: ReactNode; invalidate: () => void };
}) {
  const { t } = useShellLanguage();
  const [draft, setDraft] = useState(() => ({ text: restoreDraft(key => localStorage.getItem(key), sessionId), editGeneration: null as number | null }));
  const text = localDraft?.text ?? draft.text;
  const [expanded, setExpanded] = useState(false);
  const [imageNotice, setImageNotice] = useState("");
  function refuseImagePaste(event: ClipboardEvent<HTMLElement>) {
    if (localDraft && localImages) { localImages.paste(event); return; }
    if (event.clipboardData.files.length) { event.preventDefault(); setImageNotice("Images cannot be pasted or transferred from a local/read-only draft. Open an owned session with a supported model first; nothing was transferred."); }
  }
  const edit = (value: string) => localDraft ? localDraft.edit(value)
    : setDraft({ text: value, editGeneration: draftIndicators.edit(sessionId, value, restoredText.current) });
  const restoredText = useRef(text);
  const promptInput = useRef<PromptInput>(null);
  useLayoutEffect(() => { draftIndicators.clear(sessionId); }, [draftIndicators, sessionId]);
  useEffect(() => { if (localDraft) return; draftIndicators.persisted(sessionId, draft.editGeneration,
    persistDraft((key, value) => localStorage.setItem(key, value), key => localStorage.removeItem(key), sessionId, draft.text));
  }, [sessionId, draft, draftIndicators]);
  return <section className="composer catalog-composer" aria-label={t("Message composer")}>
    {localDraft && <details className="composer-reference-help"><summary>@</summary><p className="catalog-diagnostics">{t("@ search requires an owned, verified project. References resolve only on normal Send after creation and transfer; file contents are not uploaded.")}</p></details>}
    {expanded && <ExpandedPromptEditor text={text} onChange={edit} onPaste={refuseImagePaste} onCompositionStart={localImages?.invalidate} attachments={localDraft && localImages ? localImages.attachments : imageNotice && <p role="status">{t("Images cannot be pasted or transferred from a local/read-only draft. Open an owned session with a supported model first; nothing was transferred.")}</p>} onClose={() => { localImages?.invalidate(); setExpanded(false); }} />}
    {!expanded && localDraft && localImages && localImages.attachments}
    {!expanded && imageNotice && <p role="status">{t("Images cannot be pasted or transferred from a local/read-only draft. Open an owned session with a supported model first; nothing was transferred.")}</p>}
    <label className="sr-only" htmlFor="catalog-prompt">{t("Message draft")}</label>
    <PromptEditor id="catalog-prompt" ref={promptInput} onPaste={refuseImagePaste} label={t("Message draft")} disabled={expanded}
      onCompositionStart={() => localImages?.invalidate()}
      value={text} onChange={edit} onKeyDown={event => {
        if (dispatchTransientComposerKey({ key: event.key, ctrlKey: event.ctrlKey, shiftKey: event.shiftKey,
          altKey: event.altKey, metaKey: event.metaKey, isComposing: event.nativeEvent.isComposing,
          keyCode: event.nativeEvent.keyCode, repeat: event.repeat, defaultPrevented: event.defaultPrevented },
        promptInput.current!, onOpenHelp, onOpenPalette)) { event.preventDefault(); event.stopPropagation(); }
        if (event.key === "F6" && !event.defaultPrevented && !event.repeat && !event.nativeEvent.isComposing
          && event.nativeEvent.keyCode !== 229 && !event.ctrlKey && !event.altKey && !event.metaKey && !event.shiftKey) {
          event.preventDefault(); setExpanded(true);
        }
      }} placeholder={t(localDraft ? "Draft a prompt — no session created yet…" : "Draft a prompt for this session…")} />
    {localDraft && !expanded && <ProjectReferencePicker text={text} edit={edit} input={promptInput} />}
    <div className="composer-toolbar">
      <details><summary>{t("Draft only")}</summary><p id="catalog-draft-status" role="status">{reason ?? t("No owned desktop host; sending is unavailable. Drafts stay local when storage permits.")}</p></details>
      <div className="history-controls">
        {infoControl}
        <button id="expand-session-prompt" type="button" aria-label={t("Expand prompt editor")} title={t("Edit prompt in a large window (F6)")} onClick={() => setExpanded(true)}>{t("Expand")}</button>
        {localDraft?.action ?? <button type="button" className="primary-button send-button" disabled aria-describedby="catalog-draft-status">{t("Send unavailable")}</button>}
      </div>
    </div>
    {!localDraft && <p className="catalog-diagnostics">{t("Provider {provider}; model, prompt and reasoning not available without an owned runtime.", { provider: provider ?? t("Not recorded") })}</p>}
  </section>;
}
