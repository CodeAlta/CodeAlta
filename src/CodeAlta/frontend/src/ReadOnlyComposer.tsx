import { useContext, useEffect, useLayoutEffect, useRef, useState, useSyncExternalStore, type ReactNode, type ClipboardEvent } from "react";
import { Button } from "@blueprintjs/core";
import { ActiveProviderStatus } from "./ActiveProviderStatus";
import { ActivitySpinner } from "./ActivitySpinner";
import { createDraftIndicators, persistDraft, restoreDraft } from "./promptDraft";
import { equationKey, type PromptInput } from "./PromptEditor";
import { dispatchComposerKey, dispatchTransientComposerKey } from "./composerKeyboard";
import { AppIcon } from "./AppIcon";
import { ComposerSurface } from "./ComposerSurface";
import { AttachMenu, AttachMenuItems } from "./PromptImageAttachments";
import { desktopShell } from "#neoastra";
import { pickFolder } from "./folderPicker";
import { folderLink } from "./projectReferences";
import { insertIssueReferences } from "./issueReferences";
import { linkAttachment } from "./promptImages";
import { ProjectReferenceContext } from "./ProjectReferencePicker";
import { ExpandedPromptEditor } from "./ExpandedPromptEditor";
import { ProjectReferencePicker } from "./ProjectReferencePicker";
import { IssuePicker } from "./IssuePicker";
import { MicButton } from "./speech/MicButton";
import { DraftUsage } from "./promptSurface/PromptSurfaceParts";
import { useShellLanguage } from "./shellLanguage";

const noSubscription = () => () => { };

export function ReadOnlyComposer({ sessionId, provider, draftIndicators, reason, infoControl, onOpenHelp, onOpenCommands, localDraft, localImages, active = true, folder = null, placeholder, busyStatus }: {
  active?: boolean;
  /** The folder of the project the draft starts a session in: the folder dialog of the attach menu opens there. */
  folder?: string | null;
  /** What the prompt says before anything is typed, in the place of the one of a new session. */
  placeholder?: string;
  /** What the status of a busy prompt says, in the place of "Creating session…". */
  busyStatus?: string;
  sessionId: string; provider: string | null;
  draftIndicators: ReturnType<typeof createDraftIndicators>; reason?: string;
  infoControl?: ReactNode;
  onOpenHelp?: () => void; onOpenCommands?: () => void;
  localDraft?: {
    /** The text of the draft as it is now, and how to follow it: each keystroke renders this prompt, not its owner. */
    read: () => string; subscribe: (listener: () => void) => () => void;
    edit: (text: string) => void; action: ReactNode; options?: ReactNode; notice?: ReactNode;
    submit: () => void; disabled: boolean; busy: boolean;
    /** What the prompt bar of a session that does not exist yet shows in place of a session's own facts. */
    surface?: { epoch: string; onOpenProviders: () => void; contextTokens: number | null } };
  localImages?: { paste: (event: ClipboardEvent<HTMLElement>) => void; attach: (files: File[], origin: HTMLElement) => void;
    addLinks: (links: readonly ReturnType<typeof linkAttachment>[]) => void; attachments: ReactNode; invalidate: () => void };
}) {
  const { t } = useShellLanguage();
  const [draft, setDraft] = useState(() => ({ text: restoreDraft(key => localStorage.getItem(key), sessionId), editGeneration: null as number | null }));
  const readLocal = () => localDraft ? localDraft.read() : null;
  const text = useSyncExternalStore(localDraft?.subscribe ?? noSubscription, readLocal, readLocal) ?? draft.text;
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
  const referenceScope = useContext(ProjectReferenceContext);
  const [issueRequest, setIssueRequest] = useState(0);
  // As the composer of a session: a folder of the project is a project reference, any other a link to its full path.
  async function addFolder() {
    if (!localDraft || localDraft.busy || !localImages) return;
    const pick = await pickFolder(desktopShell.pickFolder, t("Add a folder to the prompt"), folder);
    const link = pick.status === "ok" ? folderLink(pick.path, referenceScope ? folder : null) : null;
    if (pick.status === "ok" && link) localImages.addLinks([linkAttachment("folder", pick.path.replace(/[\\/]+$/u, "").split(/[\\/]/u).at(-1) || pick.path, link)]);
  }
  const attachIssues = (issues: readonly { number: number; url: string; title: string }[]) => localImages?.addLinks(issues.flatMap(issue => {
    const link = insertIssueReferences("", 0, 0, [issue])?.text;
    return link ? [linkAttachment("issue", `#${issue.number} ${issue.title}`, link)] : [];
  }));
  const attachActions = localDraft && localImages ? { onFolder: () => void addFolder(), onIssues: referenceScope ? () => setIssueRequest(value => value + 1) : null } : null;
  useLayoutEffect(() => { draftIndicators.clear(sessionId); }, [draftIndicators, sessionId]);
  useEffect(() => { if (localDraft) return; draftIndicators.persisted(sessionId, draft.editGeneration,
    persistDraft((key, value) => localStorage.setItem(key, value), key => localStorage.removeItem(key), sessionId, draft.text));
  }, [sessionId, draft, draftIndicators]);
  return <>
    {!expanded && localDraft && localImages && localImages.attachments}
    <ComposerSurface className={localDraft ? undefined : "catalog-composer"} busy={localDraft?.busy} status={<span className="composer-foreground-status">
      {localDraft?.busy ? <ActivitySpinner size={14} /> : <AppIcon name="prompt" size={14} />}
      <span className="composer-foreground-text">{localDraft?.busy && busyStatus ? busyStatus : t(localDraft?.busy ? "Creating session…" : localDraft ? "Prompt ready" : "Draft only")}</span>
    </span>}
    expandedEditor={expanded && <ExpandedPromptEditor text={text} onChange={edit} onPaste={refuseImagePaste} onCompositionStart={localImages?.invalidate} attachments={localDraft && localImages ? localImages.attachments : imageNotice && <p role="status">{t("Images cannot be pasted or transferred from a local/read-only draft. Open an owned session with a supported model first; nothing was transferred.")}</p>} onClose={() => { localImages?.invalidate(); setExpanded(false); }} />}
    notice={<>{localDraft?.notice}{!expanded && imageNotice && <p role="status">{t("Images cannot be pasted or transferred from a local/read-only draft. Open an owned session with a supported model first; nothing was transferred.")}</p>}</>}
    options={localDraft && localImages ? <><AttachMenu id={`attach-files-${sessionId}`} disabled={localDraft.busy} onFiles={localImages.attach} {...attachActions} />{localDraft.options}</> : localDraft?.options}
    onDropFiles={localDraft && localImages && !localDraft.busy ? localImages.attach : null}
    meters={localDraft?.surface && <DraftUsage contextTokens={localDraft.surface.contextTokens} />}
    more={{ items: <>
      {localDraft && localImages && <AttachMenuItems inputId={`attach-files-${sessionId}`} disabled={localDraft.busy} {...attachActions} />}
      {/* A session that does not exist yet has no information to show: the control is there, disabled. */}
      {infoControl ?? <Button variant="minimal" className="session-info-trigger" icon={<AppIcon name="info" size={16} />} disabled
        aria-label={t("Session info")} title={t("Session info")}>{t("Session info")}</Button>}
      <Button id={active ? "expand-session-prompt" : `expand-session-prompt-${sessionId}`} variant="minimal" icon={<AppIcon name="expand" size={16} />} aria-label={t("Expand prompt editor")}
        title={t("Edit prompt in a large window (F6)")} onClick={() => setExpanded(true)} endIcon={<kbd>F6</kbd>}>{t("Expand prompt editor")}</Button>
      {localDraft && <Button id={active ? "insert-equation" : `insert-equation-${sessionId}`} variant="minimal" icon={<AppIcon name="sigma" size={16} />} disabled={expanded || localDraft.busy}
        aria-label={t("Insert equation")} title={t("Write an equation and insert it in the prompt (Alt+=)")} onClick={() => promptInput.current?.editEquation()}
        endIcon={<kbd>{equationKey}</kbd>}>{t("Insert equation")}</Button>}
      {localDraft?.surface && <ActiveProviderStatus epoch={localDraft.surface.epoch} onOpen={localDraft.surface.onOpenProviders} />}
    </> }}
    editor={{ id: active ? "catalog-prompt" : `catalog-prompt-${sessionId}`, ref: promptInput, onPaste: refuseImagePaste, label: t("Message"), disabled: expanded,
      onCompositionStart: () => localImages?.invalidate(), value: text, onChange: edit, onKeyDown: event => {
        if (dispatchTransientComposerKey({ key: event.key, ctrlKey: event.ctrlKey, shiftKey: event.shiftKey,
          altKey: event.altKey, metaKey: event.metaKey, isComposing: event.nativeEvent.isComposing,
          keyCode: event.nativeEvent.keyCode, repeat: event.repeat, defaultPrevented: event.defaultPrevented },
        promptInput.current!, onOpenHelp, onOpenCommands)) { event.preventDefault(); event.stopPropagation(); return; }
        if (localDraft && dispatchComposerKey({ key: event.key, ctrlKey: event.ctrlKey, shiftKey: event.shiftKey,
          altKey: event.altKey, metaKey: event.metaKey, isComposing: event.nativeEvent.isComposing,
          keyCode: event.nativeEvent.keyCode, repeat: event.repeat, defaultPrevented: event.defaultPrevented },
        () => { if (!localDraft.disabled) localDraft.submit(); }, () => { if (!localDraft.disabled) localDraft.submit(); },
        navigator.platform.startsWith("Mac"))) { event.preventDefault(); event.stopPropagation(); return; }
        if (event.key === "F6" && !event.defaultPrevented && !event.repeat && !event.nativeEvent.isComposing
          && event.nativeEvent.keyCode !== 229 && !event.ctrlKey && !event.altKey && !event.metaKey && !event.shiftKey) {
          event.preventDefault(); setExpanded(true);
        }
      }, placeholder: placeholder ?? t("Ask CodeAlta to work on this project…") }}>
        {localDraft && !expanded && <ProjectReferencePicker text={text} edit={edit} input={promptInput} />}
        {localDraft && !expanded && <IssuePicker edit={edit} input={promptInput} openRequest={issueRequest} onAttach={attachIssues} />}
        <p id={`catalog-draft-status-${sessionId}`} className="sr-only">{reason ?? t("Sending requires the desktop app.")}</p>
        {localDraft && <MicButton owner={`draft:${sessionId}`} input={promptInput} disabled={expanded || localDraft.busy} visible={active}
          id={active ? "dictate-prompt" : `dictate-prompt-${sessionId}`} onSend={() => { if (!localDraft.disabled) localDraft.submit(); }} />}
        {localDraft?.action ?? <Button className="send-button" intent="primary" icon={<AppIcon name="send" size={16} />} disabled aria-label={t("Send unavailable")} aria-describedby={`catalog-draft-status-${sessionId}`} />}
  </ComposerSurface></>;
}
