import { useEffect, useRef, useState } from "react";
import type { ApplicationLogsRequest, ApplicationLogsResponse } from "#neoastra";
import { AppIcon } from "./AppIcon";

export function ApplicationLogsPanel({ read }: {
  read: (request: ApplicationLogsRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<ApplicationLogsResponse>;
}) {
  const [snapshot, setSnapshot] = useState<ApplicationLogsResponse>();
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(false);
  const [wrap, setWrap] = useState(true);
  const pending = useRef<AbortController | null>(null);
  useEffect(() => () => { pending.current?.abort(); }, []);

  function refresh() {
    pending.current?.abort();
    const controller = new AbortController();
    pending.current = controller;
    setSnapshot(undefined); setError(""); setLoading(true);
    void read({}, { signal: controller.signal, timeoutMilliseconds: 8000 }).then(value => {
      if (controller.signal.aborted) return;
      if (!(["ok", "unavailable", "read_failed"].includes(value.status)) || !Array.isArray(value.rows)
        || value.rows.length > 64 || !/^\d{1,19}$/.test(value.captureOmitted)
        || !Number.isInteger(value.readOmitted) || value.readOmitted < 0 || value.readOmitted > 128
        || value.rows.some(row => typeof row.timestamp !== "string" || row.timestamp.length > 64
          || typeof row.level !== "string" || row.level.length > 32 || typeof row.logger !== "string" || row.logger.length > 128
          || typeof row.text !== "string" || row.text.length > 2048 || typeof row.textTruncated !== "boolean")) {
        setError("Invalid logs response. Nothing was displayed."); return;
      }
      setSnapshot(value);
    }).catch(() => { if (!controller.signal.aborted) setError("Application logs could not be read."); })
      .finally(() => { if (!controller.signal.aborted) setLoading(false); });
  }

  return <main className="configuration-page application-logs">
    <header className="page-heading"><span className="eyebrow">Desktop</span><h1>Application Logs</h1>
      <p>Read-only, bounded messages captured in memory from this desktop process only. Not persisted logs or a complete history. Logs can contain sensitive information; they stay in this window.</p></header>
    <div className="logs-toolbar"><button autoFocus type="button" onClick={refresh}><AppIcon name="refresh" size={15} />Refresh logs</button>
      <label><input type="checkbox" checked={wrap} onChange={event => setWrap(event.target.checked)} /> Wrap lines</label></div>
    {loading && <p role="status">Reading current-process logs…</p>}
    {error && <p role="alert">{error}</p>}
    {snapshot?.status === "unavailable" && <p role="status">In-memory capture unavailable: this window has no desktop-owned capture (for example, external logger ownership or browser demo). No files were read.</p>}
    {snapshot?.status === "read_failed" && <p role="alert">Application logs could not be read.</p>}
    {snapshot?.status === "ok" && <>
      {(snapshot.captureOmitted !== "0" || snapshot.readOmitted > 0) && <p role="status">Older entries omitted: {snapshot.captureOmitted} from in-memory capacity, {snapshot.readOmitted} from the bounded response.</p>}
      {snapshot.rows.length === 0 ? <p role="status">No captured messages in this process.</p>
        : <ol className={`logs-rows${wrap ? " logs-wrap" : ""}`} aria-label="Current process log entries">{snapshot.rows.map((row, index) =>
          <li key={index}><span className="logs-metadata">{row.timestamp} · {row.level} · {row.logger}</span>
            <pre>{row.text}{row.textTruncated && " [message truncated]"}</pre></li>)}</ol>}
    </>}
    {!snapshot && !error && !loading && <p role="status">Select Refresh logs to read the current in-memory snapshot.</p>}
  </main>;
}
