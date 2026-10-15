import { memo, useContext, useEffect, useId, useLayoutEffect, useRef, useState } from "react";
import { BackgroundCallsContext } from "./BackgroundTaskViews";
import { AppIcon } from "./AppIcon";
import { MarkdownContent, MarkdownLinkScopeContext, MarkdownLinksContext } from "./MarkdownContent";
import { PluginHtml } from "./PluginHtml";
import { externalOriginSubtitles, notSentSubtitle, writeMarkdown, type TimelineItem } from "./timeline";
import { SessionReference } from "./SessionReference";
import { useShellLanguage } from "./shellLanguage";
import { timelineTime } from "./sessionTime";
import { FileChangeInspection } from "./FileChangeInspection";
import type { HistorySourceTarget } from "./HistorySource";
import { TimelineDetails } from "./TimelineDetails";
import { TimelineImages } from "./TimelineImageStrip";
import { AttachedFileChips } from "./PromptImageAttachments";
import type { TimelineImageSource } from "./timelineImages";
import { ActivitySpinner } from "./ActivitySpinner";
import { formatSize, toolState } from "./toolCall";
import { useToolOutput, type ToolOutputs } from "./toolOutput";
import { modalDialogOpen } from "./modalDialogs";
import { SessionTabMenu } from "./SessionTabMenu";
import { canBranchFrom, type BranchMode } from "./sessionBranch/branchPoints";
import { canBookmark } from "./bookmarks/bookmarks";
import { useRowBookmark } from "./bookmarks/timelineBookmarks";
import { SystemPromptReaderContext } from "./SystemPromptDialog";
import { promptProblem, promptTokens } from "./systemPrompt";
import { usePromptChange } from "./systemPromptReader";
import { TimelineClaudeCli } from "./claudeCli/timelineCard";

const longBodyThreshold = 1200;
const previewLength = 240;
const commandPreviewLength = 80;

/** The host-owned opener of the links of a message: the one of every Markdown of the window. A detached fixture has none. */
export const MessageLinksContext = MarkdownLinksContext;

// Display-only command identity, never a command parser or execution target.
export function commandPreview(source: string): string {
  const line = source.trim().replace(/\s+/g, " ");
  if (line.length <= commandPreviewLength) return line;
  const last = line.charCodeAt(commandPreviewLength - 2);
  const end = last >= 0xd800 && last <= 0xdbff ? commandPreviewLength - 2 : commandPreviewLength - 1;
  return line.slice(0, end) + "…";
}

type TimelineMessageProps = { item: TimelineItem; canInspect?: () => boolean; toolTile?: boolean;
  historySource?: HistorySourceTarget; onOpenSource?: (target: HistorySourceTarget) => void;
  /** Reads the images of the item, when it has some. */
  imageSource?: TimelineImageSource;
  /** The key of the row, and what opens the window of a tool call: given the key and the button that asked. */
  rowKey?: string; onOpenTool?: (rowKey: string, origin: HTMLButtonElement) => void;
  /** The live output of the tool calls: the tile of a running call says what it writes. */
  toolOutputs?: ToolOutputs;
  /** Opens the window of a fork or a rewind on this message, given its journal offset: only the prompt of a turn is given it. */
  onBranch?: (promptOffset: string, mode: BranchMode) => void };

// A long timeline is rendered again on every page of history and every live update: a row whose item,
// source range and image reader are the same has nothing to redo.
// What a tile says of a call whose task goes on in the background, or ended there without running to its end.
const backgroundOutcome = { running: "Running in the background", failed: "Failed in the background", stopped: "Stopped in the background" } as const;

function sameMessage(previous: TimelineMessageProps, next: TimelineMessageProps): boolean {
  const before = previous.historySource, after = next.historySource;
  return previous.item === next.item && previous.toolTile === next.toolTile && previous.canInspect === next.canInspect
    && previous.onOpenSource === next.onOpenSource && previous.imageSource?.key === next.imageSource?.key
    && previous.rowKey === next.rowKey && previous.onOpenTool === next.onOpenTool && previous.toolOutputs === next.toolOutputs
    && previous.onBranch === next.onBranch
    && (before === after || !!before && !!after && before.start === after.start && before.end === after.end
      && before.revision.sessionId === after.revision.sessionId && before.revision.length === after.revision.length
      && before.revision.lastWriteUtcTicks === after.revision.lastWriteUtcTicks);
}

export const TimelineMessage = memo(function TimelineMessage({ item, canInspect, historySource, onOpenSource, toolTile = false, imageSource,
  rowKey, onOpenTool, toolOutputs, onBranch }: TimelineMessageProps) {
  const { t, locale } = useShellLanguage();
  const job = item.jobResult;
  toolTile ||= !!job;
  // The row of a system prompt is one line that opens its window, wherever it is pressed.
  const prompt = item.prompt;
  const clickableTile = item.category === "tool" || !!job || !!prompt;
  // What a prompt changed is in its row: from the history page, or asked once the row is shown.
  const promptChange = usePromptChange(useContext(SystemPromptReaderContext), prompt && /^\d+$/u.test(item.key) ? item.key : null,
    prompt?.change ?? null, !!prompt?.changed);
  const promptWarning = prompt ? promptProblem(prompt) : null;
  // The links of the text of a message are followed while the row is the one that is shown: a relative path
  // starts from the folder its session works in.
  const openLink = useContext(MessageLinksContext);
  const linkScope = useContext(MarkdownLinkScopeContext);
  const messageLink = openLink ? (address: string) => { if (canInspect?.() ?? true) openLink(address, linkScope); } : null;
  const state = job?.phase ?? (item.category === "tool" ? toolState(item.toolPhase) : null);
  // A call that started a task only says that it returned: what its task does in the background is said beside.
  const backgroundCalls = useContext(BackgroundCallsContext);
  const background = item.category === "tool" && state === "completed" && item.toolCall ? backgroundCalls.get(item.toolCall.activityId) : undefined;
  // A call that runs is followed: its tile says how much it wrote and its last line.
  const live = useToolOutput(toolOutputs, item.toolCall?.activityId, toolTile && (state === "running" || state === "pending"));
  const timestamp = timelineTime(item.timestamp, locale);
  const toolTrigger = useRef<HTMLButtonElement>(null);
  // A prompt the user wrote, kept in the journal, is where a session can be forked from or rewound to. Both
  // open a window that says what they do: neither acts at once.
  const branch = onBranch && canBranchFrom(item) ? (mode: BranchMode) => { if (!modalDialogOpen() && (canInspect?.() ?? true)) onBranch(item.key, mode); } : null;
  // A message the journal holds whole can be bookmarked where the timeline keeps bookmarks: the row follows its own mark.
  const bookmarkable = canBookmark(item);
  const { marks, bookmarkId } = useRowBookmark(bookmarkable ? item.key : null);
  const bookmark = marks && bookmarkable ? () => { if (!modalDialogOpen() && (canInspect?.() ?? true)) marks.toggle(item.key); } : null;
  const article = useRef<HTMLElement>(null);
  const [branchMenu, setBranchMenu] = useState<{ x: number; y: number } | null>(null);
  const [copyState, setCopyState] = useState<"idle" | "copied" | "failed">("idle");
  const [details, setDetails] = useState<{ item: TimelineItem; origin: HTMLButtonElement; current?: () => boolean } | null>(null);
  const latestItem = useRef(item); latestItem.current = item;
  // Reconciliation may allocate an identical presentation on an unrelated App render.
  // Revision-key remounts and changed presentation values still retire the dialog.
  const sameDetailItem = () => !!details && (latestItem.current === details.item || JSON.stringify(latestItem.current) === JSON.stringify(details.item));
  const detailCurrent = () => sameDetailItem() && (details?.current?.() ?? true) && (canInspect?.() ?? true);
  function closeDetails() {
    const origin = details?.origin;
    const restore = detailCurrent();
    setDetails(null);
    if (restore && origin) requestAnimationFrame(() => {
      if (origin.isConnected && !origin.closest('[inert], [hidden]') && (canInspect?.() ?? true)
        && sameDetailItem() && (details?.current?.() ?? true)
        && !modalDialogOpen()) origin.focus();
    });
  }
  const [disclosure, setDisclosure] = useState<{ source: string; expanded: boolean; current?: () => boolean } | null>(null);
  const bodyId = useId();
  // A changed record at the same offset must not inherit the previous record's disclosure.
  const body = item.markdown;
  // Conversation messages always render their supplied Markdown. Optional
  // disclosure is reserved for long supporting diagnostics, never the conversation.
  const longBody = item.category !== "user" && item.category !== "assistant" && (body?.length ?? 0) > longBodyThreshold;
  // Value identity of the bounded presentation, not original journal bytes. Do
  // not serialize terse/detail-only records that have no inline disclosure.
  const source = longBody ? JSON.stringify(item) : "";
  const latest = useRef({ source, canInspect }); latest.current = { source, canInspect };
  const current = () => latest.current.source === source && (canInspect?.() ?? true) && (latest.current.canInspect?.() ?? true);
  const expanded = longBody && disclosure?.source === source && disclosure.expanded && (disclosure.current?.() ?? true) && current();
  // Invalidate the committed source lifetime, including A -> B -> A. Compare metadata
  // values, not arrays allocated by each History refresh; retained records keep expansion.
  useLayoutEffect(() => {
    setDisclosure(null);
  }, [source]);
  useLayoutEffect(() => {
    if (disclosure && (!(disclosure.current?.() ?? true) || !current())) setDisclosure(null);
  });
  useEffect(() => {
    if (!longBody) return;
    const retire = (event: Event) => { if (event.target instanceof HTMLDialogElement) setDisclosure(null); };
    document.addEventListener("beforetoggle", retire, true);
    return () => document.removeEventListener("beforetoggle", retire, true);
  }, [longBody]);
  const reset = useRef<number | undefined>(undefined);
  const active = useRef(false);
  const copySequence = useRef(0);
  useLayoutEffect(() => {
    active.current = true;
    setCopyState("idle");
    return () => {
      active.current = false;
      copySequence.current++;
      if (reset.current !== undefined) window.clearTimeout(reset.current);
      reset.current = undefined;
    };
  }, [item.key, item.eventType, item.category, item.title, item.timestamp, item.copyMarkdown, item.truncated, item.bodyOmitted]);
  async function copy() {
    const text = item.copyMarkdown;
    if (!text || !active.current) return;
    const sequence = ++copySequence.current;
    if (reset.current !== undefined) window.clearTimeout(reset.current);
    reset.current = undefined;
    setCopyState("idle");
    const state = await writeMarkdown(value => navigator.clipboard.writeText(value), text);
    if (!active.current || sequence !== copySequence.current) return;
    setCopyState(state);
    reset.current = window.setTimeout(() => {
      if (!active.current || sequence !== copySequence.current) return;
      reset.current = undefined;
      setCopyState("idle");
    }, 1600);
  }
  const hasDetails = !!(body || item.detailMarkdown || item.details || item.metadata.length || item.detailSections?.length || prompt);
  const compact = !!job || ["status", "reasoning", "tool", "file", "prompt", "plugin"].includes(item.category);
  const excerpt = (item.summary || body || item.detailMarkdown || "").split(/\r?\n\s*\r?\n/)[0];
  const codePreview = clickableTile && item.summaryIsCode
    ? commandPreview(excerpt) : null;
  const toolOutput = live?.last || item.toolOutput;
  const outcome = ({ completed: "Completed", failed: "Failed", canceled: "Canceled", requested: "Pending",
    started: "Running", progressed: "Running", selected: "Running", deselected: "Completed" } as Record<string, "Completed" | "Failed" | "Canceled" | "Pending" | "Running">)[item.toolPhase ?? ""];
  // Categories below have fixed UI titles in toTimelineItem; tool/provider names do not.
  const title = item.category === "file" ? t("Modified files") : item.category === "image" ? t("Image read") : item.category === "user" ? t(item.delegated ? "Agent message" : item.jobResult ? "Background job" : "You") : item.category === "plan" ? t("Plan")
    : item.category === "assistant" ? t("Assistant") : item.category === "notes" ? t("Notes") : item.category === "error" ? t("Error")
    : item.category === "reasoning" ? t(item.title === "Reasoning summary" ? "Reasoning summary" : "Reasoning")
    : prompt ? t(prompt.changed ? "System prompt changed" : "System prompt recorded") : item.title;
  // The agent prompt by its name, then the size of the prompt.
  const promptFacts = prompt ? [prompt.name, prompt.tokens > 0 ? t("{count} tokens", { count: promptTokens(prompt.tokens) }) : null].filter(Boolean).join(" · ") : null;
  const copyLabel = copyState === "copied" ? t("Copied") : copyState === "failed" ? t("Copy failed") : t("Copy {title} as Markdown", { title });
  // A tool call has its own window, which the timeline holds: it follows the call while it runs. A row that is
  // no call (an output whose call is not in the window) keeps the details of its record.
  const callWindow = item.category === "tool" && !!item.toolCall && !!onOpenTool && !!rowKey;
  function openDetails(origin: HTMLButtonElement) {
    if (!current() || !origin.isConnected || origin.closest('[inert], [hidden]')
      || modalDialogOpen()) return;
    if (callWindow) onOpenTool!(rowKey!, origin);
    else setDetails({ item, origin, current: canInspect });
  }
  return <article ref={article} className={`message timeline-message message-${job ? "job" : item.category}${compact ? " timeline-compact" : ""}`}
    data-prompt-offset={item.category === "user" && /^\d+$/u.test(item.key) ? item.key : undefined}
    // The offset of a message the journal holds, by which the timeline goes to it; and whether it is bookmarked.
    data-row-offset={bookmarkable ? item.key : undefined} data-bookmarked={bookmarkId ? "true" : undefined}
    // The menu of the prompt, unless text is selected or the press is on a link or a field: those keep the menu of the system.
    onContextMenu={branch ? event => {
      if (event.defaultPrevented || window.getSelection()?.toString() || (event.target as HTMLElement).closest("a, input, textarea, dialog, .session-tab-popup")) return;
      event.preventDefault();
      setBranchMenu({ x: event.clientX, y: event.clientY });
    } : undefined}
    onClick={event => {
      if (clickableTile && hasDetails && toolTrigger.current && !event.defaultPrevented
        && !(event.target as HTMLElement).closest("button, a, dialog, input, textarea") && !window.getSelection()?.toString()) openDetails(toolTrigger.current);
    }}
    data-tool-phase={job?.phase ?? item.toolPhase} data-tool-background={background} data-delegated={item.delegated ? "true" : undefined} data-not-sent={item.notSent ? "true" : undefined} data-job-result={job ? job.succeeded ? "succeeded" : "ended" : undefined} data-persisted-message={item.category === "user" || item.category === "assistant" ? "true" : undefined}>
    <div className="avatar"><AppIcon name={item.icon} size={17} /></div>
    <div className="message-body">
      <div className="message-heading">
        <span>{clickableTile && hasDetails ? <button ref={toolTrigger} type="button" className="tool-tile-title" aria-haspopup="dialog"
          aria-label={job ? `${title} · ${item.summary} · ${job.result}` : undefined}
          // Open on Enter's release: opening on its press focuses the window's Blueprint Close button,
          // which would interpret that same release as its own activation and immediately close it.
          onKeyDown={event => { if (event.key === "Enter" || event.key === " " && (event.repeat || event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229)) event.preventDefault(); }}
          onKeyUp={event => { if (event.key === "Enter" && !event.defaultPrevented && !event.repeat && !event.nativeEvent.isComposing && event.nativeEvent.keyCode !== 229) openDetails(event.currentTarget); }}
          onClick={event => { if (!event.defaultPrevented) openDetails(event.currentTarget); }}>{state === "running" || background === "running"
            ? <ActivitySpinner size={10} className="tool-state-spinner" /> : !job && !prompt && <span className="tool-state-dot" aria-hidden="true">●</span>} <strong>{title}</strong></button>
          : item.category !== "reasoning" && <strong>{title}</strong>}{prompt ? promptFacts && <small>{promptFacts}</small>
          : !toolTile && item.subtitle && <small>{item.subtitle === "Sending…" || item.subtitle === "Pending" || item.subtitle === "Failed" || item.subtitle === "Streaming" ? t(item.subtitle) : item.notSent ? t(notSentSubtitle) : item.madeElsewhere ? t(externalOriginSubtitles[item.madeElsewhere]) : item.subtitle}</small>}
          {item.delegated && item.sourceSessionId && <SessionReference sessionId={item.sourceSessionId} />}</span>
        {compact && item.html ? <div className="timeline-inline-preview timeline-plugin-html"><PluginHtml html={item.html} pluginKey={item.pluginKey} script={{ path: item.script ?? null, problem: item.scriptProblem ?? null, whenShown: true }} /></div>
          : compact && excerpt && !toolTile && item.category !== "file" && <div className="timeline-inline-preview">{codePreview !== null ? <code>{codePreview}</code>
          : item.summary ? excerpt : <MarkdownContent source={excerpt} onOpenLink={null} />}</div>}
        {!toolTile && item.toolChanges && <span className="file-counts tool-changes" title={t("Lines added and removed by this call")}><b>+{item.toolChanges.added}</b> <em>−{item.toolChanges.removed}</em></span>}
        {promptChange && <span className="file-counts prompt-changes" title={t("Lines added and removed")}><b>+{promptChange.added}</b> <em>−{promptChange.removed}</em></span>}
        {promptWarning && <span className="prompt-warning" role="img" aria-label={t(promptWarning === "lossy" ? "The provider could not take this prompt as it is written." : "This prompt was not sent to the provider.")}
          title={t(promptWarning === "lossy" ? "The provider could not take this prompt as it is written." : "This prompt was not sent to the provider.")}><AppIcon name="warning" size={13} /></span>}
        <span className="message-actions">
          {hasDetails && !callWindow && !job && !prompt && <button type="button" className="timeline-detail-trigger" aria-label={t("Details")} title={t("Details")} aria-haspopup="dialog"
            onKeyDown={event => { if ((event.key === "Enter" || event.key === " ") && (event.repeat || event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229)) event.preventDefault(); }}
            onClick={event => { if (!event.defaultPrevented) openDetails(event.currentTarget); }}><AppIcon name="info" size={15} /></button>}
          {branch && <button type="button" className="branch-from-message" aria-label={t("Fork from here")} title={t("Fork from here")} aria-haspopup="dialog"
            onClick={() => branch("fork")}><AppIcon name="branch" size={15} /></button>}
          {branch && <button type="button" className="branch-from-message" aria-label={t("Rewind to here")} title={t("Rewind to here")} aria-haspopup="dialog"
            onClick={() => branch("rewind")}><AppIcon name="rewind" size={15} /></button>}
          {item.copyMarkdown && <button type="button" className={`copy-markdown copy-${copyState}`} onClick={() => void copy()}
            aria-label={copyLabel} title={copyLabel}><AppIcon name={copyState === "copied" ? "checked" : copyState === "failed" ? "error" : "copy"} size={15} /><span className="sr-only" aria-live="polite">{copyState === "idle" ? "" : copyLabel}</span></button>}
          {/* The last of the actions: it stays drawn on a bookmarked message, beside the time. */}
          {bookmark && <button type="button" className="bookmark-message" aria-pressed={!!bookmarkId} aria-label={t(bookmarkId ? "Remove bookmark" : "Bookmark this message")}
            title={t(bookmarkId ? "Remove bookmark" : "Bookmark this message")} onClick={bookmark}><AppIcon name="bookmark" size={15} /></button>}
          <time title={timestamp.title} dateTime={timestamp.dateTime}>{timestamp.label}</time>
        </span>
      </div>
      {toolTile && codePreview && <code className="tool-command-preview">{codePreview}</code>}
      {/* A path too long for the tile loses its start: its last names say which file it is. */}
      {toolTile && !codePreview && item.summary && (/[\\/]/.test(item.summary) && !/\s/.test(item.summary)
        ? <div className="tool-argument-preview" data-path title={item.summary}><bdi>{item.summary}</bdi></div>
        : <div className="tool-argument-preview">{commandPreview(item.summary)}</div>)}
      {toolTile && <div className="tool-result-summary">
        {job && <span className="tool-outcome job-result-outcome" title={job.result}>{job.result}</span>}
        {outcome && <span className="tool-outcome">{t(background ? backgroundOutcome[background] : outcome)}</span>}
        {!!item.toolExitCode && <span className="tool-exit-code">{t("Exit code {code}", { code: item.toolExitCode })}</span>}
        {item.toolChanges && <span className="file-counts tool-changes" title={t("Lines added and removed by this call")}><b>+{item.toolChanges.added}</b> <em>−{item.toolChanges.removed}</em></span>}
        {live && live.total > 0 ? <span className="tool-output-stats">{live.lines}L · {formatSize(live.total)}</span>
          : item.toolOutputBytes != null && item.toolOutputLines != null && <span className="tool-output-stats">{item.toolOutputLines}L · {(item.toolOutputBytes / 1024).toFixed(1)} KB</span>}
        {toolOutput && <span className="tool-result-preview" title={toolOutput.slice(0, 512)}>{commandPreview(toolOutput)}</span>}
      </div>}
      {!compact && item.summary && (item.summaryIsCode ? <code className="timeline-primary-code">{item.summary}</code> : <p className="timeline-summary">{item.summary}</p>)}
      {!compact && (body && longBody ? <>
        <button type="button" className="quiet-button long-message-toggle" aria-controls={bodyId}
          aria-expanded={expanded} disabled={!current()}
          onKeyDown={event => { if ((event.key === "Enter" || event.key === " ") && (event.repeat || event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229)) event.preventDefault(); }}
          onClick={event => {
            if (event.defaultPrevented || !active.current || !current() || !event.currentTarget.isConnected || event.currentTarget.closest("[inert]")
              || modalDialogOpen()) return;
            setDisclosure({ source, expanded: !expanded, current: canInspect });
          }}>
          <AppIcon name="chevronDown" size={14} />{t(expanded ? "Collapse message" : "Show full message")}
        </button>
        <div id={bodyId}>{expanded ? <MarkdownContent source={body} timelineCodeBlocks onOpenLink={messageLink} />
          : <p className="long-message-preview">{t("Preview (plain text):")} {plainTextPreview(body)}…</p>}</div>
      </> : body && <MarkdownContent source={body} timelineCodeBlocks onOpenLink={messageLink} />)}
      {item.images && <TimelineImages images={item.images} source={imageSource} />}
      {item.attachments && <AttachedFileChips attachments={item.attachments} />}
      {item.missingCliProvider && <TimelineClaudeCli providerKey={item.missingCliProvider} />}
      {item.fileChanges && <FileChangeInspection key={item.fileChanges.source} changes={item.fileChanges} canInspect={canInspect} />}
      {details && <TimelineDetails item={details.item} current={detailCurrent} onClose={closeDetails} />}
      {branch && branchMenu && article.current && <SessionTabMenu anchor={article.current} at={branchMenu} container={document.body}
        title={t("Actions for this prompt")} current={() => canInspect?.() ?? true} onClose={() => setBranchMenu(null)}
        items={[{ key: "fork", label: t("Fork from here"), icon: "branch", onSelect: () => branch("fork") },
          { key: "rewind", label: t("Rewind to here"), icon: "rewind", onSelect: () => branch("rewind") }]} />}
    </div>
  </article>;
}, sameMessage);

// Never parse a cut Markdown document: React escapes this inert excerpt as plain text. Avoid splitting a surrogate pair.
function plainTextPreview(source: string): string {
  const last = source.charCodeAt(previewLength - 1);
  const end = last >= 0xd800 && last <= 0xdbff ? previewLength - 1 : previewLength;
  return source.slice(0, end);
}
