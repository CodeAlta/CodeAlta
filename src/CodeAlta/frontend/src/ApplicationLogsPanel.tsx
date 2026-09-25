import { useEffect, useRef, useState, useSyncExternalStore } from "react";
import type { ApplicationLogsRequest, ApplicationLogsResponse } from "#neoastra";
import { AppIcon } from "./AppIcon";
import { logClearTarget, type createApplicationLogClearActions } from "./applicationLogClear";

export function ApplicationLogsPanel({ read, clearActions }: {
  read: (request: ApplicationLogsRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<ApplicationLogsResponse>;
  clearActions: ReturnType<typeof createApplicationLogClearActions>;
}) {
  const [snapshot, setSnapshot] = useState<ApplicationLogsResponse>();
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(false);
  const [wrap, setWrap] = useState(true);
  const [confirming, setConfirming] = useState(false);
  const [confirmation, setConfirmation] = useState("");
  const clearTrigger = useRef<HTMLButtonElement>(null);
  const restoreClearFocus = useRef(false);
  const clearState = useSyncExternalStore(clearActions.subscribe, clearActions.snapshot);
  const pending = useRef<AbortController | null>(null);
  useEffect(() => () => { pending.current?.abort(); }, []);
  useEffect(() => { if (!confirming && restoreClearFocus.current) {
    restoreClearFocus.current = false; clearTrigger.current?.focus();
  } }, [confirming]);

  function refresh() {
    pending.current?.abort();
    const controller = new AbortController();
    pending.current = controller;
    setSnapshot(undefined); setError(""); setLoading(true); setConfirming(false); setConfirmation("");
    void read({}, { signal: controller.signal, timeoutMilliseconds: 8000 }).then(value => {
      if (controller.signal.aborted) return;
      if (!(["ok", "unavailable", "read_failed"].includes(value.status)) || !Array.isArray(value.rows)
        || value.rows.length > 64 || !/^\d{1,19}$/.test(value.captureOmitted)
        || !Number.isInteger(value.readOmitted) || value.readOmitted < 0 || value.readOmitted > 128
        || value.rows.some(row => typeof row.timestamp !== "string" || row.timestamp.length > 64
          || typeof row.level !== "string" || row.level.length > 32 || typeof row.logger !== "string" || row.logger.length > 128
          || typeof row.text !== "string" || row.text.length > 2048 || typeof row.textTruncated !== "boolean")
        || value.status === "ok" && value.rows.length > 0 && !logClearTarget(value)) {
        setError("Invalid logs response. Nothing was displayed."); return;
      }
      setSnapshot(value);
    }).catch(() => { if (!controller.signal.aborted) setError("Application logs could not be read."); })
      .finally(() => { if (!controller.signal.aborted) setLoading(false); });
  }

  const target = snapshot && logClearTarget(snapshot);
  const clearedPage = !!target && clearState.kind === "confirmed" && clearState.target?.boundary === target.boundary
    && clearState.target.captureId === target.captureId;
  const clearAllowed = !!target && (clearState.kind === "idle" || clearState.kind === "confirmed"
    && clearState.target?.captureId === target.captureId && BigInt(target.boundary) > BigInt(clearState.target.boundary)
    && target.grant !== clearState.target.grant);
  function cancelClear() { restoreClearFocus.current = true; setConfirming(false); setConfirmation(""); }

  return <main className="configuration-page application-logs">
    <header className="page-heading"><span className="eyebrow">Desktop</span><h1>Application Logs</h1>
      <p>Bounded messages captured in memory from this desktop process only. Not persisted logs or a complete history. Logs can contain sensitive information; they stay in this window.</p></header>
    <div className="logs-toolbar"><button autoFocus type="button" onClick={refresh}><AppIcon name="refresh" size={15} />Refresh logs</button>
      <label><input type="checkbox" checked={wrap} onChange={event => setWrap(event.target.checked)} /> Wrap lines</label>
      {clearAllowed && !confirming && <button ref={clearTrigger} type="button" onClick={() => setConfirming(true)}>Clear captured messages…</button>}</div>
    {confirming && target && <form className="logs-confirm" aria-label="Confirm captured log clear" onSubmit={event => {
      event.preventDefault();
      if (clearActions.submit(target, confirmation)) { setConfirming(false); setConfirmation(""); }
    }}><p>This clears only in-memory messages through observed boundary <strong>{target.boundary}</strong> of capture <code>{target.captureId}</code>.
      {` ${target.rows} displayed row(s) and ${target.readOmitted} additional response-omitted row(s) are covered; ${target.captureOmitted} older capacity-omitted row(s) through this boundary are already absent and are logically covered. Concurrent newer messages remain. No log files are changed.`}</p>
      <label>Type CLEAR CAPTURED LOGS to confirm<input autoFocus value={confirmation} onChange={event => setConfirmation(event.target.value)}
        onKeyDown={event => { if (event.key === "Escape" && !event.nativeEvent.isComposing && event.nativeEvent.keyCode !== 229) {
          event.preventDefault(); event.stopPropagation(); cancelClear();
        } }} /></label>
      <button type="submit" disabled={confirmation !== "CLEAR CAPTURED LOGS"}>Clear this captured snapshot</button>
      <button type="button" onClick={cancelClear}>Cancel</button></form>}
    {clearState.kind !== "idle" && clearState.target && <p role="status" className="logs-clear-status">Capture {clearState.target.captureId}, observed boundary {clearState.target.boundary}: {clearState.kind === "pending"
      ? "clear request pending; original request retained across navigation."
      : clearState.kind === "uncertain" ? "clear outcome unconfirmed; original request retained. Refresh cannot unlock or retry it."
      : clearState.kind === "refused" ? "clear refused; no entries cleared by this request. Original request remains locked."
      : `confirmed: ${clearState.result!.clearedRows} in-memory row(s) removed; ${clearState.result!.coveredOmitted} older already-omitted row(s) logically covered. Refresh to see newer messages.`}</p>}
    {loading && <p role="status">Reading current-process logs…</p>}
    {error && <p role="alert">{error}</p>}
    {snapshot?.status === "unavailable" && <p role="status">In-memory capture unavailable: this window has no desktop-owned capture (for example, external logger ownership or browser demo). No files were read.</p>}
    {snapshot?.status === "read_failed" && <p role="alert">Application logs could not be read.</p>}
    {snapshot?.status === "ok" && !clearedPage && <>
      {(snapshot.captureOmitted !== "0" || snapshot.readOmitted > 0) && <p role="status">Older entries omitted: {snapshot.captureOmitted} from in-memory capacity, {snapshot.readOmitted} from the bounded response.</p>}
      {snapshot.rows.length === 0 ? <p role="status">No captured messages in this process.</p>
        : <ol className={`logs-rows${wrap ? " logs-wrap" : ""}`} aria-label="Current process log entries">{snapshot.rows.map((row, index) =>
          <li key={index}><span className="logs-metadata">{row.timestamp} · {row.level} · {row.logger}</span>
            <pre>{row.text}{row.textTruncated && " [message truncated]"}</pre></li>)}</ol>}
    </>}
    {!snapshot && !error && !loading && <p role="status">Select Refresh logs to read the current in-memory snapshot.</p>}
  </main>;
}
