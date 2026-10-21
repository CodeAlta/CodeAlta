import { matchesOutgoing } from "./outgoingEcho";
import { Component, memo, useCallback, useContext, useEffect, useLayoutEffect, useMemo, useRef, useState, type CSSProperties, type RefObject } from "react";
import { Button } from "@blueprintjs/core";
import { workspace, type HistoryRequest, type SessionDisplayView, type SessionPluginEvent } from "#neoastra";
import { ActivitySpinner } from "./ActivitySpinner";
import { AppIcon } from "./AppIcon";
import { BackgroundCallsContext } from "./BackgroundTaskViews";
import { historyCanRetry, historyMessage, historySettled, loadHistory, mergeHistoryPage, olderHistoryStep, type HistoryState, type HistoryTimeline } from "./history";
import { liveTextItem, liveToolItem } from "./liveTimeline";
import { orderTimelineRows, reconcileTimeline, type ReconciledRow } from "./reconcileTimeline";
import { groupTimelineTools } from "./toolGroups";
import { emphasisProperties, groupEntryTypes } from "./timelineEmphasis/timelineEmphasis";
import { useTimelineEmphasis } from "./timelineEmphasis/useTimelineEmphasis";
import type { HistorySources } from "./timelineGroupView";
import { TimelineGroupView } from "./TimelineEntryGroup";
import { activityExpanded, activitySummary, foldTimeline } from "./timelineActivity/activityFold";
import { TimelineActivity, type TimelineGroupContext } from "./timelineActivity/TimelineActivity";
import type { TimelineGroup } from "./toolGroups";
import { latestNotes, latestUsageText, unsentPrompt, type UnsentPrompt } from "./timeline";
import { pluginEventItems, pluginEventsWindow, type PluginEventsRead } from "./pluginEvents";
import { useShellLanguage } from "./shellLanguage";
import { HistorySource, type HistorySourceTarget } from "./HistorySource";
import type { OutgoingMessage } from "./sessionOperations";
import { inlineImageSource, type TimelineImageReader, type TimelineImageSource } from "./timelineImages";
import { ToolCallDialog } from "./ToolCallDialog";
import type { ToolCallReader } from "./toolCallReader";
import { SystemPromptReaderContext } from "./SystemPromptDialog";
import type { SystemPromptReader } from "./systemPromptReader";
import type { ToolOutputs } from "./toolOutput";
import type { TimelineItem } from "./timeline";
import { modalDialogOpen } from "./modalDialogs";
import { turnPromptKeys, type BranchMode } from "./sessionBranch/branchPoints";
import { revealStep, type RevealOutcome, type RevealRequest } from "./bookmarks/revealRequests";
import { TimelineBookmarksContext } from "./bookmarks/timelineBookmarks";

// The production caller supplies readTimeline (workspace.historyTimeline). The injection seam lets the mounted
// browser fixture exercise this exact component with an isolated, revisioned test journal.
export type NewestHistoryResult = Readonly<{ sessionId: string; generation: number; revision: string | null;
  error: string | null }>;
export type NewestHistoryRequest = () => number;
/**
 * Asked by the panel that scrolls the timeline when its reader goes up near the top of what is loaded: the
 * chunk of turns before it is read, as far as the rules of `olderHistoryStep` allow. Says whether a read started.
 */
export type OlderHistoryRequest = () => boolean;

// `olderFrom` is the first record of the window when older history was asked for; `known` is the settled
// window a refresh of the newest history keeps and appends to. `all` reads older history to the start of
// the journal instead of one chunk of turns.
type HistoryTarget = { request: HistoryRequest; explicitOlder: boolean; explicitNewest: boolean; generation: number;
  olderFrom?: string; known?: HistoryTimeline; all?: boolean };

/**
 * While everything is being loaded the timeline takes what was read in small batches: at least this many
 * records and this many milliseconds apart. A batch is what the window is busy with at once, and each one
 * rebuilds the list, so they are neither large nor frequent.
 */
const publicationRecords = 300, publicationInterval = 400;
const noEntries: HistoryTimeline["entries"] = [];
const noChoices: ReadonlyMap<string, boolean> = new Map();

/**
 * The rows of the timeline: the persisted and live records, the prompts being sent and the cards plugins
 * derive, in order, with consecutive tool calls grouped and the work between two messages folded into one
 * activity line. It is rendered again only when one of these changes, not on every page the panel reads,
 * each group only when one of its rows did, and each row only when its own item changed.
 */
const TimelineRows = memo(function TimelineRows({ sessionId, rows, entries, outgoing, onAcknowledgeOutgoing, pluginEvents, echoes: showEchoes, empty,
  revision, sources, readImages, readTool, readPrompt, toolOutputs, canInspect, onOpenSource, promptKeys, onBranch, running, shown }: {
  sessionId: string; rows: readonly ReconciledRow[]; entries: HistoryTimeline["entries"];
  outgoing: readonly OutgoingMessage[]; onAcknowledgeOutgoing?: (keys: readonly string[]) => void;
  pluginEvents?: readonly SessionPluginEvent[];
  /** The window reaches the journal end: prompts being sent belong after it. */
  echoes: boolean;
  /** The history was read and holds nothing to show. It only changes while there are no rows. */
  empty: boolean;
  revision: HistoryTimeline["revision"] | null; sources: HistorySources;
  readImages?: TimelineImageReader; canInspect: () => boolean; onOpenSource: (target: HistorySourceTarget) => void;
  /** Reads the whole record of a tool call, and follows what a running one writes. */
  readTool?: ToolCallReader; toolOutputs?: ToolOutputs;
  /** Reads the system prompts of the session: the rows of its prompt records and their window use it. */
  readPrompt?: SystemPromptReader;
  /** The prompts that start a turn, by journal offset, and what opens the window of a fork or a rewind on one of them. */
  promptKeys?: ReadonlySet<string>; onBranch?: (promptOffset: string, mode: BranchMode) => void;
  /** Whether a run of the session goes on: the activity at the end of the timeline is its work. Unknown, a call that runs says it. */
  running?: boolean;
  /** The journal offset of a message the timeline is asked to show: the activity that holds it opens. */
  shown?: string | null;
}) {
  const { t } = useShellLanguage();
  const [emphasis] = useTimelineEmphasis();
  const focused = emphasis.focused;
  // The activities the reader opened or closed, by the keys of their rows: a line keeps the reader's choice while
  // it grows, and changing the mode of the timeline gives every line the default of the new mode.
  const [choices, setChoices] = useState<{ sessionId: string; focused: boolean; map: ReadonlyMap<string, boolean> }>(() => ({ sessionId, focused, map: noChoices }));
  const chosen = choices.sessionId === sessionId && choices.focused === focused ? choices.map : noChoices;
  const toggleActivity = useCallback((groups: readonly TimelineGroup[], expanded: boolean) => setChoices(current => {
    const map = new Map(current.sessionId === sessionId && current.focused === focused ? current.map : noChoices);
    for (const group of groups) for (const row of group.rows) map.set(row.key, expanded);
    return { sessionId, focused, map };
  }), [sessionId, focused]);
  const emphasisStyle = useMemo(() => emphasisProperties(emphasis) as CSSProperties, [emphasis]);
  // The window of a tool call is held here, by the key of its row: the row changes while the call runs
  // (a live row, then the records of the journal) and the window follows it.
  const [openTool, setOpenTool] = useState<{ sessionId: string; key: string; origin: HTMLButtonElement } | null>(null);
  const openedItem = useRef<TimelineItem | null>(null);
  const openToolWindow = useCallback((key: string, origin: HTMLButtonElement) => { openedItem.current = null; setOpenTool({ sessionId, key, origin }); }, [sessionId]);
  const closeToolWindow = useCallback(() => setOpenTool(current => {
    const origin = current?.origin;
    if (origin) requestAnimationFrame(() => {
      if (origin.isConnected && !origin.closest('[inert], [hidden]') && !modalDialogOpen()) origin.focus();
    });
    return null;
  }), []);
  // The cards of plugins are the same items until the plugins answer again: their rows are not rendered on a live update.
  const firstTimestamp = entries[0]?.timestamp;
  const pluginItems = useMemo(() => pluginEvents ? pluginEventItems(pluginEvents, firstTimestamp) : [], [pluginEvents, firstTimestamp]);
  const items = rows.slice();
  // Match each echo to at most one new source message. Never deduplicate repeated
  // prompts against old history or use this display match to settle a Send intent.
  let runs: Map<string, string | null> | undefined;
  const runOf = (offset: string) => (runs ??= new Map(entries.map(entry => [entry.offset, entry.runId]))).get(offset);
  const availableUsers = !outgoing.length ? [] : items.filter(row => row.source === "history" ? row.item.category === "user" : row.source === "liveText" && row.row.kind.toLowerCase() === "user");
  const acknowledged: string[] = [];
  const echoes = outgoing.filter(echo => {
    if (echo.state === "failed") return true;
    const identity = (row: typeof availableUsers[number]) => row.source === "history" ? row.item.clientRequestId : row.source === "liveText" ? row.row.clientRequestId : null;
    // The message that carries the identity of the Send is the one, whenever it was recorded: an echo shown again by a
    // retry is newer than its message. Only then a message without identity, newer than the echo, by its text.
    let index = availableUsers.findIndex(row => identity(row) === echo.clientRequestId);
    if (index < 0) index = availableUsers.findIndex(row => {
      const timestamp = row.source === "history" ? row.item.timestamp : row.row.timestamp;
      const text = row.source === "history" ? row.item.markdown : row.source === "liveText" ? row.row.text : null;
      const runId = row.source === "history" ? runOf(row.item.key) : row.row.runId;
      return matchesOutgoing(echo, { clientRequestId: identity(row), text, runId })
        && Date.parse(timestamp ?? "") >= Date.parse(echo.timestamp);
    });
    if (index < 0) return true;
    const [matched] = availableUsers.splice(index, 1);
    // The live copy of a prompt has no images or files: the card that holds them stays until the persisted message arrives.
    if (matched.source === "liveText" && (echo.images?.length || echo.attachments?.length)) { items.splice(items.indexOf(matched), 1); return true; }
    acknowledged.push(echo.key); return false;
  });
  useEffect(() => { if (acknowledged.length) onAcknowledgeOutgoing?.(acknowledged); }, [JSON.stringify(acknowledged), onAcknowledgeOutgoing]);
  const echoImages = new Map<string, TimelineImageSource>();
  for (const echo of showEchoes ? echoes : []) {
    const images = echo.images?.length ? echo.images : undefined;
    const item = liveTextItem({ contentId: echo.key, runId: echo.runId, kind: "User", text: echo.text || (echo.imageCount && !images && !echo.attachments?.length ? t("{count} image attached", { count: echo.imageCount }) : ""),
      timestamp: echo.timestamp, sequence: null, isComplete: true, isTruncated: false, startedWithDelta: false });
    if (images) echoImages.set(`outgoing:${echo.key}`, inlineImageSource(`outgoing:${echo.key}`, images));
    items.push({ source: "history", key: `outgoing:${echo.key}`, item: { ...item,
      images: images?.map((image, index) => ({ index, title: image.title, mediaType: image.mediaType })),
      attachments: echo.attachments?.length ? echo.attachments : undefined,
      subtitle: echo.state === "sending" ? "Sending…" : echo.state === "failed" ? "Failed" : echo.state === "uncertain" ? "Pending" : null } });
  }
  for (const item of pluginItems) items.push({ source: "history", key: item.key, item });
  orderTimelineRows(items);
  const groups = groupTimelineTools(items, entries);
  const backgroundCalls = useContext(BackgroundCallsContext);
  const entryTypes = groupEntryTypes(groups, backgroundCalls);
  const blocks = foldTimeline(groups, entryTypes);
  // A message asked for in a closed activity opens it, and it stays open once the timeline has gone there.
  useLayoutEffect(() => {
    if (!shown) return;
    const holder = blocks.find(block => block.kind === "activity" && block.groups.some(group => group.rows.some(row => row.source === "history" && row.item.key === shown)));
    if (holder?.kind === "activity" && !activityExpanded(holder.groups, chosen, focused)) toggleActivity(holder.groups, true);
  });
  const last = blocks.at(-1);
  const liveActivity = last?.kind === "activity" && (running ?? activitySummary(last.groups, backgroundCalls).running > 0) ? last.key : null;
  const context: TimelineGroupContext = { sessionId, revision, sources, echoImages, readImages, toolOutputs, canInspect, onOpenSource,
    onOpenTool: openToolWindow, promptKeys, onBranch };
  const opened = openTool?.sessionId === sessionId ? items.find(row => row.key === openTool.key) : undefined;
  if (opened) openedItem.current = opened.source === "history" ? opened.item : opened.source === "liveTool" ? liveToolItem(opened.row) : openedItem.current;
  // A row that left the window (older history slid away) keeps its details open on what it last was.
  const shownTool = openTool?.sessionId === sessionId ? openedItem.current : null;
  const toolImages = shownTool?.toolCall?.images?.length && shownTool.toolCall.outputOffset ? readImages?.(shownTool.toolCall.outputOffset) : undefined;
  return <SystemPromptReaderContext.Provider value={readPrompt ?? null}>
    {items.length === 0 && empty && <div className="empty-history">{t("No visible events in this history.")}</div>}
    <div className={`messages${emphasis.enabled ? " timeline-appearance-enabled" : ""}`} style={emphasisStyle}>
      {blocks.map(block => block.kind === "row"
        ? <TimelineGroupView key={`${sessionId}:${block.key}`} {...context} group={block.group} entryType={entryTypes.get(block.key) ?? "status"} />
        : <TimelineActivity key={`${sessionId}:${block.key}`} {...context} groups={block.groups} entryTypes={entryTypes}
          expanded={activityExpanded(block.groups, chosen, focused, shown)} live={block.key === liveActivity} images={emphasis.foldedImages} onToggle={toggleActivity} />)}
    </div>
    {shownTool && <ToolCallDialog item={shownTool} reader={readTool} outputs={toolOutputs} imageSource={toolImages} current={canInspect} onClose={closeToolWindow} />}
  </SystemPromptReaderContext.Provider>;
});

/**
 * Tells the panel that scrolls the timeline when the rows of a window with older history are put in the page:
 * `before` at the last moment before they are, `after` once they are there. The panel reads where the rows in
 * view are and puts them back there. Read any earlier (when the page arrives, before it is rendered), the place
 * would be the one of a reader who has scrolled since, and putting the rows back there would undo that scroll.
 */
class OlderRows extends Component<{ window: unknown; older: boolean; before: () => void; after: () => void }> {
  getSnapshotBeforeUpdate(previous: Readonly<{ window: unknown }>): boolean {
    if (previous.window === this.props.window || !this.props.older) return false;
    this.props.before();
    return true;
  }
  componentDidUpdate(_previous: unknown, _state: unknown, placed: boolean) { if (placed) this.props.after(); }
  render() { return null; }
}

function revisionOf(cursor: HistoryRequest["cursor"]): string | null {
  return cursor?.version === 2 ? JSON.stringify([cursor.length, cursor.lastWriteUtcTicks]) : null;
}

export function History({ sessionId, observing = true, onNotesChange, onUsageChange, onUnsentPrompt, onSettled, onBeforeOlder, onAfterOlder, onNewerOmitted, onNavigationReset,
  newestRequest, olderRequest, onNewestResult, live, read, readPluginEvents, readImages, readTool, readPrompt, toolOutputs, canInspect, outgoing = [], onAcknowledgeOutgoing, onBranch,
  reveal = null, onReveal, running }: {
  sessionId: string; onNotesChange: (markdown: string) => void; onSettled: () => void;
  /** Reports the newest persisted usage record text of the loaded window. */
  onUsageChange?: (text: string | null) => void;
  /** Reports the last prompt of the loaded window when its run never sent it (the conversation changed while it waited). */
  onUnsentPrompt?: (prompt: UnsentPrompt | null) => void;
  observing?: boolean;
  onBeforeOlder: () => void; onAfterOlder: () => void; live: SessionDisplayView | null;
  onNewerOmitted?: (value: boolean) => void;
  onNavigationReset?: (generation: number, explicitNewest: boolean) => void;
  newestRequest?: RefObject<NewestHistoryRequest | null>;
  /** Filled with what reads the turns before the window when the reader goes up near its top. */
  olderRequest?: RefObject<OlderHistoryRequest | null>;
  onNewestResult?: (result: NewestHistoryResult) => void;
  read: typeof workspace.historyTail;
  /** Reads the cards plugins derive from the finished turns of the loaded window, such as turn statistics. */
  readPluginEvents?: PluginEventsRead;
  /** Reads the images attached to the persisted user messages of this session. */
  readImages?: TimelineImageReader;
  /** Reads the whole record of a tool call of this session. */
  readTool?: ToolCallReader;
  /** Reads the system prompts of this session, whole, with the prompt each one replaced. */
  readPrompt?: SystemPromptReader;
  /** Follows what the running tool calls of this session write. */
  toolOutputs?: ToolOutputs;
  canInspect?: () => boolean;
  outgoing?: readonly OutgoingMessage[];
  onAcknowledgeOutgoing?: (keys: readonly string[]) => void;
  /** Opens the window of a fork or a rewind on the prompt of a turn, named by its journal offset; absent where the session cannot be changed. */
  onBranch?: (promptOffset: string, mode: BranchMode) => void;
  /** A message to show, named by its journal offset: older history is read until the window holds it. A new object asks again. */
  reveal?: RevealRequest | null;
  /** Says how a request to show a message ended, once the rows of the window are drawn. */
  onReveal?: (request: RevealRequest, outcome: RevealOutcome) => void;
  /** Whether a run of the session goes on, when it is known. */
  running?: boolean;
}) {
  const { t } = useShellLanguage();
  const bookmarkMarks = useContext(TimelineBookmarksContext);
  const [target, setTarget] = useState<HistoryTarget>(() =>
    ({ request: { sessionId, cursor: null }, explicitOlder: false, explicitNewest: false, generation: 0 }));
  const [state, setState] = useState<HistoryState>();
  const [window, setWindow] = useState<{ timeline: HistoryTimeline; generation: number; revision: string | null }>();
  const [candidate, setCandidate] = useState<typeof window>();
  const [sourceTarget, setSourceTarget] = useState<(HistorySourceTarget & { current: () => boolean }) | null>(null);
  const generation = useRef(0);
  const retainedStart = useRef<string | undefined>(undefined);
  const requestedNewest = useRef<number | null>(null);
  const readingRevision = useRef<{ generation: number; revision: string | null } | null>(null);
  const merged = useRef<HistoryRequest | null>(null);
  const published = useRef(0);
  // What going up already asked for: the turns before a window once, a new read after a failure once.
  const olderAsked = useRef<HistoryTimeline | undefined>(undefined);
  const olderRetried = useRef<HistoryState | undefined>(undefined);
  const failedAt = useRef(0);
  const request = target.request;
  const timeline = window?.timeline;
  function refreshNewest(keyboard: boolean): number {
    // A window that still reaches the journal end is kept, older turns included: the refresh only appends.
    // One that slid away from the end is replaced by the latest turn.
    const known = timeline && !timeline.newerOmitted && timeline.sessionId === sessionId ? timeline : undefined;
    if (keyboard && !known) retainedStart.current = undefined;
    const current = state?.request === request ? state : undefined;
    if (keyboard && target.explicitNewest && !historySettled(current,
      window?.generation === target.generation ? timeline : candidate?.generation === target.generation ? candidate.timeline : undefined)
      && current?.kind !== "error") {
      requestedNewest.current = target.generation;
      return target.generation;
    }
    const next = ++generation.current;
    requestedNewest.current = keyboard ? next : null;
    setSourceTarget(null);
    // A settled window names the stamp of its journal: while the journal keeps it, the host answers without rows.
    const stamp = known?.stamp && (known.next === null || known.limitReached || known.turnReached === true) ? known.stamp : null;
    setTarget({ request: stamp ? { sessionId, cursor: null, known: stamp } : { sessionId, cursor: null }, explicitOlder: false, explicitNewest: true, generation: next, known });
    return next;
  }
  useLayoutEffect(() => {
    if (!newestRequest) return;
    const invoke = () => refreshNewest(true);
    newestRequest.current = invoke;
    return () => { if (newestRequest.current === invoke) newestRequest.current = null; };
  });
  useLayoutEffect(() => { onNavigationReset?.(target.generation, target.explicitNewest); }, [target, onNavigationReset]);
  useEffect(() => {
    // Becoming visible again resumes the page chain; it never replays a merged page. A merged
    // page's cursor no longer continues the window, so replaying it would replace the loaded timeline.
    if (!observing || merged.current === request) return;
    const abort = new AbortController();
    // The revision a refresh keeps when the journal did not change.
    const keptRevision = window?.revision ?? null;
    void loadHistory(read, request, abort.signal, value => {
      if (value.kind === "ready") {
        const revision = revisionOf(value.page.next ?? value.request.cursor);
        const prior = readingRevision.current;
        if (value.request.cursor && prior?.generation === target.generation && prior.revision !== revision) {
          failedAt.current = performance.now();
          setState({ kind: "error", request, code: "history_changed" });
          return;
        }
        readingRevision.current = { generation: target.generation, revision };
      }
      if (value.kind === "error") failedAt.current = performance.now();
      setState(value);
      if (value.kind === "ready") {
        merged.current = value.request;
        // A read of everything is merged aside and shown every few pages: see the publication below.
        const update = (current: typeof window) => ({
          timeline: mergeHistoryPage(current?.timeline, value.request, value.page, target.explicitOlder, retainedStart.current,
            { known: target.known, olderFrom: target.olderFrom, all: target.all }),
          generation: target.generation, revision: value.page.status === "unchanged" ? keptRevision : revisionOf(value.page.next ?? value.request.cursor),
        });
        if (target.explicitNewest || target.all) setCandidate(update);
        else setWindow(update);
      }
    });
    return () => abort.abort();
  }, [read, target, observing]);
  useEffect(() => {
    // Earlier pages can contain older notes, not the current note. Never promote them to latest.
    if (!timeline?.newerOmitted) onNotesChange(latestNotes(timeline?.entries ?? []));
  }, [timeline, onNotesChange]);
  // Derived from the whole window: computed when the window changes, not on every page the panel reads.
  const usageText = useMemo(() => timeline && !timeline.newerOmitted ? latestUsageText(timeline.entries) : null, [timeline]);
  useEffect(() => { if (timeline && !timeline.newerOmitted) onUsageChange?.(usageText); }, [usageText, timeline?.sessionId, onUsageChange]);
  const unsent = useMemo(() => timeline && !timeline.newerOmitted ? unsentPrompt(timeline.entries) : null, [timeline]);
  useEffect(() => { if (timeline && !timeline.newerOmitted) onUnsentPrompt?.(unsent); }, [unsent?.runId, timeline?.sessionId, onUnsentPrompt]);
  // Plugin cards follow the window: they are read again when a turn ends or older history is loaded, and
  // the previous ones stay until the new answer arrives.
  const [pluginCards, setPluginCards] = useState<{ sessionId: string; events: readonly SessionPluginEvent[] }>();
  const pluginWindow = useMemo(() => timeline && !timeline.newerOmitted ? pluginEventsWindow(timeline.entries) : null, [timeline]);
  const pluginKey = pluginWindow?.key, pluginFrom = pluginWindow?.notBefore;
  useEffect(() => {
    if (!readPluginEvents || !observing || !pluginKey || !pluginFrom) return;
    const abort = new AbortController();
    let retry: ReturnType<typeof setTimeout> | undefined;
    const load = (attempt: number) => void readPluginEvents(pluginFrom, abort.signal).then(events => {
      if (abort.signal.aborted) return;
      if (events === "retry") { if (attempt < 2) retry = setTimeout(() => load(attempt + 1), 1000); }
      else if (events) setPluginCards({ sessionId, events });
    }, () => { /* The timeline stays as it is without the cards. */ });
    load(0);
    return () => { abort.abort(); clearTimeout(retry); };
  }, [readPluginEvents, observing, sessionId, pluginKey, pluginFrom]);
  const current = state?.request === request ? state : undefined;
  const liveRefresh = useRef<{ revision: string | undefined; ready: boolean; retry: boolean; refresh: () => void }>(null);
  liveRefresh.current = { revision: live?.revision,
    ready: (current?.kind === "ready" && window?.generation === target.generation && historySettled(current, window?.timeline)
      || historyCanRetry(current))
      && observing && !timeline?.newerOmitted && !sourceTarget && (canInspect?.() ?? true),
    retry: historyCanRetry(current),
    refresh: () => { refreshNewest(false); } };
  useEffect(() => {
    let refreshed: string | undefined;
    let retries = 0;
    // Live revisions invalidate persisted presentation. Coalesce journal reads,
    // never cancel an in-progress page chain or replace an explicitly older view.
    const timer = globalThis.window.setInterval(() => {
      const value = liveRefresh.current;
      if (!value?.ready || !value.revision) return;
      if (value.revision === refreshed) {
        if (!value.retry || retries++ >= 2) return;
      } else retries = 0;
      refreshed = value.revision;
      value.refresh();
    }, 500);
    return () => globalThis.window.clearInterval(timer);
  }, [sessionId]);
  const accumulation = target.explicitNewest || target.all ? candidate : window;
  useLayoutEffect(() => {
    if (accumulation?.timeline.turnReached && retainedStart.current === undefined) retainedStart.current = accumulation.timeline.entries[0]?.offset;
    // Older turns loaded on request stay part of the window that later refreshes keep.
    if (target.explicitOlder && accumulation?.generation === target.generation && !accumulation.timeline.newerOmitted
      && accumulation.timeline.entries.length) retainedStart.current = accumulation.timeline.entries[0].offset;
  }, [accumulation, target]);
  useEffect(() => {
    const accumulated = accumulation?.timeline;
    if (!observing || current?.kind !== "ready" || accumulation?.generation !== target.generation || !accumulated?.next ||
        accumulated.sessionId !== sessionId || accumulated.limitReached || accumulated.turnReached || accumulated.newerOmitted) return;
    const timer = globalThis.window.setTimeout(() => setTarget({ ...target, request: { sessionId, cursor: accumulated.next } }), 0);
    return () => globalThis.window.clearTimeout(timer);
  }, [current, accumulation, sessionId, target, observing]);
  useLayoutEffect(() => {
    if (target.explicitNewest && current?.kind === "ready" && candidate?.generation === target.generation &&
      historySettled(current, candidate.timeline) && window !== candidate) setWindow(candidate);
  }, [target, current, candidate, window]);
  // Everything is being loaded: the timeline takes what was read every few pages, and when the read ends
  // (the start of the journal, the largest window, an error). Each time the reader stays on the rows in view:
  // see OlderRows.
  useLayoutEffect(() => {
    if (!target.all || candidate?.generation !== target.generation || window === candidate) return;
    const read = candidate.timeline;
    if (current?.kind === "error" || current?.kind === "ready" && (historySettled(current, read)
      || read.entries.length - (window?.timeline.entries.length ?? 0) >= publicationRecords && performance.now() - published.current >= publicationInterval)) {
      published.current = performance.now();
      setWindow(candidate);
    }
  }, [target, current, candidate, window]);
  useLayoutEffect(() => {
    if (historySettled(current, window?.generation === target.generation ? timeline : undefined)) onSettled();
  }, [current, window, timeline, target, onSettled]);
  useLayoutEffect(() => {
    if (requestedNewest.current !== target.generation || !target.explicitNewest) return;
    if (current?.kind === "error") {
      requestedNewest.current = null;
      onNewestResult?.({ sessionId, generation: target.generation, revision: null, error: current.code });
    } else if (current?.kind === "ready" && window?.generation === target.generation && timeline &&
      !timeline.newerOmitted && historySettled(current, timeline)) {
      requestedNewest.current = null;
      onNewestResult?.({ sessionId, generation: target.generation, revision: window.revision, error: null });
    }
  }, [current, window, timeline, target, sessionId, onNewestResult]);
  useLayoutEffect(() => { onNewerOmitted?.(timeline?.newerOmitted === true); }, [timeline?.newerOmitted, onNewerOmitted]);
  function loadOlder(all: boolean) {
    if (!timeline?.next) return;
    // A read of everything continues from the window as it is.
    if (all) { setCandidate(window); published.current = 0; }
    setTarget({ request: { sessionId, cursor: timeline.next }, explicitOlder: true, explicitNewest: false, generation: ++generation.current,
      olderFrom: timeline.entries[0]?.offset, all });
  }
  // Stopping keeps what was read: it is shown, and the read ends as a chunk does, at a whole turn.
  function stopLoadingAll() {
    if (!target.all) return;
    if (candidate?.generation === target.generation && window !== candidate) setWindow(candidate);
    setTarget({ ...target, all: false });
  }
  const olderSettled = window?.generation === target.generation && historySettled(current, timeline);
  const loadingAll = !!target.all && current?.kind !== "error" && !olderSettled;
  // A chunk of older turns is being read: the reader went up, or a message is being gone to.
  const readingOlder = target.explicitOlder && !target.all && current?.kind !== "error" && !olderSettled;
  // Reading everything can start while a chunk is read, and goes on from what that read has brought in.
  const loadDisabled = current?.kind === "error" || current?.kind === "loading" && !readingOlder;
  // The rows of a long timeline are costly to build and to compare: they are rebuilt when the window or the
  // live view changes, and a row is rendered again only when its item did.
  const entries = timeline?.entries ?? noEntries;
  const reconciled = useMemo(() => reconcileTimeline(entries, live), [entries, live]);
  const sources = useMemo(() => new Map((timeline?.sources ?? []).map(source => [source.start, source])), [timeline?.sources]);
  // The prompts a session can be forked from or rewound to: worked out when the window changes, and only where it is offered.
  const promptKeys = useMemo(() => onBranch ? turnPromptKeys(entries) : undefined, [entries, !onBranch]);
  // The bookmarks of the timeline mark the rows of the window that is loaded.
  useLayoutEffect(() => { bookmarkMarks?.setEntries(entries); }, [bookmarkMarks, entries]);
  // A message to show that the window does not hold: older history is read, a chunk of turns at a time, until it
  // does. Each chunk settles before the next is asked for, so the reader sees the timeline grow towards the message.
  const revealed = useRef<RevealRequest | null>(null);
  const revealRetries = useRef<{ request: RevealRequest; count: number } | null>(null);
  const answerReveal = useRef(onReveal); answerReveal.current = onReveal;
  useEffect(() => {
    if (!reveal || revealed.current === reveal || !observing) return;
    const end = (outcome: RevealOutcome) => { revealed.current = reveal; answerReveal.current?.(reveal, outcome); };
    if (current?.kind === "error") {
      // The journal of a session that works changes while it is read: the newest history is read again, which
      // keeps what was loaded, and the read of older history goes on from there. A few times, not for ever.
      const retries = revealRetries.current?.request === reveal ? revealRetries.current.count : 0;
      if (!historyCanRetry(current) || retries >= 4) { end("failed"); return; }
      revealRetries.current = { request: reveal, count: retries + 1 };
      refreshNewest(false);
      return;
    }
    if (!timeline || current?.kind !== "ready" || window?.generation !== target.generation || !historySettled(current, timeline)) return;
    const step = revealStep(timeline.entries, timeline.next !== null, timeline.newerOmitted, reveal.offset);
    if (step === "older") loadOlder(false);
    else if (step === "newest") refreshNewest(false);
    else end(step);
  }, [reveal, current, window, timeline, target, observing]);
  // The reader goes up near the top of the window: the chunk of turns before it is read. Nothing is read
  // without that move, so a timeline that nobody scrolls stays as it is.
  function readOlder(): boolean {
    const step = olderHistoryStep({ observing, state: current, timeline, shown: window?.generation === target.generation,
      busy: !!reveal && revealed.current !== reveal, asked: olderAsked.current === timeline,
      retried: olderRetried.current === current, sinceFailure: performance.now() - failedAt.current });
    if (step === "older") { olderAsked.current = timeline; loadOlder(false); }
    else if (step === "refresh") { olderRetried.current = current; refreshNewest(false); }
    return step !== "none";
  }
  useLayoutEffect(() => {
    if (!olderRequest) return;
    olderRequest.current = readOlder;
    return () => { if (olderRequest.current === readOlder) olderRequest.current = null; };
  });
  const inspect = useRef(canInspect); inspect.current = canInspect;
  const inspectable = useCallback(() => inspect.current?.() ?? true, []);
  const openSource = useCallback((value: HistorySourceTarget) => {
    const captured = generation.current;
    setSourceTarget({ ...value, current: () => generation.current === captured && (inspect.current?.() ?? true) });
  }, []);
  return <section className="conversation history" aria-label={t("Session timeline")}
    data-window-ready={current?.kind === "ready" && window?.generation === target.generation && historySettled(current, timeline)}>
    {/* Older rows join the window: a chunk the reader went up for, a batch of everything, what a stopped read brought. */}
    <OlderRows window={window} older={target.explicitOlder} before={onBeforeOlder} after={onAfterOlder} />
    {!timeline && (!current || current.kind === "loading") && <p role="status">{t("Loading the latest persisted history.")}</p>}
    {current?.kind === "error" && <p role="alert" className="error-text">{t(historyMessage(current.code))}</p>}
    {current?.kind === "error" && !!timeline?.entries.length && <p role="status">{t("Showing previously loaded history. Refresh to update.")}</p>}
    {timeline?.tailOmitted && <div role="status" className="banner">{t("The malformed final journal record was omitted.")}</div>}
    {timeline?.next && <div className="load-more-bar">{loadingAll ? <>
      <span className="load-more-progress" role="status"><ActivitySpinner size={14} />{t("Loading previous messages… {count} loaded",
        { count: (candidate?.generation === target.generation ? candidate.timeline : timeline).entries.length })}</span>
      <Button size="small" icon={<AppIcon name="stop" size={14} />} text={t("Stop")} onClick={stopLoadingAll} />
    </> : <span className="load-more-slot" data-reading={readingOlder}>
      {/* The turns before are read as the reader goes up; the button reads them all at once. A read that
          lasts takes the place of the button, in the same slot. */}
      <Button size="small" className="load-more" icon={<AppIcon name="history" size={14} />} text={t("Load all messages")}
        disabled={loadDisabled} onClick={() => loadOlder(true)} />
      {readingOlder && <span className="load-more-progress load-more-reading" role="status"><ActivitySpinner size={14} />{t("Loading previous messages…")}</span>}
    </span>}</div>}
    <TimelineRows sessionId={sessionId} rows={reconciled} entries={entries} outgoing={outgoing} onAcknowledgeOutgoing={onAcknowledgeOutgoing}
      pluginEvents={pluginCards?.sessionId === sessionId && timeline && !timeline.newerOmitted ? pluginCards.events : undefined}
      echoes={!timeline?.newerOmitted} empty={reconciled.length === 0 && current?.kind === "ready"} revision={timeline?.revision ?? null} sources={sources}
      readImages={readImages} readTool={readTool} readPrompt={readPrompt} toolOutputs={toolOutputs} canInspect={inspectable} onOpenSource={openSource}
      promptKeys={promptKeys} onBranch={onBranch} running={running} shown={reveal?.offset ?? null} />
    {sourceTarget && sourceTarget.revision.sessionId === sessionId && <HistorySource key={JSON.stringify(sourceTarget)} target={sourceTarget}
      canInspect={() => sourceTarget.current() && (canInspect?.() ?? true)
        && JSON.stringify(sourceTarget.revision) === JSON.stringify(timeline?.revision)} onClose={() => setSourceTarget(null)} />}
  </section>;
}
