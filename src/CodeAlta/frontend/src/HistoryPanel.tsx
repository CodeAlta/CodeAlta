import { useEffect, useLayoutEffect, useRef, useState, type RefObject } from "react";
import { workspace, type HistoryRequest, type SessionDisplayView } from "#neoastra";
import { AppIcon } from "./AppIcon";
import { historyMessage, historySettled, loadHistory, mergeHistoryPage, type HistoryState, type HistoryTimeline } from "./history";
import { liveTextItem, liveToolItem } from "./liveTimeline";
import { reconcileTimeline } from "./reconcileTimeline";
import { groupTimelineTools } from "./toolGroups";
import { latestNotes } from "./timeline";
import { TimelineMessage } from "./TimelineMessage";
import { useShellLanguage } from "./shellLanguage";
import { HistorySource, type HistorySourceTarget } from "./HistorySource";
import type { OutgoingMessage } from "./sessionOperations";

// The production caller supplies readTimeline (workspace.historyTimeline). The injection seam lets the mounted
// browser fixture exercise this exact component with an isolated, revisioned test journal.
export type NewestHistoryResult = Readonly<{ sessionId: string; generation: number; revision: string | null;
  error: string | null }>;
export type NewestHistoryRequest = () => number;

type HistoryTarget = { request: HistoryRequest; explicitOlder: boolean; explicitNewest: boolean; generation: number };

function revisionOf(cursor: HistoryRequest["cursor"]): string | null {
  return cursor?.version === 2 ? JSON.stringify([cursor.length, cursor.lastWriteUtcTicks]) : null;
}

export function History({ sessionId, onNotesChange, onSettled, onBeforeOlder, onAfterOlder, onNewerOmitted, onNavigationReset,
  newestRequest, onNewestResult, live, read, canInspect, outgoing = [], onAcknowledgeOutgoing, messageCount }: {
  sessionId: string; onNotesChange: (markdown: string) => void; onSettled: () => void;
  onBeforeOlder: () => void; onAfterOlder: () => void; live: SessionDisplayView | null;
  onNewerOmitted?: (value: boolean) => void;
  onNavigationReset?: (generation: number, explicitNewest: boolean) => void;
  newestRequest?: RefObject<NewestHistoryRequest | null>;
  onNewestResult?: (result: NewestHistoryResult) => void;
  read: typeof workspace.historyTail;
  canInspect?: () => boolean;
  outgoing?: readonly OutgoingMessage[];
  onAcknowledgeOutgoing?: (keys: readonly string[]) => void;
  messageCount?: number | null;
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
  const beforeOlder = useRef(onBeforeOlder);
  beforeOlder.current = onBeforeOlder;
  const request = target.request;
  const timeline = window?.timeline;
  function refreshNewest(keyboard: boolean): number {
    if (keyboard) retainedStart.current = undefined;
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
    setTarget({ request: { sessionId, cursor: null }, explicitOlder: false, explicitNewest: true, generation: next });
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
        if (target.explicitOlder) beforeOlder.current();
        const update = (current: typeof window) => ({
          timeline: mergeHistoryPage(current?.timeline, value.request, value.page, target.explicitOlder, retainedStart.current),
          generation: target.generation, revision: revisionOf(value.page.next ?? value.request.cursor),
        });
        if (target.explicitNewest) setCandidate(update);
        else setWindow(update);
      }
    });
    return () => abort.abort();
  }, [read, target]);
  useEffect(() => {
    // Earlier pages can contain older notes, not the current note. Never promote them to latest.
    if (!timeline?.newerOmitted) onNotesChange(latestNotes(timeline?.entries ?? []));
  }, [timeline, onNotesChange]);
  const current = state?.request === request ? state : undefined;
  const liveRefresh = useRef<{ revision: string | undefined; ready: boolean; retry: boolean; refresh: () => void }>(null);
  liveRefresh.current = { revision: live?.revision,
    ready: (current?.kind === "ready" && window?.generation === target.generation && historySettled(current, window?.timeline)
      || current?.kind === "error" && current.code === "history_changed")
      && !target.explicitOlder && !timeline?.newerOmitted && !sourceTarget && (canInspect?.() ?? true),
    retry: current?.kind === "error" && current.code === "history_changed",
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
  const accumulation = target.explicitNewest ? candidate : window;
  useLayoutEffect(() => {
    if (accumulation?.timeline.turnReached && retainedStart.current === undefined) retainedStart.current = accumulation.timeline.entries[0]?.offset;
  }, [accumulation]);
  useEffect(() => {
    const accumulated = accumulation?.timeline;
    if (current?.kind !== "ready" || accumulation?.generation !== target.generation || !accumulated?.next ||
        accumulated.sessionId !== sessionId || accumulated.limitReached || accumulated.turnReached || target.explicitOlder) return;
    const timer = globalThis.window.setTimeout(() => setTarget({ request: { sessionId, cursor: accumulated.next },
      explicitOlder: false, explicitNewest: target.explicitNewest, generation: target.generation }), 0);
    return () => globalThis.window.clearTimeout(timer);
  }, [current, accumulation, sessionId, target]);
  useLayoutEffect(() => {
    if (target.explicitNewest && current?.kind === "ready" && candidate?.generation === target.generation &&
      historySettled(current, candidate.timeline) && window !== candidate) setWindow(candidate);
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
  const items = reconcileTimeline(timeline?.entries ?? [], live);
  // Match each echo to at most one new source message. Never deduplicate repeated
  // prompts against old history or use this display match to settle a Send intent.
  const availableUsers = items.filter(row => row.source === "history" ? row.item.category === "user" : row.source === "liveText" && row.row.kind.toLowerCase() === "user");
  const acknowledged: string[] = [];
  const echoes = outgoing.filter(echo => {
    if (echo.state === "failed") return true;
    const index = availableUsers.findIndex(row => {
      const timestamp = row.source === "history" ? row.item.timestamp : row.row.timestamp;
      const text = row.source === "history" ? row.item.markdown : row.source === "liveText" ? row.row.text : null;
      const runId = row.source === "history" ? timeline?.entries.find(entry => entry.offset === row.item.key)?.runId : row.row.runId;
      return text === echo.text && Date.parse(timestamp ?? "") >= Date.parse(echo.timestamp)
        && (!echo.runId || runId === echo.runId);
    });
    if (index < 0) return true;
    availableUsers.splice(index, 1); acknowledged.push(echo.key); return false;
  });
  useEffect(() => { if (acknowledged.length) onAcknowledgeOutgoing?.(acknowledged); }, [JSON.stringify(acknowledged), onAcknowledgeOutgoing]);
  for (const echo of timeline?.newerOmitted || target.explicitOlder ? [] : echoes) {
    const item = liveTextItem({ contentId: echo.key, runId: echo.runId, kind: "User", text: echo.text || (echo.imageCount ? t("{count} image attached", { count: echo.imageCount }) : ""),
      timestamp: echo.timestamp, sequence: null, isComplete: true, isTruncated: false, startedWithDelta: false });
    items.push({ source: "history", key: `outgoing:${echo.key}`, item: { ...item,
      subtitle: echo.state === "sending" ? "Sending…" : echo.state === "failed" ? "Failed" : echo.state === "uncertain" ? "Pending" : null } });
  }
  items.sort((a, b) => Date.parse(a.source === "history" ? a.item.timestamp : a.row.timestamp ?? "") - Date.parse(b.source === "history" ? b.item.timestamp : b.row.timestamp ?? ""));
  const olderCount = messageCount == null ? null : Math.max(0, messageCount - items.filter(row => row.source === "history" && !row.key.startsWith("outgoing:")).length);
  return <section className="conversation history" aria-label={t("Session timeline")}
    data-window-ready={current?.kind === "ready" && window?.generation === target.generation && historySettled(current, timeline)}>
    {!timeline && (!current || current.kind === "loading") && <p role="status">{t("Loading the latest persisted history.")}</p>}
    {current?.kind === "error" && <p role="alert" className="error-text">{t(historyMessage(current.code))}</p>}
    {current?.kind === "error" && !!timeline?.entries.length && <p role="status">{t("Previously loaded history is retained; the window is partial and may be from an older revision. Refresh explicitly to replace it.")}</p>}
    {timeline?.tailOmitted && <div role="status" className="banner">{t("The malformed final journal record was omitted.")}</div>}
    {timeline?.next && <button type="button" className="load-more" disabled={current?.kind === "loading" || current?.kind === "error"}
      onClick={() => setTarget({ request: { sessionId, cursor: timeline.next }, explicitOlder: true,
        explicitNewest: false, generation: ++generation.current })}>
      <AppIcon name="history" size={14} />{olderCount ? t("Load {count} previous messages", { count: olderCount }) : t("Load previous messages")}</button>}
    {items.length === 0 && current?.kind === "ready" && <div className="empty-history">{t("No visible events in this history.")}</div>}
    <div className="messages">
      {groupTimelineTools(items, timeline?.entries ?? []).map(group => <div key={`${sessionId}:${group.key}`}
        className={group.tools ? "timeline-tool-group" : "timeline-single-row"} role={group.tools ? "group" : undefined}
        aria-label={group.tools ? t("Tools") : undefined}>
        {group.tools && <div className="timeline-tool-group-heading"><AppIcon name="tool" size={14} /><span>{t("Tool calls")}</span><span className="tool-group-counts">{t("{count} call(s)", { count: group.rows.length })}
          {(["completed", "failed", "started", "canceled"] as const).map(phase => {
            const count = group.rows.filter(row => (row.source === "history" ? row.item.toolPhase : row.source === "liveTool" ? row.row.phase.toLowerCase() : null) === phase).length;
            return count > 0 ? <span key={phase} data-phase={phase}> · {t(phase === "completed" ? "{count} done" : phase === "failed" ? "{count} failed" : phase === "started" ? "{count} running" : "{count} canceled", { count })}</span> : null;
          })}</span></div>}
        {group.rows.map(item => item.source === "history" ? <TimelineMessage key={`${sessionId}:${item.key}`} item={item.item} canInspect={canInspect} toolTile={group.tools}
        onOpenSource={value => {
          const captured = generation.current;
          setSourceTarget({ ...value, current: () => generation.current === captured && (canInspect?.() ?? true) });
        }} historySource={timeline?.revision && timeline.sources?.find(source => source.start === item.item.key)
          ? { revision: timeline.revision, ...timeline.sources.find(source => source.start === item.item.key)! } : undefined} />
        : <TimelineMessage key={`${sessionId}:${item.key}`} item={item.source === "liveText" ? liveTextItem(item.row) : liveToolItem(item.row)} canInspect={canInspect} toolTile={group.tools} />)}
      </div>)}
    </div>
    {sourceTarget && sourceTarget.revision.sessionId === sessionId && <HistorySource key={JSON.stringify(sourceTarget)} target={sourceTarget}
      canInspect={() => sourceTarget.current() && (canInspect?.() ?? true)
        && JSON.stringify(sourceTarget.revision) === JSON.stringify(timeline?.revision)} onClose={() => setSourceTarget(null)} />}
  </section>;
}
