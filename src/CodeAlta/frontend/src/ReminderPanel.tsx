import { useEffect, useRef, useState, useSyncExternalStore } from "react";
import type { ReminderDetailRequest, ReminderDetailResponse, ReminderListRequest, ReminderListResponse } from "#neoastra";
import type { ReminderTarget, createReminderActions } from "./reminderActions";
import { reminderDelaySeconds } from "./reminderDuration";

export function ReminderPanel({ target, read, readDetail, actions, mutationAllowed, canMutate }: {
  target: ReminderTarget | null;
  mutationAllowed: boolean;
  canMutate: () => boolean;
  read: (request: ReminderListRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<ReminderListResponse>;
  readDetail: (request: ReminderDetailRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<ReminderDetailResponse>;
  actions: ReturnType<typeof createReminderActions>;
}) {
  useSyncExternalStore(actions.subscribe, () => target ? actions.get(target) : undefined);
  const [page, setPage] = useState<ReminderListResponse>();
  const [error, setError] = useState("");
  const [content, setContent] = useState("");
  const [delay, setDelay] = useState("300");
  const [repeat, setRepeat] = useState("1");
  const [selected, setSelected] = useState<string | null>(null);
  const [confirmation, setConfirmation] = useState("");
  const [detail, setDetail] = useState<ReminderDetailResponse>();
  const [detailError, setDetailError] = useState("");
  const [confirmLoad, setConfirmLoad] = useState<string | null>(null);
  const [reload, setReload] = useState(0);
  const latest = useRef(target);
  latest.current = target;
  useEffect(() => {
    latest.current = target;
    return () => { latest.current = null; };
  }, [target?.epoch, target?.sessionId]);
  useEffect(() => {
    setPage(undefined); setError(""); setSelected(null); setConfirmation(""); setContent(""); setConfirmLoad(null);
  }, [target?.epoch, target?.sessionId]);
  useEffect(() => {
    if (!target) return;
    const controller = new AbortController();
    setPage(undefined); setError("");
    void read({ expectedEpoch: target.epoch, sessionId: target.sessionId },
      { signal: controller.signal, timeoutMilliseconds: 15000 }).then(value => {
      if (controller.signal.aborted) return;
      if (value.epoch !== target.epoch || value.sessionId !== target.sessionId || value.status !== "ok" ||
        !Array.isArray(value.reminders) || value.reminders.length > 32 ||
        new Set(value.reminders.map(row => row.id)).size !== value.reminders.length ||
        value.reminders.some(row => typeof row.id !== "string" || !row.id || row.id.length > 256 ||
          !["active", "completed"].includes(row.state) || typeof row.preview !== "string" || row.preview.length > 160 ||
          !Number.isInteger(row.delaySeconds) || row.delaySeconds < 1 || row.delaySeconds > 86400 ||
          !Number.isInteger(row.repeatCount) || row.repeatCount < 1 || row.repeatCount > 20 ||
          !Number.isInteger(row.firedCount) || row.firedCount < 0 || row.firedCount > row.repeatCount ||
          row.lastError != null && (typeof row.lastError !== "string" || row.lastError.length > 128)) ||
        value.activeCount !== value.reminders.filter(row => row.state === "active").length ||
        value.completedCount !== value.reminders.filter(row => row.state === "completed").length) {
        setError(value.status === "stale_epoch" || value.epoch !== target.epoch ? "Host identity changed. Reload required."
          : `Reminder list unavailable (${value.status}).`); return;
      }
      setPage(value);
    }).catch(() => { if (!controller.signal.aborted) setError("Reminder list could not be read."); });
    return () => controller.abort();
  }, [target?.epoch, target?.sessionId, reload, read]);
  const active = page?.epoch === target?.epoch && page?.sessionId === target?.sessionId ? page : undefined;
  const row = active?.reminders.find(item => item.id === selected);
  useEffect(() => {
    setDetail(undefined); setDetailError(""); setConfirmLoad(null);
    if (!target || !row || !mutationAllowed) return;
    const controller = new AbortController();
    void readDetail({ expectedEpoch: target.epoch, sessionId: target.sessionId, reminderId: row.id },
      { signal: controller.signal, timeoutMilliseconds: 15000 }).then(value => {
      if (controller.signal.aborted) return;
      if (value.epoch !== target.epoch || value.sessionId !== target.sessionId || value.reminderId !== row.id ||
        value.status !== "ok" || typeof value.content !== "string" || !value.content.trim() || value.content.length > 4096 ||
        value.delaySeconds !== row.delaySeconds || value.repeatCount !== row.repeatCount) {
        setDetailError(value.status === "stale_epoch" || value.epoch !== target.epoch ? "Host identity changed. Reload required."
          : value.status === "missing_reminder" ? "Reminder is unavailable or was deleted. Refresh the list."
          : value.status === "closed" ? "Reminder host is closed. Reload required."
          : "Reminder detail could not be read. Refresh the list to try again.");
        return;
      }
      setDetail(value);
    }).catch(() => { if (!controller.signal.aborted) setDetailError("Reminder detail could not be read. Refresh the list to try again."); });
    return () => controller.abort();
  }, [target?.epoch, target?.sessionId, row?.id, active, readDetail, mutationAllowed]);
  const shownDetail = detail && target && row && active && mutationAllowed && detail.epoch === target.epoch &&
    detail.sessionId === target.sessionId && detail.reminderId === row.id ? detail : undefined;
  const operation = target ? actions.get(target) : undefined;
  const delaySeconds = reminderDelaySeconds(delay);
  const validRepeat = /^[1-9]\d*$/.test(repeat) && Number(repeat) <= 20;
  const full = target && actions.isFull(target);
  const blocked = !mutationAllowed || !!operation?.pending || !!operation?.hold || !!full;
  function useAsNew(discard: boolean) {
    if (!target || !row || !shownDetail || !canMutate() || blocked ||
      actions.get(target)?.pending || actions.get(target)?.hold || actions.isFull(target) ||
      shownDetail.content == null || shownDetail.delaySeconds == null || shownDetail.repeatCount == null ||
      (discard && confirmLoad !== row.id)) return;
    if (!discard && (content !== "" || delay !== "300" || repeat !== "1")) { setConfirmLoad(row.id); return; }
    setContent(shownDetail.content); setDelay(String(shownDetail.delaySeconds)); setRepeat(String(shownDetail.repeatCount));
    setConfirmLoad(null);
  }
  async function create() {
    if (!target || !canMutate() || !content.trim() || content.length > 4096 || delaySeconds === null || !validRepeat || blocked) return;
    const captured = target;
    if (await actions.submit(captured, { expectedEpoch: captured.epoch, sessionId: captured.sessionId,
      content, delaySeconds, repeatCount: Number(repeat) }, "create") &&
      latest.current?.epoch === captured.epoch && latest.current.sessionId === captured.sessionId) setReload(n => n + 1);
  }
  async function remove() {
    if (!target || !canMutate() || !row || confirmation !== row.id || blocked) return;
    const captured = target;
    if (await actions.submit(captured, { expectedEpoch: captured.epoch, sessionId: captured.sessionId,
      reminderId: row.id, confirmation }, "delete") &&
      latest.current?.epoch === captured.epoch && latest.current.sessionId === captured.sessionId) {
      setConfirmation(""); setReload(n => n + 1);
    }
  }
  return <main className="configuration-page reminder-page" aria-label="Reminders">
    <header className="page-heading"><span className="eyebrow">Desktop / Reminders</span><h1>Reminders</h1>
      <p>Delayed prompts for the selected session. Schedules are in memory only and are lost when the host stops.
        At firing, the owned host attempts one Send; busy, unavailable or failed sends are not retried. Completion means the attempt finished, not that the agent answered.</p></header>
    {!target ? <p role="status">Select an owned session to manage its reminders.</p> : <>
      {!mutationAllowed && <p role="alert">Host identity is invalidated. Reload before changing reminders.</p>}
      <p>Session: <code>{target.sessionId}</code>.</p>
      <button type="button" onClick={() => setReload(n => n + 1)}>Refresh reminders</button>
      {error && <p role="alert" className="error-text">{error}</p>}
      {!active && !error && <p role="status">Loading reminders.</p>}
      {active && <div className="model-catalog-layout"><section className="model-catalog-providers" aria-label="Reminder list">
        <h2>Schedules</h2><p role="status">As of refresh: {active.activeCount} active, {active.completedCount} completed.</p>
        {active.reminders.length === 0 && <p>No reminders for this session.</p>}
        {active.reminders.map(item => <button type="button" key={item.id} aria-pressed={selected === item.id}
          onClick={() => { setSelected(item.id); setConfirmation(""); setConfirmLoad(null); setDetail(undefined); setDetailError(""); }}>
          <strong>Preview: {item.preview}</strong><small>{item.state} · {item.firedCount}/{item.repeatCount} attempts · {item.id}</small>
        </button>)}
      </section><section className="model-catalog-results" aria-label="Reminder details and creation">
        <h2>Create reminder</h2><label htmlFor="reminder-content">Prompt to send</label>
        <textarea id="reminder-content" value={content} maxLength={4096} onChange={event => { setContent(event.target.value); setConfirmLoad(null); }} />
        <label htmlFor="reminder-delay">Delay: whole seconds (1–86400) or invariant HH:mm:ss / d.HH:mm:ss</label>
        <input id="reminder-delay" type="text" maxLength={24} value={delay} onChange={event => { setDelay(event.target.value); setConfirmLoad(null); }} />
        <label htmlFor="reminder-repeat">Total attempts (1–20)</label>
        <input id="reminder-repeat" type="number" min="1" max="20" step="1" value={repeat} onChange={event => { setRepeat(event.target.value); setConfirmLoad(null); }} />
        {delaySeconds === null && <p role="alert">Enter 1–86400 whole seconds or HH:mm:ss (00–23 hours), optionally prefixed with d. (e.g. 1.00:00:00). Fractions are not accepted.</p>}
        {!validRepeat && <p role="alert">Enter a whole repeat count between 1 and 20.</p>}
        <button type="button" disabled={blocked || !content.trim() || delaySeconds === null || !validRepeat} onClick={() => void create()}>Create reminder</button>
        {operation && <p role={operation.hold ? "alert" : "status"}>{operation.message}</p>}
        {full && <p role="alert">Pending or uncertain reminder admissions fill this window. No operation was retried or evicted.</p>}
        {row && <section aria-label="Selected reminder"><h3>Selected reminder</h3>
          <p><code>{row.id}</code> · {row.state} · {row.firedCount}/{row.repeatCount} attempts · every {row.delaySeconds} seconds.</p>
          <p>Next due: {row.dueAt ?? "None"}. Last send exit code: {row.lastExitCode ?? "None"}.
            {row.lastError && ` Last error: ${row.lastError}`}</p>
          {!shownDetail && !detailError && <p role="status">Loading full reminder message.</p>}
          {detailError && <p role="alert">{detailError}</p>}
          {shownDetail && <><h4>Full scheduled message</h4>
            <p aria-label="Full reminder message" style={{ whiteSpace: "pre-wrap", overflowWrap: "anywhere" }}>{shownDetail.content}</p>
            <button type="button" disabled={blocked} onClick={() => useAsNew(false)}>Use as new reminder</button>
            {confirmLoad === row.id && <p role="alert">The Create form has edits. Discard them to load this reminder without changing its schedule.
              <button type="button" disabled={blocked} onClick={() => useAsNew(true)}>Discard draft and use reminder</button>{" "}
              <button type="button" onClick={() => setConfirmLoad(null)}>Keep draft</button></p>}</>}
          <label htmlFor="reminder-confirm">To delete, type the exact reminder ID</label>
          <input id="reminder-confirm" value={confirmation} onChange={event => setConfirmation(event.target.value)} />
          <button type="button" disabled={blocked || confirmation !== row.id} onClick={() => void remove()}>Delete confirmed reminder</button>
          <p>Deletion cannot retract an already captured delivery or a submitted run.</p></section>}
      </section></div>}
    </>}
  </main>;
}
