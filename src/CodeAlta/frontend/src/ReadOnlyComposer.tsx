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
import { folderPickNotice, folderTitle, pickFolder } from "./folderPicker";
import { showToast } from "./appToaster";
import { folderLink } from "./projectReferences";
import { insertIssueReferences } from "./issueReferences";
import { attachmentBytes, linkAttachment } from "./promptImages";
import { ProjectReferenceContext } from "./ProjectReferencePicker";
import { ExpandedPromptEditor } from "./ExpandedPromptEditor";
import { ProjectReferencePicker } from "./ProjectReferencePicker";
import { ReferencePicker } from "./ReferencePicker";
import { promptKeysHint } from "./promptKeys";
import { MicButton } from "./speech/MicButton";
import { DraftUsage } from "./promptSurface/PromptSurfaceParts";
import { useShellLanguage } from "./shellLanguage";
import { shellAttachment, type ShellCommands, type ShellCommandTarget } from "./shellCommands/shellCommands";
import { holdsShellOutput, useShellPrompt } from "./shellCommands/useShellPrompt";

const noSubscription = () => () => { };

export function ReadOnlyComposer({ sessionId, provider, draftIndicators, reason, infoControl, onOpenHelp, onOpenCommands, localDraft, localImages, active = true, folder = null, placeholder, busyStatus, shell }: {
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
    addLinks: (links: readonly ReturnType<typeof linkAttachment>[]) => void; remove: (attachments: readonly ReturnType<typeof linkAttachment>[]) => void;
    attachments: ReactNode; invalidate: () => void;
    /** The attachments of the draft. */
    images: readonly ReturnType<typeof linkAttachment>[] };
  /**
   * The shell commands of the prompt of a session to start (`!`): they run for its project, and their outputs go in its
   * attachments, with the prompt that starts the session. Only for a draft that takes attachments.
   */
  shell?: Readonly<{ store: ShellCommands; target: ShellCommandTarget | null; slot: HTMLElement | null }>;
}) {
  const { t } = useShellLanguage();
  const [draft, setDraft] = useState(() => ({ text: restoreDraft(key => localStorage.getItem(key), sessionId), editGeneration: null as number | null }));
  const readLocal = () => localDraft ? localDraft.read() : null;
  const text = useSyncExternalStore(localDraft?.subscribe ?? noSubscription, readLocal, readLocal) ?? draft.text;
  const [expanded, setExpanded] = useState(false);
  const [imageNotice, setImageNotice] = useState("");
  function refuseImagePaste(event: ClipboardEvent<HTMLElement>) {
    if (!event.clipboardData.files.length && shellPrompt.takeText(event.clipboardData.getData("text/plain"))) { event.preventDefault(); return; }
    if (localDraft && localImages) { localImages.paste(event); return; }
    if (event.clipboardData.files.length) { event.preventDefault(); setImageNotice("Images cannot be pasted or transferred from a local/read-only draft. Open an owned session with a supported model first; nothing was transferred."); }
  }
  const edit = (value: string) => localDraft ? localDraft.edit(value)
    : setDraft({ text: value, editGeneration: draftIndicators.edit(sessionId, value, restoredText.current) });
  const restoredText = useRef(text);
  const promptInput = useRef<PromptInput>(null);
  const [shellNotice, setShellNotice] = useState("");
  const shellPrompt = useShellPrompt({ store: localDraft && localImages ? shell?.store : undefined, prompt: `draft:${sessionId}`, target: shell?.target ?? null,
    input: promptInput, text: () => readLocal() ?? "", value: text,
    clear: () => { localDraft?.edit(""); promptInput.current?.replaceText(""); }, enabled: !!localDraft && !localDraft.busy,
    attach: (entry, attached) => {
      if (!localImages || localDraft?.busy) return "later";
      if (attached) localImages.addLinks([shellAttachment(entry)]); else localImages.remove([shellAttachment(entry)]);
      return "done";
    },
    attached: entry => holdsShellOutput(localImages?.images ?? [], entry), notice: setShellNotice,
    slot: shell?.slot ?? null, active, onSubmit: () => { shellPrompt.take(); } });
  // A draft in shell mode runs its command on Enter, whatever its Send would do.
  const submitDraft = () => { if (!shellPrompt.take() && localDraft && !localDraft.disabled) localDraft.submit(); };
  const referenceScope = useContext(ProjectReferenceContext);
  const [issueRequest, setIssueRequest] = useState(0);
  // As the composer of a session: a folder of the project is a project reference, any other a link to its full path.
  const addFolders = (paths: readonly string[]) => localImages?.addLinks(paths.flatMap(path => {
    const link = folderLink(path, referenceScope ? folder : null);
    return link ? [linkAttachment("folder", folderTitle(path), link)] : [];
  }));
  async function addFolder() {
    if (!localDraft || localDraft.busy || !localImages) return;
    const pick = await pickFolder(desktopShell.pickFolder, t("Add a folder to the prompt"), folder);
    if (pick.status === "ok") { addFolders([pick.path]); return; }
    // A desktop without a folder dialog, or a dialog that failed, is never silent.
    const notice = folderPickNotice(pick);
    if (notice) showToast({ message: t(notice), intent: "warning", icon: "warning-sign", timeout: 8000 });
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
    <ComposerSurface className={localDraft ? undefined : "catalog-composer"} busy={localDraft?.busy} place={shellPrompt.button}
      status={shellPrompt.status ?? <span className="composer-foreground-status">
      {localDraft?.busy ? <ActivitySpinner size={14} /> : <AppIcon name="prompt" size={14} />}
      <span className="composer-foreground-text">{localDraft?.busy && busyStatus ? busyStatus : t(localDraft?.busy ? "Creating session…" : localDraft ? "Prompt ready" : "Draft only")}</span>
    </span>}
    expandedEditor={expanded && <ExpandedPromptEditor text={text} onChange={edit} onPaste={refuseImagePaste} onCompositionStart={localImages?.invalidate} attachments={localDraft && localImages ? localImages.attachments : imageNotice && <p role="status">{t("Images cannot be pasted or transferred from a local/read-only draft. Open an owned session with a supported model first; nothing was transferred.")}</p>} onClose={() => { localImages?.invalidate(); setExpanded(false); }} />}
    notice={<>{localDraft?.notice}{shellNotice && <p role="status">{shellNotice}</p>}{!expanded && imageNotice && <p role="status">{t("Images cannot be pasted or transferred from a local/read-only draft. Open an owned session with a supported model first; nothing was transferred.")}</p>}</>}
    options={localDraft && localImages ? <><AttachMenu id={`attach-files-${sessionId}`} disabled={localDraft.busy} onFiles={localImages.attach} {...attachActions} />{localDraft.options}</> : localDraft?.options}
    onDropFiles={localDraft && localImages && !localDraft.busy ? localImages.attach : null}
    attachedBytes={localImages ? attachmentBytes(localImages.images) : 0}
    onDropFolders={localDraft && localImages && !localDraft.busy ? addFolders : null}
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
    editor={{ id: active ? "catalog-prompt" : `catalog-prompt-${sessionId}`, ref: promptInput, onPaste: refuseImagePaste, onType: shellPrompt.takeText, label: t("Message"), disabled: expanded,
      onCompositionStart: () => localImages?.invalidate(), value: text, onChange: value => { setShellNotice(""); edit(value); }, shell: shellPrompt.mode, onKeyDown: event => {
        if (shellPrompt.keyDown(event)) return;
        if (!shellPrompt.mode && dispatchTransientComposerKey({ key: event.key, ctrlKey: event.ctrlKey, shiftKey: event.shiftKey,
          altKey: event.altKey, metaKey: event.metaKey, isComposing: event.nativeEvent.isComposing,
          keyCode: event.nativeEvent.keyCode, repeat: event.repeat, defaultPrevented: event.defaultPrevented },
        promptInput.current!, onOpenHelp, onOpenCommands)) { event.preventDefault(); event.stopPropagation(); return; }
        if (localDraft && dispatchComposerKey({ key: event.key, ctrlKey: event.ctrlKey, shiftKey: event.shiftKey,
          altKey: event.altKey, metaKey: event.metaKey, isComposing: event.nativeEvent.isComposing,
          keyCode: event.nativeEvent.keyCode, repeat: event.repeat, defaultPrevented: event.defaultPrevented },
        submitDraft, submitDraft,
        navigator.platform.startsWith("Mac"))) { event.preventDefault(); event.stopPropagation(); return; }
        if (event.key === "F6" && !event.defaultPrevented && !event.repeat && !event.nativeEvent.isComposing
          && event.nativeEvent.keyCode !== 229 && !event.ctrlKey && !event.altKey && !event.metaKey && !event.shiftKey) {
          event.preventDefault(); setExpanded(true);
        }
      }, placeholder: shellPrompt.editor.placeholder ?? placeholder ?? (localDraft ? `${t("Ask CodeAlta to work on this project…")}\n${t(promptKeysHint)}` : t("Ask CodeAlta to work on this project…")) }}>
        {/* The pickers of `@` and `#` insert Markdown for the agent: a shell command takes these characters as they are. */}
        {localDraft && !expanded && !shellPrompt.mode && <ProjectReferencePicker text={text} edit={edit} input={promptInput} />}
        {localDraft && !expanded && !shellPrompt.mode && <ReferencePicker edit={edit} input={promptInput} openRequest={issueRequest} onAttach={attachIssues} />}
        <p id={`catalog-draft-status-${sessionId}`} className="sr-only">{reason ?? t("Sending requires the desktop app.")}</p>
        {localDraft && <MicButton owner={`draft:${sessionId}`} input={promptInput} disabled={expanded || localDraft.busy} visible={active}
          id={active ? "dictate-prompt" : `dictate-prompt-${sessionId}`} onSend={() => { if (!localDraft.disabled) localDraft.submit(); }} />}
        {shellPrompt.runButton ?? localDraft?.action ?? <Button className="send-button" intent="primary" icon={<AppIcon name="send" size={16} />} disabled aria-label={t("Send unavailable")} aria-describedby={`catalog-draft-status-${sessionId}`} />}
  </ComposerSurface>
    {shellPrompt.panel}</>;
}
