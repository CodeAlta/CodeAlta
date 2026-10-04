import { useEffect, useLayoutEffect, useRef, useState, type ReactNode, type ClipboardEvent } from "react";
import { Button } from "@blueprintjs/core";
import { ActiveProviderStatus } from "./ActiveProviderStatus";
import { ActivitySpinner } from "./ActivitySpinner";
import { compactTokens } from "./contextUsage";
import { createDraftIndicators, persistDraft, restoreDraft } from "./promptDraft";
import type { PromptInput } from "./PromptEditor";
import { dispatchComposerKey, dispatchTransientComposerKey } from "./composerKeyboard";
import { AppIcon } from "./AppIcon";
import { ComposerSurface } from "./ComposerSurface";
import { ExpandedPromptEditor } from "./ExpandedPromptEditor";
import { ProjectReferencePicker } from "./ProjectReferencePicker";
import { GitHubIssuePicker } from "./GitHubIssuePicker";
import { useShellLanguage } from "./shellLanguage";

// The context meter of a session that has not started: nothing used yet out of the selected model's window.
function DraftUsage({ contextTokens }: { contextTokens: number | null }) {
  const { t } = useShellLanguage();
  const summary = contextTokens && contextTokens > 0 ? `0 / ${compactTokens(String(contextTokens))}` : "0";
  const label = t("Context usage: {summary}", { summary: `0% · ${summary}` });
  return <Button variant="minimal" className="context-usage" data-intent="none" disabled aria-label={label} title={label}>
    <span className="context-usage-meter" aria-hidden="true"><span style={{ width: "0%" }} /></span>
    <span className="context-usage-text">0%</span>
    <span className="context-usage-tokens">{summary}</span>
  </Button>;
}

export function ReadOnlyComposer({ sessionId, provider, draftIndicators, reason, infoControl, onOpenHelp, onOpenPalette, localDraft, localImages, active = true }: {
  active?: boolean;
  sessionId: string; provider: string | null;
  draftIndicators: ReturnType<typeof createDraftIndicators>; reason?: string;
  infoControl?: ReactNode;
  onOpenHelp?: () => void; onOpenPalette?: () => void;
  localDraft?: { text: string; edit: (text: string) => void; action: ReactNode; options?: ReactNode; notice?: ReactNode;
    submit: () => void; disabled: boolean; busy: boolean;
    /** What the prompt bar of a session that does not exist yet shows in place of a session's own facts. */
    surface?: { epoch: string; onOpenProviders: () => void; contextTokens: number | null } };
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
  return <>
    {!expanded && localDraft && localImages && localImages.attachments}
    <ComposerSurface className={localDraft ? undefined : "catalog-composer"} busy={localDraft?.busy} status={<>
      {localDraft?.busy ? <ActivitySpinner size={14} /> : <AppIcon name="prompt" size={14} />}
      {t(localDraft?.busy ? "Creating session…" : localDraft ? "Prompt ready" : "Draft only")}
    </>}
    expandedEditor={expanded && <ExpandedPromptEditor text={text} onChange={edit} onPaste={refuseImagePaste} onCompositionStart={localImages?.invalidate} attachments={localDraft && localImages ? localImages.attachments : imageNotice && <p role="status">{t("Images cannot be pasted or transferred from a local/read-only draft. Open an owned session with a supported model first; nothing was transferred.")}</p>} onClose={() => { localImages?.invalidate(); setExpanded(false); }} />}
    notice={<>{localDraft?.notice}{!expanded && imageNotice && <p role="status">{t("Images cannot be pasted or transferred from a local/read-only draft. Open an owned session with a supported model first; nothing was transferred.")}</p>}</>}
    options={localDraft?.options}
    editor={{ id: active ? "catalog-prompt" : `catalog-prompt-${sessionId}`, ref: promptInput, onPaste: refuseImagePaste, label: t("Message"), disabled: expanded,
      onCompositionStart: () => localImages?.invalidate(), value: text, onChange: edit, onKeyDown: event => {
        if (dispatchTransientComposerKey({ key: event.key, ctrlKey: event.ctrlKey, shiftKey: event.shiftKey,
          altKey: event.altKey, metaKey: event.metaKey, isComposing: event.nativeEvent.isComposing,
          keyCode: event.nativeEvent.keyCode, repeat: event.repeat, defaultPrevented: event.defaultPrevented },
        promptInput.current!, onOpenHelp, onOpenPalette)) { event.preventDefault(); event.stopPropagation(); return; }
        if (localDraft && dispatchComposerKey({ key: event.key, ctrlKey: event.ctrlKey, shiftKey: event.shiftKey,
          altKey: event.altKey, metaKey: event.metaKey, isComposing: event.nativeEvent.isComposing,
          keyCode: event.nativeEvent.keyCode, repeat: event.repeat, defaultPrevented: event.defaultPrevented },
        () => { if (!localDraft.disabled) localDraft.submit(); }, () => {})) { event.preventDefault(); event.stopPropagation(); return; }
        if (event.key === "F6" && !event.defaultPrevented && !event.repeat && !event.nativeEvent.isComposing
          && event.nativeEvent.keyCode !== 229 && !event.ctrlKey && !event.altKey && !event.metaKey && !event.shiftKey) {
          event.preventDefault(); setExpanded(true);
        }
      }, placeholder: t("Ask CodeAlta to work on this project…") }}>
        {localDraft && !expanded && <ProjectReferencePicker text={text} edit={edit} input={promptInput} />}
        {localDraft && !expanded && <GitHubIssuePicker edit={edit} input={promptInput} />}
        {localDraft?.surface && <ActiveProviderStatus epoch={localDraft.surface.epoch} onOpen={localDraft.surface.onOpenProviders} />}
        <details className="composer-draft-info"><summary aria-label={t("Draft information")} title={t("Draft information")}><AppIcon name="info" size={16} /></summary><p id={`catalog-draft-status-${sessionId}`} role="status">{reason ?? t("Sending requires the desktop app.")}</p></details>
        {infoControl}
        {localDraft?.surface && <DraftUsage contextTokens={localDraft.surface.contextTokens} />}
        {localDraft?.surface && <Button variant="minimal" icon={<AppIcon name="reminder" size={16} />} data-reminder-count="" disabled
          aria-label={t("Reminders: {count} active", { count: 0 })} title={t("Reminders: {count} active", { count: 0 })}><span className="reminder-count" aria-hidden="true">0</span></Button>}
        <Button id={active ? "expand-session-prompt" : `expand-session-prompt-${sessionId}`} variant="minimal" icon={<AppIcon name="expand" size={16} />} aria-label={t("Expand prompt editor")} title={t("Edit prompt in a large window (F6)")} onClick={() => setExpanded(true)} />
        {localDraft?.surface && <Button variant="minimal" icon={<AppIcon name="compact" size={16} />} disabled
          aria-label={t("Compact the conversation (Ctrl+F11)")} title={t("Compact the conversation (Ctrl+F11)")} />}
        {localDraft?.action ?? <Button className="send-button" intent="primary" icon={<AppIcon name="send" size={16} />} disabled aria-label={t("Send unavailable")} aria-describedby={`catalog-draft-status-${sessionId}`} />}
  </ComposerSurface></>;
}
