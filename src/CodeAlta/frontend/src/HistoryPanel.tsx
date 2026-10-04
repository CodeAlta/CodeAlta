import { matchesOutgoingText } from "./outgoingEcho";
import { memo, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type RefObject } from "react";
import { Button, ButtonGroup, Menu, MenuItem, PopoverNext } from "@blueprintjs/core";
import { workspace, type HistoryRequest, type SessionDisplayView, type SessionPluginEvent } from "#neoastra";
import { ActivitySpinner } from "./ActivitySpinner";
import { AppIcon } from "./AppIcon";
import { historyCanRetry, historyMessage, historySettled, loadHistory, mergeHistoryPage, type HistoryState, type HistoryTimeline } from "./history";
import { liveTextItem, liveToolItem } from "./liveTimeline";
import { orderTimelineRows, reconcileTimeline, type ReconciledRow } from "./reconcileTimeline";
import { groupTimelineTools } from "./toolGroups";
import { latestNotes, latestUsageText } from "./timeline";
import { pluginEventItems, pluginEventsWindow, type PluginEventsRead } from "./pluginEvents";
import { TimelineMessage } from "./TimelineMessage";
import { useShellLanguage } from "./shellLanguage";
import { HistorySource, type HistorySourceTarget } from "./HistorySource";
import type { OutgoingMessage } from "./sessionOperations";
import { inlineImageSource, type TimelineImageReader, type TimelineImageSource } from "./timelineImages";

// The production caller supplies readTimeline (workspace.historyTimeline). The injection seam lets the mounted
// browser fixture exercise this exact component with an isolated, revisioned test journal.
export type NewestHistoryResult = Readonly<{ sessionId: string; generation: number; revision: string | null;
  error: string | null }>;
export type NewestHistoryRequest = () => number;

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

/**
 * The rows of the timeline: the persisted and live records, the prompts being sent and the cards plugins
 * derive, in order, with consecutive tool calls grouped. It is rendered again only when one of these
 * changes, not on every page the panel reads, and each row only when its own item changed.
 */
const TimelineRows = memo(function TimelineRows({ sessionId, rows, entries, outgoing, onAcknowledgeOutgoing, pluginEvents, echoes: showEchoes, empty,
  revision, sources, readImages, canInspect, onOpenSource }: {
  sessionId: string; rows: readonly ReconciledRow[]; entries: HistoryTimeline["entries"];
  outgoing: readonly OutgoingMessage[]; onAcknowledgeOutgoing?: (keys: readonly string[]) => void;
  pluginEvents?: readonly SessionPluginEvent[];
  /** The window reaches the journal end: prompts being sent belong after it. */
  echoes: boolean;
  /** The history was read and holds nothing to show. It only changes while there are no rows. */
  empty: boolean;
  revision: HistoryTimeline["revision"] | null; sources: ReadonlyMap<string, NonNullable<HistoryTimeline["sources"]>[number]>;
  readImages?: TimelineImageReader; canInspect: () => boolean; onOpenSource: (target: HistorySourceTarget) => void;
}) {
  const { t } = useShellLanguage();
  const items = rows.slice();
  // Match each echo to at most one new source message. Never deduplicate repeated
  // prompts against old history or use this display match to settle a Send intent.
  let runs: Map<string, string | null> | undefined;
  const runOf = (offset: string) => (runs ??= new Map(entries.map(entry => [entry.offset, entry.runId]))).get(offset);
  const availableUsers = !outgoing.length ? [] : items.filter(row => row.source === "history" ? row.item.category === "user" : row.source === "liveText" && row.row.kind.toLowerCase() === "user");
  const acknowledged: string[] = [];
  const echoes = outgoing.filter(echo => {
    if (echo.state === "failed") return true;
    const index = availableUsers.findIndex(row => {
      const timestamp = row.source === "history" ? row.item.timestamp : row.row.timestamp;
      const text = row.source === "history" ? row.item.markdown : row.source === "liveText" ? row.row.text : null;
      const runId = row.source === "history" ? runOf(row.item.key) : row.row.runId;
      return matchesOutgoingText(text, echo.text, echo.imageCount, echo.runId, runId)
        && Date.parse(timestamp ?? "") >= Date.parse(echo.timestamp);
    });
    if (index < 0) return true;
    const [matched] = availableUsers.splice(index, 1);
    // The live copy of a prompt has no images: the card that holds them stays until the persisted message arrives.
    if (matched.source === "liveText" && echo.images?.length) { items.splice(items.indexOf(matched), 1); return true; }
    acknowledged.push(echo.key); return false;
  });
  useEffect(() => { if (acknowledged.length) onAcknowledgeOutgoing?.(acknowledged); }, [JSON.stringify(acknowledged), onAcknowledgeOutgoing]);
  const echoImages = new Map<string, TimelineImageSource>();
  for (const echo of showEchoes ? echoes : []) {
    const images = echo.images?.length ? echo.images : undefined;
    const item = liveTextItem({ contentId: echo.key, runId: echo.runId, kind: "User", text: echo.text || (echo.imageCount && !images ? t("{count} image attached", { count: echo.imageCount }) : ""),
      timestamp: echo.timestamp, sequence: null, isComplete: true, isTruncated: false, startedWithDelta: false });
    if (images) echoImages.set(`outgoing:${echo.key}`, inlineImageSource(`outgoing:${echo.key}`, images));
    items.push({ source: "history", key: `outgoing:${echo.key}`, item: { ...item,
      images: images?.map((image, index) => ({ index, title: image.title, mediaType: image.mediaType })),
      subtitle: echo.state === "sending" ? "Sending…" : echo.state === "failed" ? "Failed" : echo.state === "uncertain" ? "Pending" : null } });
  }
  if (pluginEvents) {
    for (const item of pluginEventItems(pluginEvents, entries[0]?.timestamp)) items.push({ source: "history", key: item.key, item });
  }
  orderTimelineRows(items);
  const groups = groupTimelineTools(items, entries);
  return <>
    {items.length === 0 && empty && <div className="empty-history">{t("No visible events in this history.")}</div>}
    <div className="messages">
      {groups.map(group => <div key={`${sessionId}:${group.key}`}
        className={group.tools ? "timeline-tool-group" : "timeline-single-row"} role={group.tools ? "group" : undefined}
        aria-label={group.tools ? t("Tools") : undefined}>
        {group.tools && <div className="timeline-tool-group-heading"><AppIcon name="tool" size={14} /><span>{t("Tool calls")}</span><span className="tool-group-counts">{t("{count} call(s)", { count: group.rows.length })}
          {(["completed", "failed", "started", "canceled"] as const).map(phase => {
            const count = group.rows.filter(row => (row.source === "history" ? row.item.toolPhase : row.source === "liveTool" ? row.row.phase.toLowerCase() : null) === phase).length;
            return count > 0 ? <span key={phase} data-phase={phase}> · {t(phase === "completed" ? "{count} done" : phase === "failed" ? "{count} failed" : phase === "started" ? "{count} running" : "{count} canceled", { count })}</span> : null;
          })}</span></div>}
        {group.rows.map(item => item.source === "history" ? <TimelineMessage key={`${sessionId}:${item.key}`} item={item.item} canInspect={canInspect} toolTile={group.tools}
        imageSource={!item.item.images ? undefined : echoImages.get(item.key) ?? (item.item.eventType !== "plugin" ? readImages?.(item.item.key) : undefined)}
        onOpenSource={onOpenSource} historySource={revision && sources.has(item.item.key) ? { revision, ...sources.get(item.item.key)! } : undefined} />
        : <TimelineMessage key={`${sessionId}:${item.key}`} item={item.source === "liveText" ? liveTextItem(item.row) : liveToolItem(item.row)} canInspect={canInspect} toolTile={group.tools} />)}
      </div>)}
    </div>
  </>;
});

function revisionOf(cursor: HistoryRequest["cursor"]): string | null {
  return cursor?.version === 2 ? JSON.stringify([cursor.length, cursor.lastWriteUtcTicks]) : null;
}

export function History({ sessionId, observing = true, onNotesChange, onUsageChange, onSettled, onBeforeOlder, onAfterOlder, onNewerOmitted, onNavigationReset,
  newestRequest, onNewestResult, live, read, readPluginEvents, readImages, canInspect, outgoing = [], onAcknowledgeOutgoing }: {
  sessionId: string; onNotesChange: (markdown: string) => void; onSettled: () => void;
  /** Reports the newest persisted usage record text of the loaded window. */
  onUsageChange?: (text: string | null) => void;
  observing?: boolean;
  onBeforeOlder: () => void; onAfterOlder: () => void; live: SessionDisplayView | null;
  onNewerOmitted?: (value: boolean) => void;
  onNavigationReset?: (generation: number, explicitNewest: boolean) => void;
  newestRequest?: RefObject<NewestHistoryRequest | null>;
  onNewestResult?: (result: NewestHistoryResult) => void;
  read: typeof workspace.historyTail;
  /** Reads the cards plugins derive from the finished turns of the loaded window, such as turn statistics. */
  readPluginEvents?: PluginEventsRead;
  /** Reads the images attached to the persisted user messages of this session. */
  readImages?: TimelineImageReader;
  canInspect?: () => boolean;
  outgoing?: readonly OutgoingMessage[];
  onAcknowledgeOutgoing?: (keys: readonly string[]) => void;
}) {
  const { t } = useShellLanguage();
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
  const beforeOlder = useRef(onBeforeOlder);
  beforeOlder.current = onBeforeOlder;
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
    setTarget({ request: { sessionId, cursor: null }, explicitOlder: false, explicitNewest: true, generation: next, known });
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
    void loadHistory(read, request, abort.signal, value => {
      if (value.kind === "ready") {
        const revision = revisionOf(value.page.next ?? value.request.cursor);
        const prior = readingRevision.current;
        if (value.request.cursor && prior?.generation === target.generation && prior.revision !== revision) {
          setState({ kind: "error", request, code: "history_changed" });
          return;
        }
        readingRevision.current = { generation: target.generation, revision };
      }
      setState(value);
      if (value.kind === "ready") {
        merged.current = value.request;
        // A read of everything is merged aside and shown every few pages: see the publication below.
        if (target.explicitOlder && !target.all) beforeOlder.current();
        const update = (current: typeof window) => ({
          timeline: mergeHistoryPage(current?.timeline, value.request, value.page, target.explicitOlder, retainedStart.current,
            { known: target.known, olderFrom: target.olderFrom, all: target.all }),
          generation: target.generation, revision: revisionOf(value.page.next ?? value.request.cursor),
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
  // (the start of the journal, the largest window, an error). Each time the reader stays on the rows in view.
  useLayoutEffect(() => {
    if (!target.all || candidate?.generation !== target.generation || window === candidate) return;
    const read = candidate.timeline;
    if (current?.kind === "error" || current?.kind === "ready" && (historySettled(current, read)
      || read.entries.length - (window?.timeline.entries.length ?? 0) >= publicationRecords && performance.now() - published.current >= publicationInterval)) {
      published.current = performance.now();
      beforeOlder.current();
      setWindow(candidate);
    }
  }, [target, current, candidate, window]);
  useLayoutEffect(() => {
    if (target.explicitOlder && current?.kind === "ready" && window?.generation === target.generation &&
      timeline?.sessionId === sessionId) onAfterOlder();
  }, [target, current, window, timeline, sessionId, onAfterOlder]);
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
    if (candidate?.generation === target.generation && window !== candidate) { beforeOlder.current(); setWindow(candidate); }
    setTarget({ ...target, all: false });
  }
  const loadDisabled = current?.kind === "loading" || current?.kind === "error";
  const loadingAll = !!target.all && current?.kind !== "error"
    && !(window?.generation === target.generation && historySettled(current, timeline));
  // The rows of a long timeline are costly to build and to compare: they are rebuilt when the window or the
  // live view changes, and a row is rendered again only when its item did.
  const entries = timeline?.entries ?? noEntries;
  const reconciled = useMemo(() => reconcileTimeline(entries, live), [entries, live]);
  const sources = useMemo(() => new Map((timeline?.sources ?? []).map(source => [source.start, source])), [timeline?.sources]);
  const inspect = useRef(canInspect); inspect.current = canInspect;
  const inspectable = useCallback(() => inspect.current?.() ?? true, []);
  const openSource = useCallback((value: HistorySourceTarget) => {
    const captured = generation.current;
    setSourceTarget({ ...value, current: () => generation.current === captured && (inspect.current?.() ?? true) });
  }, []);
  return <section className="conversation history" aria-label={t("Session timeline")}
    data-window-ready={current?.kind === "ready" && window?.generation === target.generation && historySettled(current, timeline)}>
    {!timeline && (!current || current.kind === "loading") && <p role="status">{t("Loading the latest persisted history.")}</p>}
    {current?.kind === "error" && <p role="alert" className="error-text">{t(historyMessage(current.code))}</p>}
    {current?.kind === "error" && !!timeline?.entries.length && <p role="status">{t("Showing previously loaded history. Refresh to update.")}</p>}
    {timeline?.tailOmitted && <div role="status" className="banner">{t("The malformed final journal record was omitted.")}</div>}
    {timeline?.next && <div className="load-more-bar">{loadingAll ? <>
      <span className="load-more-progress" role="status"><ActivitySpinner size={14} />{t("Loading previous messages… {count} loaded",
        { count: (candidate?.generation === target.generation ? candidate.timeline : timeline).entries.length })}</span>
      <Button size="small" icon={<AppIcon name="stop" size={14} />} text={t("Stop")} onClick={stopLoadingAll} />
    </> : <ButtonGroup className="load-more-group">
      {/* One slot, like Send: the button loads a chunk, the caret offers the whole history as well. */}
      <Button intent="primary" className="load-more" icon={<AppIcon name="history" size={14} />} text={t("Load previous messages")}
        disabled={loadDisabled} onClick={() => loadOlder(false)} />
      <PopoverNext placement="bottom-end" disabled={loadDisabled} content={<Menu aria-label={t("Load options")}>
        <MenuItem icon={<AppIcon name="history" size={16} />} text={t("Load previous messages")} onClick={() => loadOlder(false)} />
        <MenuItem icon={<AppIcon name="browse" size={16} />} text={t("Load all previous messages")} onClick={() => loadOlder(true)} />
      </Menu>}>
        <Button intent="primary" className="load-more-options" disabled={loadDisabled} aria-label={t("Load options")} title={t("Load options")}
          icon={<AppIcon name="chevronDown" size={14} />} />
      </PopoverNext>
    </ButtonGroup>}</div>}
    <TimelineRows sessionId={sessionId} rows={reconciled} entries={entries} outgoing={outgoing} onAcknowledgeOutgoing={onAcknowledgeOutgoing}
      pluginEvents={pluginCards?.sessionId === sessionId && timeline && !timeline.newerOmitted ? pluginCards.events : undefined}
      echoes={!timeline?.newerOmitted} empty={reconciled.length === 0 && current?.kind === "ready"} revision={timeline?.revision ?? null} sources={sources}
      readImages={readImages} canInspect={inspectable} onOpenSource={openSource} />
    {sourceTarget && sourceTarget.revision.sessionId === sessionId && <HistorySource key={JSON.stringify(sourceTarget)} target={sourceTarget}
      canInspect={() => sourceTarget.current() && (canInspect?.() ?? true)
        && JSON.stringify(sourceTarget.revision) === JSON.stringify(timeline?.revision)} onClose={() => setSourceTarget(null)} />}
  </section>;
}
