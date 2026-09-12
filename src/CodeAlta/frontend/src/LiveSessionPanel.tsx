import { useEffect, useSyncExternalStore } from "react";
import { createSessionDisplayStore, displayRowKey, displayToolActivityKey } from "./sessionDisplay";

export function LiveSessionPanel({ store, hostEpoch, sessionId }: {
  store: ReturnType<typeof createSessionDisplayStore>; hostEpoch: string; sessionId: string;
}) {
  const observed = useSyncExternalStore(store.subscribe, store.getSnapshot);
  useEffect(() => {
    store.select(hostEpoch, sessionId);
    return () => store.detach();
  }, [store, hostEpoch, sessionId]);
  // Never flash the previous selection during the render preceding effect cleanup/admission.
  const state = observed.hostEpoch === hostEpoch && observed.sessionId === sessionId ? observed : null;
  const snapshot = state?.snapshot;
  const session = snapshot?.session;
  return <section className="live-display" aria-label="Selected session live display">
    <h3>Live status, text and reported tools — partial window</h3>
    <p className="detail">Only this selected session is observed. This is not persisted history, a complete transcript, or a tool-results/usage/interaction view. Closing or reconnecting this observation does not stop a run.</p>
    <p className="detail">A same-host reload obtains a new baseline of retained partial values only, not history, results, decisions or effects. Restart restores no authority.</p>
    <p role="status">Observation: {state?.kind ?? "loading"}{state?.code ? ` · ${state.code}` : ""}</p>
    {state?.code === "stale_epoch" && <p role="alert">The host has changed. Reload the Desktop UI before continuing; reconnecting with this old host identity will not work.</p>}
    <button type="button" disabled={state?.code === "stale_epoch"} onClick={() => store.select(hostEpoch, sessionId)}>Reconnect live display</button>
    {snapshot && <>
      <p className="detail">Projection {snapshot.projectionEpoch} · revision {snapshot.revision}</p>
      {snapshot.hasGap && <p role="status">Intermediate updates were coalesced. This replacement is the latest retained window, not recovered history.</p>}
      {snapshot.isClosed && <p role="status">Runtime display closed. No further updates will arrive on this observation.</p>}
      {(snapshot.evictedSessions !== "0" || snapshot.omittedSessionEvents !== "0") && <p className="detail">Global coverage: {snapshot.evictedSessions} session windows evicted; {snapshot.omittedSessionEvents} publications with omitted session identity.</p>}
      {!session && <p role="status">No retained live state for this session: it may not have published yet, or its window was evicted. This does not mean idle or completed.</p>}
      {session && <>
        <dl>
          <dt>Latest published lifecycle</dt><dd>{session.lifecycle?.kind ?? "Not observed"}{session.lifecycle?.runId ? ` · run ${session.lifecycle.runId}` : ""}</dd>
          <dt>Queue count</dt><dd>{session.queuedPromptCount ?? "Not observed"}</dd>
          <dt>Host status</dt><dd>{session.statusKind ?? "Not observed"}{session.statusMessage ? ` · ${session.statusMessage}` : ""}</dd>
          <dt>Provider / configuration key</dt><dd>{session.configuration?.providerId ?? "Not observed"} / {session.configuration?.providerKey ?? "Not observed"}</dd>
          <dt>Model / reasoning</dt><dd>{session.configuration?.modelId ?? "Not observed"} / {session.configuration?.reasoningEffort ?? "Not observed"}</dd>
          <dt>Agent prompt</dt><dd>{session.configuration?.agentPromptId ?? "Not observed"}</dd>
        </dl>
        {session.lifecycle?.message && <p>{session.lifecycle.message}</p>}
        {(session.metadataTruncated || session.transportTruncated) && <p className="detail">Some status/configuration labels were shortened.</p>}
        <p className="detail">Retained-window omissions: {session.evictedTextItems} text items evicted; {session.unsupportedEvents} unsupported publications. Other payload details are not projected.</p>
        <h4>REPORTED tool activity</h4>
        <p className="detail">At most two plain ToolCalls, least-to-most recently updated. These are reports, not command execution, permissions, receipts or run completion. Started precedes invocation and may precede permission resolution; it proves neither approval nor process start. Completed, Failed and Canceled are not exactly-once external effect acknowledgments. Reported phases may regress.</p>
        <p className="detail">{session.evictedToolActivities} tool identities evicted in this retained window. Missing or evicted activity is unknown, not idle or complete.</p>
        {session.toolActivities.length === 0 && <p>No reported tool activity retained.</p>}
        <ol className="history-records">{session.toolActivities.map(activity => <li key={displayToolActivityKey(session.sessionId, activity)}>
          <strong>{activity.name ?? "Unnamed tool"}</strong>{activity.isNameTruncated && <span className="detail"> · name prefix truncated</span>}
          <p>Reported {activity.phase} · provider {activity.providerId} · run {activity.runId ?? "not supplied"} · activity {activity.activityId}</p>
        </li>)}</ol>
        {session.text.length === 0 && <p>No text retained in this window.</p>}
        <ol className="history-records">{session.text.map(row => <li key={displayRowKey(session.sessionId, row)}>
          <strong>{row.kind}</strong><span className="detail"> · {row.isComplete ? "finalized content" : "streaming content"} · {row.contentId}</span>
          <pre>{row.text}</pre>
          {row.isTruncated && <p className="detail">Text prefix truncated.</p>}
          {row.startedWithDelta && <p className="detail">No finalized baseline; earlier text may be missing.</p>}
        </li>)}</ol>
      </>}
    </>}
  </section>;
}
