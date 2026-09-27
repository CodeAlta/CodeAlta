import { useEffect, useRef, useState, useSyncExternalStore, type KeyboardEvent as ReactKeyboardEvent } from "react";
import type { ReminderDetailRequest, ReminderDetailResponse, ReminderListRequest, ReminderListResponse } from "#neoastra";
import type { ReminderTarget, createReminderActions } from "./reminderActions";
import { reminderDelaySeconds } from "./reminderDuration";
import { validReminderList } from "./reminderListObservation";
import { useShellLanguage } from "./shellLanguage";

type DetailNotice = "Host identity changed. Reload required." | "Reminder is unavailable or was deleted. Refresh the list."
  | "Reminder host is closed. Reload required." | "Reminder detail could not be read. Refresh the list to try again."
  | "Reminder changed, finished or was deleted. Refresh before saving again; your edit draft is retained.";
type ReadNotice = { kind: "changed" } | { kind: "failed" } | { kind: "unavailable"; status: string };

export function ReminderPanel({ target, read, readDetail, actions, mutationAllowed, canMutate, readOnly = false }: {
  target: ReminderTarget | null;
  mutationAllowed: boolean;
  readOnly?: boolean;
  canMutate: () => boolean;
  read: (request: ReminderListRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<ReminderListResponse>;
  readDetail: (request: ReminderDetailRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<ReminderDetailResponse>;
  actions: ReturnType<typeof createReminderActions>;
}) {
  const { t } = useShellLanguage();
  useSyncExternalStore(actions.subscribe, () => target ? actions.get(target) : undefined);
  const [page, setPage] = useState<ReminderListResponse>();
  const [error, setError] = useState<ReadNotice | null>(null);
  const [content, setContent] = useState("");
  const [delay, setDelay] = useState("300");
  const [repeat, setRepeat] = useState("1");
  const [selected, setSelected] = useState<string | null>(null);
  const [confirmation, setConfirmation] = useState("");
  const [detail, setDetail] = useState<ReminderDetailResponse>();
  const [detailError, setDetailError] = useState<DetailNotice | null>(null);
  const [confirmLoad, setConfirmLoad] = useState<string | null>(null);
  const [editor, setEditor] = useState<{ base: ReminderDetailResponse; text: string }>();
  const [confirmSelection, setConfirmSelection] = useState<string | null>(null);
  const [confirmDiscardEdit, setConfirmDiscardEdit] = useState(false);
  const panelRef = useRef<HTMLElement>(null);
  const contentRef = useRef<HTMLTextAreaElement>(null);
  const delayRef = useRef<HTMLInputElement>(null);
  const repeatRef = useRef<HTMLInputElement>(null);
  const createTrigger = useRef<HTMLButtonElement>(null);
  const refreshTrigger = useRef<HTMLButtonElement>(null);
  const editorRef = useRef<HTMLTextAreaElement>(null);
  const saveTrigger = useRef<HTMLButtonElement>(null);
  const confirmationTrigger = useRef<HTMLInputElement>(null);
  const selectionVersion = useRef(0);
  const [reload, setReload] = useState(0);
  const latest = useRef(target);
  latest.current = target;
  useEffect(() => {
    latest.current = target;
    return () => { latest.current = null; };
  }, [target?.epoch, target?.sessionId]);
  useEffect(() => {
    selectionVersion.current++;
    setPage(undefined); setError(null); setSelected(null); setConfirmation(""); setContent(""); setConfirmLoad(null);
    setEditor(undefined); setConfirmSelection(null); setConfirmDiscardEdit(false);
  }, [target?.epoch, target?.sessionId]);
  useEffect(() => {
    if (!target) return;
    const controller = new AbortController();
    setPage(undefined); setError(null);
    void read({ expectedEpoch: target.epoch, sessionId: target.sessionId },
      { signal: controller.signal, timeoutMilliseconds: 15000 }).then(value => {
      if (controller.signal.aborted) return;
      if (!validReminderList(target, value)) {
        setError(value.status === "stale_epoch" || value.epoch !== target.epoch ? { kind: "changed" }
          : { kind: "unavailable", status: value.status }); return;
      }
      setPage(value);
    }).catch(() => { if (!controller.signal.aborted) setError({ kind: "failed" }); });
    return () => controller.abort();
  }, [target?.epoch, target?.sessionId, reload, read]);
  const active = page?.epoch === target?.epoch && page?.sessionId === target?.sessionId ? page : undefined;
  const row = active?.reminders.find(item => item.id === selected);
  useEffect(() => {
    setDetail(undefined); setDetailError(null); setConfirmLoad(null);
    if (!target || !row || !mutationAllowed) return;
    const controller = new AbortController();
    void readDetail({ expectedEpoch: target.epoch, sessionId: target.sessionId, reminderId: row.id },
      { signal: controller.signal, timeoutMilliseconds: 15000 }).then(value => {
      if (controller.signal.aborted) return;
      if (value.epoch !== target.epoch || value.sessionId !== target.sessionId || value.reminderId !== row.id ||
        value.status !== "ok" || typeof value.content !== "string" || !value.content.trim() || value.content.length > 4096 ||
        typeof value.editRevision !== "string" || !/^(0|[1-9]\d{0,18})$/.test(value.editRevision) ||
        value.delaySeconds !== row.delaySeconds || value.repeatCount !== row.repeatCount) {
        setDetailError(value.status === "stale_epoch" || value.epoch !== target.epoch ? "Host identity changed. Reload required."
          : value.status === "missing_reminder" ? "Reminder is unavailable or was deleted. Refresh the list."
          : value.status === "closed" ? "Reminder host is closed. Reload required."
          : "Reminder detail could not be read. Refresh the list to try again.");
        return;
      }
      setDetail(value);
      setEditor(previous => previous && previous.base.reminderId === row.id && previous.text !== previous.base.content
        ? previous : { base: value, text: value.content! });
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
  const dirtyEdit = !!editor && editor.text !== editor.base.content;
  const orphanDraft = target && dirtyEdit && editor && editor.base.epoch === target.epoch &&
    editor.base.sessionId === target.sessionId && !shownDetail ? editor : undefined;
  const retainedSave = target && operation?.kind === "save" && (operation.pending || operation.hold) &&
    operation.request && "editRevision" in operation.request && operation.request.expectedEpoch === target.epoch &&
    operation.request.sessionId === target.sessionId ? operation.request : undefined;
  function choose(id: string, discard = false) {
    if (id === selected) return;
    if (dirtyEdit && !discard) { setConfirmSelection(id); return; }
    selectionVersion.current++;
    setSelected(id); setConfirmation(""); setConfirmLoad(null); setDetail(undefined); setDetailError(null);
    setEditor(undefined); setConfirmSelection(null); setConfirmDiscardEdit(false);
  }
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
  async function save() {
    if (!target || !canMutate() || !row || !shownDetail || row.state !== "active" || !editor ||
      editor.base.editRevision !== shownDetail.editRevision || editor.base.reminderId !== row.id ||
      !dirtyEdit || !editor.text.trim() || editor.text.length > 4096 || blocked) return;
    const captured = target;
    const version = selectionVersion.current;
    const id = row.id;
    const result = await actions.submit(captured, { expectedEpoch: captured.epoch, sessionId: captured.sessionId,
      reminderId: id, editRevision: editor.base.editRevision!, content: editor.text }, "save");
    if (latest.current?.epoch !== captured.epoch || latest.current.sessionId !== captured.sessionId ||
      version !== selectionVersion.current) return;
    if (result) { setEditor(undefined); setDetail(undefined); setReload(n => n + 1); }
    else if (["conflict", "missing_reminder", "completed"].includes(actions.get(captured)?.status ?? "")) {
      setDetail(undefined);
      setDetailError("Reminder changed, finished or was deleted. Refresh before saving again; your edit draft is retained.");
    }
  }
  function panelKeyDown(event: ReactKeyboardEvent<HTMLElement>) {
    const source = event.target;
    if (!(source instanceof HTMLElement) || !panelRef.current?.contains(source) ||
      event.defaultPrevented || event.repeat || event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229 ||
      document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]') ||
      confirmLoad !== null || confirmSelection !== null || confirmDiscardEdit ||
      !target || !mutationAllowed || !canMutate()) return;
    const editing = !!source.closest("input, textarea, select, [contenteditable]");
    const key = event.key.toLowerCase();
    const ctrl = event.ctrlKey && !event.metaKey && !event.altKey && !event.shiftKey;
    const matchesTarget = (element: HTMLElement | null) => !!element?.isConnected && panelRef.current?.contains(element) === true &&
      element.dataset.epoch === target.epoch && element.dataset.sessionId === target.sessionId;
    const inCreateForm = [contentRef.current, delayRef.current, repeatRef.current, createTrigger.current]
      .some(control => control === source && control.isConnected && !control.disabled);
    let handled = false;
    if (ctrl && key === "enter" && inCreateForm && !blocked && matchesTarget(createTrigger.current) && !createTrigger.current!.disabled) {
      createTrigger.current!.click(); handled = true;
    } else if (ctrl && key === "r" && !editing && matchesTarget(refreshTrigger.current) && !refreshTrigger.current!.disabled) {
      refreshTrigger.current!.click(); handled = true;
    } else if (ctrl && key === "e" && !editing && row?.state === "active" && shownDetail && !blocked &&
      matchesTarget(editorRef.current) && editorRef.current!.dataset.reminderId === row.id && !editorRef.current!.disabled) {
      editorRef.current!.focus(); handled = true;
    } else if (ctrl && key === "s" && (!editing || source === editorRef.current) && row?.state === "active" &&
      shownDetail && !blocked && matchesTarget(saveTrigger.current) && !saveTrigger.current!.disabled &&
      saveTrigger.current!.dataset.reminderId === row.id && saveTrigger.current!.dataset.editRevision === shownDetail.editRevision) {
      saveTrigger.current!.click(); handled = true;
    } else if (!event.ctrlKey && !event.altKey && !event.metaKey && !event.shiftKey && key === "delete" &&
      !editing && row && !blocked && matchesTarget(confirmationTrigger.current) &&
      confirmationTrigger.current!.dataset.reminderId === row.id && !confirmationTrigger.current!.disabled) {
      confirmationTrigger.current!.focus(); handled = true;
    }
    if (handled) { event.preventDefault(); event.stopPropagation(); }
  }
  return <main ref={panelRef} onKeyDown={panelKeyDown} className="configuration-page reminder-page" aria-label={t("Reminders")}>
    <header className="page-heading"><span className="eyebrow">{t("Desktop / Reminders")}</span><h1>{t("Reminders")}</h1>
      <p>{t("Delayed prompts for the selected session. Schedules are in memory only and are lost when the host stops. At firing, the owned host attempts one Send; busy, unavailable or failed sends are not retried. Completion means the attempt finished, not that the agent answered.")}</p></header>
    {!target ? <p role="status">{t("Select an owned session to manage its reminders.")}</p> : <>
      {!mutationAllowed && <p role="alert">{readOnly
        ? t("Archived project is read-only. Retained evidence can be inspected, but no Save, Create, Delete or shortcut write is available.")
        : t("Host identity is invalidated. Reload before changing reminders.")}</p>}
      <p>{t("Session:")} <code>{target.sessionId}</code>.</p>
      <button ref={refreshTrigger} type="button" data-epoch={target.epoch} data-session-id={target.sessionId}
        onClick={() => setReload(n => n + 1)}>{t("Refresh reminders")}</button>
      {error && <p role="alert" className="error-text">{error.kind === "changed" ? t("Host identity changed. Reload required.") : error.kind === "failed" ? t("Reminder list could not be read.") : t("Reminder list unavailable ({status}).", { status: error.status })}</p>}
      {!active && !error && <p role="status">{t("Loading reminders.")}</p>}
      {operation && <p role={operation.hold ? "alert" : "status"}>{operation.message}</p>}
      {full && <p role="alert">{t("Pending or uncertain reminder admissions fill this window. No operation was retried or evicted.")}</p>}
      {retainedSave && <section className="reminder-recovery" aria-label={t("Retained reminder Save")}>
        <h2>{t(operation?.pending ? "Pending reminder Save" : "Uncertain reminder Save")}</h2>
        <p>{t("This exact Save may already have committed. Refresh only observes the schedule; it does not retry, rebase or retarget this request.")}</p>
        <p>{t("Host epoch:")} <code>{retainedSave.expectedEpoch}</code>. {t("Session:")} <code>{retainedSave.sessionId}</code>.
          {t("Reminder ID:")} <code>{retainedSave.reminderId}</code>. {t("Original edit revision:")} <code>{retainedSave.editRevision}</code>.</p>
        <pre aria-label={t("Retained Save full message")}>{retainedSave.content}</pre>
      </section>}
      {orphanDraft && <section className="reminder-recovery" aria-label={t("Unsaved reminder edit recovery")}>
        <h2>{t("Unsaved reminder edit")}</h2>
        <p>{t("The original detail is unavailable. This is local draft text, not a confirmed Save. Refresh does not resubmit it.")}</p>
        <p>{t("Host epoch:")} <code>{orphanDraft.base.epoch}</code>. {t("Session:")} <code>{orphanDraft.base.sessionId}</code>.
          {t("Reminder ID:")} <code>{orphanDraft.base.reminderId}</code>. {t("Original edit revision:")} <code>{orphanDraft.base.editRevision}</code>.</p>
        <pre aria-label={t("Unsaved edit full message")}>{orphanDraft.text}</pre>
        {!operation?.pending && !operation?.hold && <>
          <button type="button" onClick={() => setConfirmDiscardEdit(true)}>{t("Discard unsaved edit")}</button>
          {confirmDiscardEdit && <p role="alert">{t("Discard unsaved message changes?")}
            <button type="button" onClick={() => { setEditor(undefined); setConfirmDiscardEdit(false); }}>{t("Confirm discard edit")}</button>{" "}
            <button type="button" onClick={() => setConfirmDiscardEdit(false)}>{t("Keep edit draft")}</button></p>}
        </>}
      </section>}
      {active && <div className="model-catalog-layout"><section className="model-catalog-providers" aria-label={t("Reminder list")}>
        <h2>{t("Schedules")}</h2><p role="status">{t("As of refresh: {active} active, {completed} completed.", { active: active.activeCount, completed: active.completedCount })}</p>
        {active.reminders.length === 0 && <p>{t("No reminders for this session.")}</p>}
        {active.reminders.map(item => <button type="button" key={item.id} aria-pressed={selected === item.id}
          onClick={() => choose(item.id)}>
          <strong>{t("Preview:")} {item.preview}</strong><small>{t("{state} · {fired}/{repeat} attempts · {id}", { state: item.state === "active" || item.state === "completed" ? t(item.state) : item.state, fired: item.firedCount, repeat: item.repeatCount, id: item.id })}</small>
        </button>)}
        {confirmSelection && <p role="alert">{t("Discard the unsaved reminder message draft to switch selection?")}
          <button type="button" onClick={() => choose(confirmSelection, true)}>{t("Discard edit and switch")}</button>{" "}
          <button type="button" onClick={() => setConfirmSelection(null)}>{t("Keep edit draft")}</button></p>}
      </section><section className="model-catalog-results" aria-label={t("Reminder details and creation")}>
        <h2>{t("Create reminder")}</h2><p>{t("Ctrl+Enter creates only from the Create message, delay, repeat or button; Enter in the message inserts a line.")}</p>
        <label htmlFor="reminder-content">{t("Prompt to send")}</label>
        <textarea ref={contentRef} id="reminder-content" value={content} maxLength={4096} onChange={event => { setContent(event.target.value); setConfirmLoad(null); }} />
        <label htmlFor="reminder-delay">{t("Delay: whole seconds (1–86400) or invariant HH:mm:ss / d.HH:mm:ss")}</label>
        <input ref={delayRef} id="reminder-delay" type="text" maxLength={24} value={delay} onChange={event => { setDelay(event.target.value); setConfirmLoad(null); }} />
        <label htmlFor="reminder-repeat">{t("Total attempts (1–20)")}</label>
        <input ref={repeatRef} id="reminder-repeat" type="number" min="1" max="20" step="1" value={repeat} onChange={event => { setRepeat(event.target.value); setConfirmLoad(null); }} />
        {delaySeconds === null && <p role="alert">{t("Enter 1–86400 whole seconds or HH:mm:ss (00–23 hours), optionally prefixed with d. (e.g. 1.00:00:00). Fractions are not accepted.")}</p>}
        {!validRepeat && <p role="alert">{t("Enter a whole repeat count between 1 and 20.")}</p>}
        <button ref={createTrigger} type="button" data-epoch={target.epoch} data-session-id={target.sessionId}
          disabled={blocked || !content.trim() || content.length > 4096 || delaySeconds === null || !validRepeat}
          onClick={() => void create()}>{t("Create reminder")}</button>
        {row && <section className="selected-reminder" aria-label={t("Selected reminder")}><h3>{t("Selected reminder")}</h3>
          <p>{t("{id} · {state} · {fired}/{repeat} attempts · every {delay} seconds.", { id: row.id, state: row.state === "active" || row.state === "completed" ? t(row.state) : row.state, fired: row.firedCount, repeat: row.repeatCount, delay: row.delaySeconds })}</p>
          <p>{t("Next due: {due}. Last send exit code: {code}.", { due: row.dueAt ?? t("None"), code: row.lastExitCode ?? t("None") })}
            {row.lastError && ` ${t("Last error:")} ${row.lastError}`}</p>
          {!shownDetail && !detailError && <p role="status">{t("Loading full reminder message.")}</p>}
          {detailError && <p role="alert">{t(detailError)}</p>}
          {shownDetail && <><h4>{t("Full scheduled message")}</h4>
            <p aria-label={t("Full reminder message")} style={{ whiteSpace: "pre-wrap", overflowWrap: "anywhere" }}>{shownDetail.content}</p>
            <label htmlFor="reminder-edit">{t("Edit full reminder message (active schedule only)")}</label>
            <textarea ref={editorRef} id="reminder-edit" data-epoch={target.epoch} data-session-id={target.sessionId}
              data-reminder-id={row.id} maxLength={4096} value={editor?.text ?? ""}
              disabled={blocked || row.state !== "active"} onChange={event => { setEditor(previous => previous && { ...previous, text: event.target.value }); setConfirmDiscardEdit(false); }} />
            <button ref={saveTrigger} type="button" data-epoch={target.epoch} data-session-id={target.sessionId}
              data-reminder-id={row.id} data-edit-revision={editor?.base.editRevision ?? ""}
              disabled={blocked || row.state !== "active" || !dirtyEdit || !editor?.text.trim() ||
              editor.text.length > 4096 || editor.base.editRevision !== shownDetail.editRevision}
              onClick={() => void save()}>{t("Save message")}</button>
            {dirtyEdit && <><button type="button" disabled={blocked} onClick={() => setConfirmDiscardEdit(true)}>{t("Discard edit draft")}</button>
              {confirmDiscardEdit && <p role="alert">{t("Discard unsaved message changes?")}
                <button type="button" disabled={blocked} onClick={() => { setEditor({ base: shownDetail, text: shownDetail.content! }); setConfirmDiscardEdit(false); }}>{t("Confirm discard edit")}</button>{" "}
                <button type="button" onClick={() => setConfirmDiscardEdit(false)}>{t("Keep edit draft")}</button></p>}</>}
            <p>{t("Saved edits affect only future captured deliveries, not already-admitted sends. Saving does not change the delay, repeat count, due time or attempts. Completed reminders cannot be edited.")}</p>
            <button type="button" disabled={blocked} onClick={() => useAsNew(false)}>{t("Use as new reminder")}</button>
            {confirmLoad === row.id && <p role="alert">{t("The Create form has edits. Discard them to load this reminder without changing its schedule.")}
              <button type="button" disabled={blocked} onClick={() => useAsNew(true)}>{t("Discard draft and use reminder")}</button>{" "}
              <button type="button" onClick={() => setConfirmLoad(null)}>{t("Keep draft")}</button></p>}</>}
          <label htmlFor="reminder-confirm">{t("To delete, type the exact reminder ID")}</label>
          <input ref={confirmationTrigger} id="reminder-confirm" data-epoch={target.epoch} data-session-id={target.sessionId}
            data-reminder-id={row.id} value={confirmation} onChange={event => setConfirmation(event.target.value)} />
          <button type="button" disabled={blocked || confirmation !== row.id} onClick={() => void remove()}>{t("Delete confirmed reminder")}</button>
          <p>{t("Deletion cannot retract an already captured delivery or a submitted run.")}</p></section>}
      </section></div>}
    </>}
  </main>;
}
