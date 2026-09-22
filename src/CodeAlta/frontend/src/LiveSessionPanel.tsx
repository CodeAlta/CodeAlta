import { useEffect, useRef, useState, useSyncExternalStore } from "react";
import { createSessionDisplayStore, displayRowKey, displayToolActivityKey } from "./sessionDisplay";
import type { createMutationCapability } from "./sessionOperations";
import { MarkdownContent } from "./MarkdownContent";
import { writeMarkdown } from "./timeline";

export function LiveSessionPanel({ store, hostEpoch, sessionId, capability }: {
  store: ReturnType<typeof createSessionDisplayStore>; hostEpoch: string; sessionId: string;
  capability: ReturnType<typeof createMutationCapability>;
}) {
  const observed = useSyncExternalStore(store.subscribe, store.getSnapshot);
  const canMutate = useSyncExternalStore(capability.subscribe, capability.canMutate);
  const scope = useRef<{ hostEpoch: string; sessionId: string; selection: ReturnType<typeof store.select> } | null>(null);
  useEffect(() => {
    const owned = { hostEpoch, sessionId, selection: store.select(hostEpoch, sessionId, capability.observe) };
    scope.current = owned;
    return () => { owned.selection.detach(); if (scope.current === owned) scope.current = null; };
  }, [store, hostEpoch, sessionId, capability]);
  // Never flash the previous selection during the render preceding effect cleanup/admission.
  const state = observed.hostEpoch === hostEpoch && observed.sessionId === sessionId ? observed : null;
  const snapshot = state?.snapshot;
  const session = snapshot?.session;
  return <section className="live-display" aria-label="Selected session live display">
    <div className="section-heading"><div><span className="eyebrow">Now</span><h3>Live activity</h3></div><span className={`status-pill live-${state?.kind ?? "loading"}`}>{state?.kind ?? "loading"}</span></div>
    {state?.code === "stale_epoch" && <p role="alert">The host has changed. Reload the Desktop UI before continuing; reconnecting with this old host identity will not work.</p>}
    {state?.cleanupBlocked && <p role="alert">Previous observation cleanup failed. Its owner is retained; no successor can open here. Reconnect cannot prove cleanup or recover effects.</p>}
    {(state?.code || state?.cleanupBlocked) && <button type="button" disabled={!canMutate || state?.code === "stale_epoch" || state?.cleanupBlocked} onClick={() => {
      const owned = scope.current;
      if (!owned || owned.hostEpoch !== hostEpoch || owned.sessionId !== sessionId || !capability.canMutate() || store.getSnapshot().cleanupBlocked) return;
      owned.selection = store.select(hostEpoch, sessionId, capability.observe);
    }}>Reconnect live activity</button>}
    {snapshot && <>
      <p className="detail">Projection {snapshot.projectionEpoch} · revision {snapshot.revision}</p>
      {snapshot.hasGap && <p role="status">Intermediate updates were coalesced. This replacement is the latest retained window, not recovered history.</p>}
      {snapshot.isClosed && <p role="status">Runtime display closed. No further updates will arrive on this observation.</p>}
      {(snapshot.evictedSessions !== "0" || snapshot.omittedSessionEvents !== "0") && <p className="detail">Global coverage: {snapshot.evictedSessions} session windows evicted; {snapshot.omittedSessionEvents} publications with omitted session identity.</p>}
      {!session && <p role="status">No retained live state for this session: it may not have published yet, or its window was evicted. This does not mean idle or completed.</p>}
      {session && <>
        <details className="live-metadata"><summary>Runtime details</summary><dl>
          <dt>Latest published lifecycle</dt><dd>{session.lifecycle?.kind ?? "Not observed"}{session.lifecycle?.runId ? ` · run ${session.lifecycle.runId}` : ""}</dd>
          <dt>Queue count</dt><dd>{session.queuedPromptCount ?? "Not observed"}</dd>
          <dt>Host status</dt><dd>{session.statusKind ?? "Not observed"}{session.statusMessage ? ` · ${session.statusMessage}` : ""}</dd>
          <dt>Provider / configuration key</dt><dd>{session.configuration?.providerId ?? "Not observed"} / {session.configuration?.providerKey ?? "Not observed"}</dd>
          <dt>Model / reasoning</dt><dd>{session.configuration?.modelId ?? "Not observed"} / {session.configuration?.reasoningEffort ?? "Not observed"}</dd>
          <dt>Agent prompt</dt><dd>{session.configuration?.agentPromptId ?? "Not observed"}</dd>
        </dl></details>
        {session.lifecycle?.message && <p>{session.lifecycle.message}</p>}
        {(session.metadataTruncated || session.transportTruncated) && <p className="detail">Some status/configuration labels were shortened.</p>}
        <p className="detail">Retained-window omissions: {session.evictedTextItems} text items evicted; {session.unsupportedEvents} unsupported publications. Other payload details are not projected.</p>
        {session.toolActivities.length > 0 && <><h4>Tools</h4><ol className="history-records tool-activity-list">{session.toolActivities.map(activity => <li key={displayToolActivityKey(session.sessionId, activity)}>
          <strong>{activity.name ?? "Unnamed tool"}</strong>{activity.isNameTruncated && <span className="detail"> · name prefix truncated</span>}
          <p>Reported {activity.phase} · provider {activity.providerId} · run {activity.runId ?? "not supplied"} · activity {activity.activityId}</p>
        </li>)}</ol></>}
        {session.text.length === 0 && <p className="muted-text">Waiting for live output…</p>}
        <div className="messages live-messages">{session.text.map(row => <LiveTextMessage key={displayRowKey(session.sessionId, row)} row={row} />)}</div>
        <details className="live-metadata"><summary>Live-window coverage</summary>
          <p className="detail">Only this selected session is observed. This is not persisted history, a complete transcript, or a tool-results/usage/interaction view. Closing or reconnecting does not stop a run.</p>
          <p className="detail">A same-host reload obtains retained partial values only. Restart restores no authority. {session.evictedToolActivities} tool identities were evicted.</p>
        </details>
      </>}
    </>}
  </section>;
}

function LiveTextMessage({ row }: { row: { kind: string; text: string; isComplete: boolean; isTruncated: boolean; startedWithDelta: boolean } }) {
  const [copied, setCopied] = useState(false);
  return <article className={`message timeline-message message-${row.kind.toLowerCase() === "user" ? "user" : row.kind.toLowerCase().startsWith("reasoning") ? "reasoning" : "assistant"}`}>
    <div className="avatar">{row.kind.toLowerCase() === "user" ? "You" : "A"}</div><div className="message-body">
      <div className="message-heading"><span><strong>{row.kind}</strong><small>{row.isComplete ? "Complete" : "Streaming"}</small></span><span className="message-actions">
        <button type="button" className="copy-markdown" onClick={() => void writeMarkdown(text => navigator.clipboard.writeText(text), row.text).then(result => { setCopied(result === "copied"); if (result === "copied") window.setTimeout(() => setCopied(false), 1600); })}>{copied ? "Copied" : "Copy Markdown"}</button>
      </span></div>
      <MarkdownContent source={row.text} />
      {row.isTruncated && <p className="detail">Text prefix truncated.</p>}
      {row.startedWithDelta && <p className="detail">Earlier text may be missing.</p>}
    </div>
  </article>;
}
