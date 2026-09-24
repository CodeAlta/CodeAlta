import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { workspace, type HistoryRequest, type SessionDisplayView } from "#neoastra";
import { AppIcon } from "./AppIcon";
import { historyMessage, historySettled, loadHistory, mergeHistoryPage, type HistoryState, type HistoryTimeline } from "./history";
import { LiveTextMessage, LiveToolMessage } from "./LiveSessionPanel";
import { reconcileTimeline } from "./reconcileTimeline";
import { latestNotes } from "./timeline";
import { TimelineMessage } from "./TimelineMessage";

// The production caller supplies workspace.historyTail. The injection seam lets the mounted
// browser fixture exercise this exact component with an isolated, revisioned test journal.
export function History({ sessionId, onNotesChange, onSettled, onBeforeOlder, onAfterOlder, live, read }: {
  sessionId: string; onNotesChange: (markdown: string) => void; onSettled: () => void;
  onBeforeOlder: () => void; onAfterOlder: () => void; live: SessionDisplayView | null;
  read: typeof workspace.historyTail;
}) {
  const [target, setTarget] = useState<{ request: HistoryRequest; explicitOlder: boolean }>(() =>
    ({ request: { sessionId, cursor: null }, explicitOlder: false }));
  const [state, setState] = useState<HistoryState>();
  const [timeline, setTimeline] = useState<HistoryTimeline>();
  const beforeOlder = useRef(onBeforeOlder);
  beforeOlder.current = onBeforeOlder;
  const request = target.request;
  useEffect(() => {
    const abort = new AbortController();
    void loadHistory(read, request, abort.signal, value => {
      setState(value);
      if (value.kind === "ready") {
        if (target.explicitOlder) beforeOlder.current();
        setTimeline(current => mergeHistoryPage(current, value.request, value.page, target.explicitOlder));
      } else if (value.kind === "error" && (value.code === "history_changed" || value.code === "invalid_cursor")) {
        setTimeline(undefined);
      }
    });
    return () => abort.abort();
  }, [read, target]);
  useEffect(() => {
    // Earlier pages can contain older notes, not the current note. Never promote them to latest.
    if (!timeline?.newerOmitted) onNotesChange(latestNotes(timeline?.entries ?? []));
  }, [timeline, onNotesChange]);
  const current = state?.request === request ? state : undefined;
  useEffect(() => {
    if (current?.kind !== "ready" || !timeline?.next || timeline.sessionId !== sessionId ||
        timeline.entries.length >= 1000 || target.explicitOlder) return;
    const timer = window.setTimeout(() => setTarget({ request: { sessionId, cursor: timeline.next }, explicitOlder: false }), 0);
    return () => window.clearTimeout(timer);
  }, [current, timeline, sessionId, target]);
  useLayoutEffect(() => {
    if (target.explicitOlder && current?.kind === "ready" && timeline?.sessionId === sessionId) onAfterOlder();
  }, [target, current, timeline, sessionId, onAfterOlder]);
  useLayoutEffect(() => {
    if (historySettled(current, timeline)) onSettled();
  }, [current, timeline, onSettled]);
  const items = reconcileTimeline(timeline?.entries ?? [], live);
  return <section className="conversation history" aria-labelledby="history-heading">
    <div className="section-heading"><div><span className="eyebrow">Journal + recent live window</span><h2 id="history-heading">Session timeline</h2></div><button type="button" className="quiet-button icon-label-button" onClick={() => {
      setTimeline(undefined); setTarget({ request: { sessionId, cursor: null }, explicitOlder: false });
    }}><AppIcon name="refresh" size={14} />Refresh newest history</button></div>
    {(!current || current.kind === "loading") && <p role="status">Loading the latest persisted history…</p>}
    {current?.kind === "error" && <p role="alert" className="error-text">{historyMessage(current.code)}</p>}
    {timeline?.tailOmitted && <div role="status" className="banner">The malformed final journal record was omitted.</div>}
    {timeline?.limitReached && <div role="status" className="banner">Showing at most 1,000 journal events. Loading older history replaces newer visible events; refresh to return to the latest.</div>}
    {timeline?.newerOmitted && <div role="status" className="banner">Newer journal events are no longer in this older window. Refresh newest history to return to the latest turn.</div>}
    {timeline?.next && <button type="button" className="load-more" disabled={current?.kind === "loading" || current?.kind === "error"}
      onClick={() => setTarget({ request: { sessionId, cursor: timeline.next }, explicitOlder: true })}>
      <AppIcon name="history" size={14} />Load older history{timeline.entries.length === 1000 ? " (replace newest visible events)" : ""}</button>}
    {items.length === 0 && current?.kind === "ready" && <div className="empty-history">No visible events in this history.</div>}
    <div className="messages">
      {items.map(item => item.source === "history" ? <TimelineMessage key={item.key} item={item.item} />
        : item.source === "liveText" ? <LiveTextMessage key={item.key} row={item.row} />
        : <LiveToolMessage key={item.key} row={item.row} />)}
    </div>
    {items.some(item => item.source !== "history") && <p className="detail live-order-note">Live rows are recent retained updates, not timestamped journal events; text/tool ordering and missing intervening activity are unknown.</p>}
  </section>;
}
