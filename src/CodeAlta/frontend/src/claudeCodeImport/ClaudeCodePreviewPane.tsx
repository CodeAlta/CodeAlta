import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Button, HTMLSelect } from "@blueprintjs/core";
import type { WorkspaceProject } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { ComposerSplitter, useComposerLayout } from "../ComposerLayout";
import { ConversationBanner, ConversationElsewhereLine, useConversationElsewhere, useElsewhereText } from "../claudeCodeOpen/ConversationElsewhereNotice";
import { ComposerChrome, type ComposerChromeValue } from "../composerChrome";
import { History } from "../HistoryPanel";
import { ProviderIcon } from "../ProviderIcon";
import { ReadOnlyComposer } from "../ReadOnlyComposer";
import { SessionWidthGrips } from "../SessionWidthGrips";
import { claudeCodeTabProvider, type FileTab } from "../fileTabs";
import { persistDraft, restoreDraft, type createDraftIndicators, type createLocalDrafts } from "../promptDraft";
import type { createImageDrafts } from "../promptImages";
import { useShellLanguage } from "../shellLanguage";
import { createTimelineScrollMemory, useTimelinePosition } from "../timelineScroll";
import { useLocalDraftImages } from "../useLocalDraftImages";
import {
  previewBannerText, previewPlaceholder, previewReader, previewRefreshView, previewSessionId, previewUnavailableMessage,
  type ClaudeCodePreviewApi, type ClaudeCodePreviewStatus,
} from "./claudeCodePreview";
import type { ClaudeCodeSessionsApi } from "./claudeCodeSessions";
import { previewDraftScope, previewDraftStorageKey, previewImageKey, type PreviewPrompt } from "./previewContinuation";

export type { PreviewPrompt } from "./previewContinuation";

const noop = () => { };
const noChrome: ComposerChromeValue = {};

/**
 * The read-only preview of a Claude Code conversation started in VS Code or a terminal, which no session of CodeAlta
 * continues yet: a banner says so and names the project its first message continues it in, which can be changed, with
 * Continue in CodeAlta (which leaves the prompt to write in) and Open in VS Code; the timeline shows the turns as an import would write them, the latest first
 * and older ones as the reader goes up; and the prompt under it continues the conversation in that project: its first
 * message imports it, then is sent to the session that continues it. Nothing is written before that. The timeline is
 * read again every few seconds while it is shown: the conversation may grow in VS Code. When a session of CodeAlta
 * continues the conversation, the preview leaves for it, unless a message waits under the preview: the preview then
 * stays with it, says so, and opens that session beside it on demand. The tab of a conversation of no project has no
 * project (an empty `projectId`).
 */
export function ClaudeCodePreviewPane({ tab, epoch, spaceId = null, api, previewApi, drafts, imageDrafts, draftIndicators, scrollMemory, visible = true, busy = false,
  importingElsewhere = false, refreshMilliseconds = 5000, onSend, onOpenInVsCode, onExisting, onOpenHelp, onOpenCommands, projects = [], target = null,
  onTargetChange }: {
  tab: FileTab;
  /** The projects a conversation can be continued in: those not archived. */
  projects?: readonly WorkspaceProject[];
  /** The project its first message continues it in; null when there is none (no project to continue it in). */
  target?: WorkspaceProject | null;
  /** Another project was chosen to continue it in. */
  onTargetChange?: (project: WorkspaceProject) => void;
  /** The epoch of the host the window knows; null while it knows none (nothing can be continued then). */
  epoch: string | null;
  /** The space the window shows, whose provider reads the conversation; null without spaces. */
  spaceId?: string | null;
  /** The calls of the group: the description of the conversation, for who holds it now. */
  api: Pick<ClaudeCodeSessionsApi, "conversation">;
  previewApi: ClaudeCodePreviewApi;
  /** The drafts typed before their session exists, which keep the one of the preview. */
  drafts: ReturnType<typeof createLocalDrafts>;
  /** The images of the drafts of the window, which keep those pasted under the preview. */
  imageDrafts: ReturnType<typeof createImageDrafts>;
  /** The marks of edited drafts of the window. */
  draftIndicators: ReturnType<typeof createDraftIndicators>;
  scrollMemory?: ReturnType<typeof createTimelineScrollMemory>;
  /** Whether the tab is in front: the timeline reads only then. */
  visible?: boolean;
  /** An import of the conversation is in progress: nothing else is asked meanwhile. */
  busy?: boolean;
  /** An import of another conversation is in progress: nothing is asked here meanwhile either. */
  importingElsewhere?: boolean;
  /** How often the timeline is read again while it is shown; 0 never. */
  refreshMilliseconds?: number;
  /** The first message: imports the conversation, then sends the message to the session that continues it. */
  onSend: (prompt: PreviewPrompt) => void;
  onOpenInVsCode: () => void;
  /**
   * A session of CodeAlta continues the conversation already: it is opened instead, or beside the preview with `keep`,
   * when a message waits under the preview.
   */
  onExisting: (sessionId: string, keep: boolean) => void;
  onOpenHelp?: () => void; onOpenCommands?: () => void;
}) {
  const { t, locale } = useShellLanguage();
  const cliSessionId = tab.key ?? "";
  // The project that lists the conversation, which the host reads it for; none for a conversation of no project, read in
  // the space the window shows. Both with the provider that listed it, whose account folder has its transcript.
  const providerId = claudeCodeTabProvider(tab);
  const project = useMemo(() => tab.projectId ? { id: tab.projectId, path: tab.projectPath, spaceId, providerId } : null,
    [tab.projectId, tab.projectPath, spaceId, providerId]);
  const readScope = useMemo(() => project ?? (spaceId || providerId ? { spaceId, providerId } : null), [project, spaceId, providerId]);
  const sessionId = previewSessionId(cliSessionId);
  const origin = tab.origin ?? "unknown";
  const [memory] = useState(() => scrollMemory ?? createTimelineScrollMemory());
  const timeline = useTimelinePosition(sessionId, memory);
  const settled = useRef(() => { }); settled.current = () => timeline.settled();
  const onSettled = useCallback(() => settled.current(), []);
  const composer = useComposerLayout(undefined, noop);
  const [unavailable, setUnavailable] = useState<string | null>(null);
  // The session of CodeAlta that continues the conversation now, and whether a message waited under the preview then.
  const [continued, setContinued] = useState<Readonly<{ sessionId: string; kept: boolean }> | null>(null);
  const existing = useRef(onExisting); existing.current = onExisting;
  const holdsMessage = useRef((): boolean => false);
  const report = useCallback((status: ClaudeCodePreviewStatus) => {
    if (status.kind === "existing") {
      // A message typed under the preview is never left behind: the preview stays with it.
      const kept = holdsMessage.current();
      setContinued({ sessionId: status.sessionId, kept });
      if (!kept) existing.current(status.sessionId, false);
    }
    else setUnavailable(status.kind === "unavailable" ? status.code : null);
  }, []);
  const read = useMemo(() => epoch ? previewReader(previewApi, epoch, readScope, cliSessionId, report) : null, [previewApi, epoch, readScope, cliSessionId, report]);
  // The tick that has the timeline read again; none before the first, so that the first page is read once.
  const [tick, setTick] = useState(0);
  const reading = !!read && visible && !unavailable && !continued && refreshMilliseconds > 0;
  useEffect(() => {
    if (!reading) return;
    const timer = setInterval(() => setTick(value => value + 1), refreshMilliseconds);
    return () => clearInterval(timer);
  }, [reading, refreshMilliseconds]);
  const refresh = useMemo(() => tick ? previewRefreshView(sessionId, tick) : null, [sessionId, tick]);

  // Who holds the conversation now (VS Code, a terminal), read while the preview is shown.
  const readHolder = useMemo(() => epoch ? async () => {
    const reply = await api.conversation({ expectedHostEpoch: epoch, projectId: project?.id ?? null, projectPath: project?.path ?? null, cliSessionId, spaceId, providerId });
    return reply.status === "ok" && reply.cliSessionId === cliSessionId ? reply.held : null;
  } : null, [epoch, project, spaceId, providerId, cliSessionId, api]);
  const elsewhere = useConversationElsewhere(readHolder, visible, busy);
  const held = useElsewhereText(elsewhere.held);
  // The banner in the place of the text of the prompt: open when the preview is shown, until the user collapses it, and
  // again when another process takes the conversation or starts to work on it.
  const [bannerOpen, setBannerOpen] = useState(true);
  useEffect(() => setBannerOpen(true), [cliSessionId]);
  const banner = bannerOpen || elsewhere.covers;

  // The draft: kept by conversation, in the window and in storage, until its message is sent.
  const scope = previewDraftScope(tab.projectId, cliSessionId);
  const storageKey = previewDraftStorageKey(scope);
  drafts.get(scope, () => restoreDraft(() => localStorage.getItem(storageKey), scope));
  const edit = (text: string) => {
    drafts.edit(scope, text);
    persistDraft((_key, value) => localStorage.setItem(storageKey, value), () => localStorage.removeItem(storageKey), scope, text);
  };
  const imageKey = previewImageKey(epoch, tab.projectId, cliSessionId);
  const waiting = busy || importingElsewhere;
  const imageGeneration = useRef(0);
  const images = useLocalDraftImages(imageDrafts, imageKey, () => {
    const generation = imageGeneration.current;
    return epoch && !waiting ? () => imageGeneration.current === generation : null;
  }, () => { imageGeneration.current++; }, locale);
  holdsMessage.current = () => !!(drafts.peek(scope)?.text ?? "").trim() || images.images.length > 0;
  const canSend = () => !!epoch && !waiting && !!target && holdsMessage.current();
  const choices = projects.filter(value => !value.archived);
  const submit = () => {
    if (!canSend()) return;
    onSend({ text: drafts.peek(scope)?.text ?? "", images: images.images, imageKey, scope });
  };
  const title = tab.name || t("Claude Code");
  // The project the first message continues the conversation in: in the banner, then on the line above the prompt.
  const targetRow = target
    ? <div className="claude-code-preview-target"><label htmlFor={`claude-code-target-${cliSessionId}`}>{t("Your first message continues it in")}</label>
      <HTMLSelect id={`claude-code-target-${cliSessionId}`} minimal value={target.id} disabled={waiting || !onTargetChange}
        aria-label={t("Project the conversation continues in")}
        onChange={event => { const chosen = choices.find(value => value.id === event.currentTarget.value); if (chosen) onTargetChange?.(chosen); }}>
        {choices.map(value => <option key={value.id} value={value.id} title={value.path}>{value.name}</option>)}
      </HTMLSelect></div>
    : <div className="claude-code-preview-target">{t("Add a project to continue this conversation in CodeAlta.")}</div>;
  return <div className="session-workspace claude-code-preview" data-active="true" ref={composer.workspaceRef} data-claude-code-preview={cliSessionId}>
    <div className="session-timeline-area">
      <div className="timeline-scroll" tabIndex={-1} ref={timeline.elementRef} aria-label={t("Preview of {title}", { title })}
        onScroll={event => timeline.scroll(event.currentTarget)} onWheel={timeline.wheel} onKeyDown={timeline.keyDown} onPointerDown={timeline.pointerDown}
        onPointerMove={timeline.pointerMove} onPointerUp={timeline.pointerEnd} onPointerCancel={timeline.pointerEnd}>
        {continued
          ? <div role="status" className="notice claude-code-preview-continued">
            <p>{t(continued.kept ? "A session of CodeAlta continues this conversation now. Your message stayed here: send it to give it to that session."
              : "A session of CodeAlta continues this conversation now.")}</p>
            <Button icon={<AppIcon name="open" size={14} />} text={t("Open the session")} onClick={() => existing.current(continued.sessionId, continued.kept)} />
          </div>
          : unavailable
          ? <p role="alert" className="notice claude-code-preview-unavailable">{t(previewUnavailableMessage(unavailable))}</p>
          : read && <History key={`${epoch}:${cliSessionId}`} sessionId={sessionId} observing={visible} read={read} live={refresh} onNotesChange={noop} onSettled={onSettled}
            onBeforeOlder={timeline.beforeOlderPage} onAfterOlder={timeline.afterOlderPage} olderRequest={timeline.olderRequestRef} />}
      </div>
    </div>
    {!timeline.following && <button type="button" className="timeline-bottom-button" onClick={() => timeline.jump()}><AppIcon name="arrowDown" size={14} />{t("Jump to latest visible")}</button>}
    {/* Once the banner is collapsed: where the first message goes, and who else holds the conversation, right above the prompt. */}
    <ConversationElsewhereLine elsewhere={elsewhere} leading={targetRow} shown={!banner} />
    <div className="composer-resize-bar" ref={composer.barRef}><ComposerSplitter {...composer.splitter} /></div>
    <div ref={composer.regionRef} className={`composer-region${composer.height === undefined ? "" : " resized"}`}
      style={composer.height === undefined ? undefined : { height: composer.height }}>
      <SessionWidthGrips />
      <ComposerChrome.Provider value={banner ? { promptCover: <ConversationBanner working={held?.working} onCollapse={() => { setBannerOpen(false); elsewhere.dismiss(); }}
        text={<>
          <span className="conversation-banner-title"><ProviderIcon providerKey={tab.icon ?? null} size={15} fallback="assistant" />{t(previewBannerText(origin))}</span>
          {held && <span className="conversation-banner-title conversation-banner-held"><AppIcon name={held.icon} size={15} />{held.text}<span className="muted-text">{held.detail}</span></span>}
        </>}
        primaryWrite={t("Continue in CodeAlta")}
        actions={<Button icon={<AppIcon name="openExternal" size={14} />} text={t("Open in VS Code")} disabled={!epoch || waiting} onClick={onOpenInVsCode} />}>
        {targetRow}</ConversationBanner> } : noChrome}>
      <ReadOnlyComposer key={scope} sessionId={scope} provider={tab.icon ?? null} draftIndicators={draftIndicators} active={visible}
        placeholder={t(previewPlaceholder(origin))} busyStatus={t(busy ? "Importing…" : "Another conversation is being imported…")} onOpenHelp={onOpenHelp} onOpenCommands={onOpenCommands}
        reason={t("Your first message imports this conversation into CodeAlta, then sends the message.")}
        localImages={epoch ? images : undefined}
        localDraft={{ read: () => drafts.peek(scope)?.text ?? "", subscribe: drafts.subscribe, edit, disabled: !epoch || waiting, busy: waiting, submit,
          action: <Button intent="primary" icon={<AppIcon name="send" size={16} />} disabled={!epoch || waiting || !target}
            aria-label={t("Send")} title={t("Continue the conversation in CodeAlta and send (Enter)")} onClick={submit} /> }} />
      </ComposerChrome.Provider>
    </div>
  </div>;
}
