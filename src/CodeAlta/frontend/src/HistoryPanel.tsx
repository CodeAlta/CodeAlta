import { useEffect, useLayoutEffect, useRef, useState, type RefObject } from "react";
import { workspace, type HistoryRequest, type SessionDisplayView } from "#neoastra";
import { AppIcon } from "./AppIcon";
import { historyMessage, historySettled, loadHistory, mergeHistoryPage, type HistoryState, type HistoryTimeline } from "./history";
import { LiveTextMessage, LiveToolMessage } from "./LiveSessionPanel";
import { reconcileTimeline } from "./reconcileTimeline";
import { latestNotes } from "./timeline";
import { TimelineMessage } from "./TimelineMessage";
import { useShellLanguage } from "./shellLanguage";

// The production caller supplies workspace.historyTail. The injection seam lets the mounted
// browser fixture exercise this exact component with an isolated, revisioned test journal.
export type NewestHistoryResult = Readonly<{ sessionId: string; generation: number; revision: string | null;
  error: string | null }>;
export type NewestHistoryRequest = () => number;

type HistoryTarget = { request: HistoryRequest; explicitOlder: boolean; explicitNewest: boolean; generation: number };

function revisionOf(cursor: HistoryRequest["cursor"]): string | null {
  return cursor?.version === 2 ? JSON.stringify([cursor.length, cursor.lastWriteUtcTicks]) : null;
}

export function History({ sessionId, onNotesChange, onSettled, onBeforeOlder, onAfterOlder, onNewerOmitted, onNavigationReset,
  newestRequest, onNewestResult, live, read, canInspect }: {
  sessionId: string; onNotesChange: (markdown: string) => void; onSettled: () => void;
  onBeforeOlder: () => void; onAfterOlder: () => void; live: SessionDisplayView | null;
  onNewerOmitted?: (value: boolean) => void;
  onNavigationReset?: (generation: number, explicitNewest: boolean) => void;
  newestRequest?: RefObject<NewestHistoryRequest | null>;
  onNewestResult?: (result: NewestHistoryResult) => void;
  read: typeof workspace.historyTail;
  canInspect?: () => boolean;
}) {
  const { t } = useShellLanguage();
  const [target, setTarget] = useState<HistoryTarget>(() =>
    ({ request: { sessionId, cursor: null }, explicitOlder: false, explicitNewest: false, generation: 0 }));
  const [state, setState] = useState<HistoryState>();
  const [window, setWindow] = useState<{ timeline: HistoryTimeline; generation: number; revision: string | null }>();
  const [candidate, setCandidate] = useState<typeof window>();
  const generation = useRef(0);
  const requestedNewest = useRef<number | null>(null);
  const readingRevision = useRef<{ generation: number; revision: string | null } | null>(null);
  const beforeOlder = useRef(onBeforeOlder);
  beforeOlder.current = onBeforeOlder;
  const request = target.request;
  const timeline = window?.timeline;
  function refreshNewest(keyboard: boolean): number {
    const current = state?.request === request ? state : undefined;
    if (keyboard && target.explicitNewest && !historySettled(current,
      window?.generation === target.generation ? timeline : candidate?.generation === target.generation ? candidate.timeline : undefined)
      && current?.kind !== "error") {
      requestedNewest.current = target.generation;
      return target.generation;
    }
    const next = ++generation.current;
    requestedNewest.current = keyboard ? next : null;
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
          if (!target.explicitNewest) setWindow(undefined);
          return;
        }
        readingRevision.current = { generation: target.generation, revision };
      }
      setState(value);
      if (value.kind === "ready") {
        if (target.explicitOlder) beforeOlder.current();
        const update = (current: typeof window) => ({
          timeline: mergeHistoryPage(current?.timeline, value.request, value.page, target.explicitOlder),
          generation: target.generation, revision: revisionOf(value.page.next ?? value.request.cursor),
        });
        if (target.explicitNewest) setCandidate(update);
        else setWindow(update);
      } else if (value.kind === "error" && !target.explicitNewest &&
        (value.code === "history_changed" || value.code === "invalid_cursor")) {
        setWindow(undefined);
      }
    });
    return () => abort.abort();
  }, [read, target]);
  useEffect(() => {
    // Earlier pages can contain older notes, not the current note. Never promote them to latest.
    if (!timeline?.newerOmitted) onNotesChange(latestNotes(timeline?.entries ?? []));
  }, [timeline, onNotesChange]);
  const current = state?.request === request ? state : undefined;
  const accumulation = target.explicitNewest ? candidate : window;
  useEffect(() => {
    const accumulated = accumulation?.timeline;
    if (current?.kind !== "ready" || accumulation?.generation !== target.generation || !accumulated?.next ||
        accumulated.sessionId !== sessionId || accumulated.entries.length >= 1000 || target.explicitOlder) return;
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
  return <section className="conversation history" aria-labelledby="history-heading"
    data-window-ready={current?.kind === "ready" && window?.generation === target.generation && historySettled(current, timeline)}>
    <div className="section-heading"><div><span className="eyebrow">{t("Journal + recent live window")}</span><h2 id="history-heading">{t("Session timeline")}</h2></div><button type="button" className="quiet-button icon-label-button" onClick={() => {
      refreshNewest(false);
    }}><AppIcon name="refresh" size={14} />{t("Refresh newest history")}</button></div>
    {(!current || current.kind === "loading") && <p role="status">{t("Loading the latest persisted history.")}</p>}
    {current?.kind === "error" && <p role="alert" className="error-text">{t(historyMessage(current.code))}</p>}
    {timeline?.tailOmitted && <div role="status" className="banner">{t("The malformed final journal record was omitted.")}</div>}
    {timeline?.limitReached && <div role="status" className="banner">{t("Showing at most 1,000 journal events. Loading older history replaces newer visible events; refresh to return to the latest.")}</div>}
    {timeline?.newerOmitted && <div role="status" className="banner">{t("Newer journal events are no longer in this older window. Refresh newest history to return to the latest turn.")}</div>}
    {timeline?.next && <button type="button" className="load-more" disabled={current?.kind === "loading" || current?.kind === "error"}
      onClick={() => setTarget({ request: { sessionId, cursor: timeline.next }, explicitOlder: true,
        explicitNewest: false, generation: ++generation.current })}>
      <AppIcon name="history" size={14} />{t("Load older history")}{timeline.entries.length === 1000 ? t(" (replace newest visible events)") : ""}</button>}
    {items.length === 0 && current?.kind === "ready" && <div className="empty-history">{t("No visible events in this history.")}</div>}
    <div className="messages">
      {items.map(item => item.source === "history" ? <TimelineMessage key={`${sessionId}:${window?.revision ?? "unversioned"}:${item.key}`} item={item.item} canInspect={canInspect} />
        : item.source === "liveText" ? <LiveTextMessage key={item.key} row={item.row} />
        : <LiveToolMessage key={item.key} row={item.row} />)}
    </div>
    {items.some(item => item.source !== "history") && <p className="detail live-order-note">{t("Live rows are recent retained updates, not timestamped journal events; text/tool ordering and missing intervening activity are unknown.")}</p>}
  </section>;
}
