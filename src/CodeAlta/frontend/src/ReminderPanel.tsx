import { useEffect, useRef, useState, useSyncExternalStore, type KeyboardEvent as ReactKeyboardEvent } from "react";
import { Button, ButtonGroup, Callout, Tag } from "@blueprintjs/core";
import { AppIcon } from "./AppIcon";
import type { ReminderDetailRequest, ReminderDetailResponse, ReminderListRequest, ReminderListResponse } from "#neoastra";
import type { ReminderTarget, createReminderActions } from "./reminderActions";
import { formatReminderDelay, reminderDelaySeconds } from "./reminderDuration";
import { validReminderList } from "./reminderListObservation";
import { useShellLanguage } from "./shellLanguage";

type DetailNotice = "Host identity changed. Reload required." | "Reminder is unavailable or was deleted. Refresh the list."
  | "Reminder host is closed. Reload required." | "Reminder detail could not be read. Refresh the list to try again."
  | "Reminder changed, finished or was deleted. Refresh before saving again; your edit draft is retained.";
// Pending-selection marker for switching from a dirty edit to the create form.
const newSelection = "\u0000new";
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
  const [delay, setDelay] = useState("5m");
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
  const confirmationTrigger = useRef<HTMLButtonElement>(null);
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
    if (!discard && (content !== "" || delay !== "5m" || repeat !== "1")) { setConfirmLoad(row.id); return; }
    setContent(shownDetail.content); setDelay(formatReminderDelay(shownDetail.delaySeconds)); setRepeat(String(shownDetail.repeatCount));
    setConfirmLoad(null);
    // Show the filled Create form, unless that would drop an unsaved message edit.
    if (!dirtyEdit) { selectionVersion.current++; setSelected(null); setConfirmation(""); setEditor(undefined); }
  }
  async function create() {
    if (!target || !canMutate() || !content.trim() || content.length > 4096 || delaySeconds === null || !validRepeat || blocked) return;
    const captured = target;
    if (await actions.submit(captured, { expectedEpoch: captured.epoch, sessionId: captured.sessionId,
      content, delaySeconds, repeatCount: Number(repeat) }, "create") &&
      latest.current?.epoch === captured.epoch && latest.current.sessionId === captured.sessionId) { setContent(""); setReload(n => n + 1); }
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
      Array.from(document.querySelectorAll('dialog[open], [role="dialog"][aria-modal="true"]')).some(modal => !modal.contains(panelRef.current)) ||
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
  const stateLabel = (state: string) => state === "active" || state === "completed" ? t(state) : state;
  const dueText = (value: string | null | undefined) => {
    if (!value) return t("None");
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? value : date.toLocaleString();
  };
  function startNew() {
    if (selected === null) { contentRef.current?.focus(); return; }
    if (dirtyEdit) { setConfirmSelection(newSelection); return; }
    selectionVersion.current++;
    setSelected(null); setConfirmation(""); setConfirmLoad(null); setDetail(undefined); setDetailError(null);
    setEditor(undefined); setConfirmSelection(null); setConfirmDiscardEdit(false);
  }
  return <main ref={panelRef} onKeyDown={panelKeyDown} className="reminder-page" aria-label={t("Reminders")}>
    {!target ? <Callout role="status" compact>{t("Select an owned session to manage its reminders.")}</Callout> : <>
      <div className="reminder-toolbar">
        <div className="reminder-summary">
          {active ? <><Tag minimal round intent={active.activeCount ? "primary" : "none"}>{t("{count} active", { count: active.activeCount })}</Tag>
            <Tag minimal round>{t("{count} completed", { count: active.completedCount })}</Tag></>
            : !error && <span role="status" className="bp6-text-muted">{t("Loading reminders.")}</span>}
          <code className="reminder-session" title={t("Session:") + " " + target.sessionId}>{target.sessionId}</code>
        </div>
        <Button ref={refreshTrigger} variant="minimal" icon={<AppIcon name="refresh" size={15} />} data-epoch={target.epoch} data-session-id={target.sessionId}
          aria-label={t("Refresh reminders")} title={t("Refresh reminders")} onClick={() => setReload(n => n + 1)} />
        <Button intent="primary" icon={<AppIcon name="plus" size={15} />} text={t("New reminder")} disabled={!mutationAllowed} onClick={startNew} />
      </div>
      {!mutationAllowed && <Callout role="alert" compact intent="warning">{readOnly
        ? t("Archived project is read-only. Retained evidence can be inspected, but no Save, Create, Delete or shortcut write is available.")
        : t("Host identity is invalidated. Reload before changing reminders.")}</Callout>}
      {error && <Callout role="alert" compact intent="danger">{error.kind === "changed" ? t("Host identity changed. Reload required.") : error.kind === "failed" ? t("Reminder list could not be read.") : t("Reminder list unavailable ({status}).", { status: error.status })}</Callout>}
      {operation && !operation.pending && operation.status !== "ok" && <Callout role="alert" compact intent="warning">{operation.message}</Callout>}
      {full && <Callout role="alert" compact intent="warning">{t("Pending or uncertain reminder admissions fill this window. No operation was retried or evicted.")}</Callout>}
      {retainedSave && <section className="reminder-recovery" aria-label={t("Retained reminder Save")}>
        <h3>{t(operation?.pending ? "Pending reminder Save" : "Uncertain reminder Save")}</h3>
        <p>{t("This exact Save may already have committed. Refresh only observes the schedule; it does not retry, rebase or retarget this request.")}</p>
        <p>{t("Reminder ID:")} <code>{retainedSave.reminderId}</code>. {t("Original edit revision:")} <code>{retainedSave.editRevision}</code>.</p>
        <pre aria-label={t("Retained Save full message")}>{retainedSave.content}</pre>
      </section>}
      {orphanDraft && <section className="reminder-recovery" aria-label={t("Unsaved reminder edit recovery")}>
        <h3>{t("Unsaved reminder edit")}</h3>
        <p>{t("The original detail is unavailable. This is local draft text, not a confirmed Save. Refresh does not resubmit it.")}</p>
        <p>{t("Reminder ID:")} <code>{orphanDraft.base.reminderId}</code>. {t("Original edit revision:")} <code>{orphanDraft.base.editRevision}</code>.</p>
        <pre aria-label={t("Unsaved edit full message")}>{orphanDraft.text}</pre>
        {!operation?.pending && !operation?.hold && <div className="reminder-actions">
          {confirmDiscardEdit ? <><span role="alert">{t("Discard unsaved message changes?")}</span>
            <Button size="small" intent="danger" text={t("Confirm discard edit")} onClick={() => { setEditor(undefined); setConfirmDiscardEdit(false); }} />
            <Button size="small" text={t("Keep edit draft")} onClick={() => setConfirmDiscardEdit(false)} /></>
            : <Button size="small" text={t("Discard unsaved edit")} onClick={() => setConfirmDiscardEdit(true)} />}
        </div>}
      </section>}
      {active && <div className="reminder-layout">
        <section className="reminder-list" aria-label={t("Reminder list")}>
          {active.reminders.length === 0 && <p className="reminder-empty">{t("No reminders for this session.")}</p>}
          {active.reminders.map(item => <button type="button" key={item.id} className="reminder-row" data-state={item.state} aria-pressed={selected === item.id}
            title={item.id} onClick={() => choose(item.id)}>
            <span className="reminder-row-preview">{item.preview}</span>
            <span className="reminder-row-meta"><span className="reminder-state" data-state={item.state}>{stateLabel(item.state)}</span>
              <span>{t("{fired}/{repeat} sent · every {delay}", { fired: item.firedCount, repeat: item.repeatCount, delay: formatReminderDelay(item.delaySeconds) })}</span></span>
          </button>)}
          {confirmSelection && <Callout role="alert" compact intent="warning" className="reminder-confirm">{t("Discard the unsaved reminder message draft to switch selection?")}
            <div className="reminder-actions">
              <Button size="small" intent="danger" text={t("Discard edit and switch")} onClick={() => {
                if (confirmSelection === newSelection) { setEditor(undefined); setConfirmSelection(null); selectionVersion.current++; setSelected(null); setConfirmation(""); setDetail(undefined); setDetailError(null); setConfirmDiscardEdit(false); }
                else choose(confirmSelection, true);
              }} />
              <Button size="small" text={t("Keep edit draft")} onClick={() => setConfirmSelection(null)} /></div></Callout>}
        </section>
        {!row ? <section className="reminder-editor" aria-label={t("Reminder details and creation")}>
          <h3>{t("New reminder")}</h3>
          <label htmlFor="reminder-content">{t("Prompt to send")}</label>
          <textarea ref={contentRef} id="reminder-content" className="bp6-input reminder-message" value={content} maxLength={4096} disabled={!mutationAllowed}
            placeholder={t("Message sent to this session when the reminder fires")} onChange={event => { setContent(event.target.value); setConfirmLoad(null); }} />
          <div className="reminder-schedule">
            <div className="reminder-field"><label htmlFor="reminder-delay">{t("Delay")}</label>
              <div className="reminder-delay">
                <input ref={delayRef} id="reminder-delay" className="bp6-input" type="text" maxLength={24} value={delay} aria-invalid={delaySeconds === null} disabled={!mutationAllowed}
                  onChange={event => { setDelay(event.target.value); setConfirmLoad(null); }} />
                <ButtonGroup>{[["5m", 300], ["15m", 900], ["1h", 3600], ["4h", 14400]].map(([label, seconds]) =>
                  <Button key={label} size="small" variant="outlined" active={delaySeconds === seconds} disabled={!mutationAllowed} text={label} onClick={() => { setDelay(String(label)); setConfirmLoad(null); }} />)}</ButtonGroup>
              </div></div>
            <div className="reminder-field"><label htmlFor="reminder-repeat">{t("Attempts")}</label>
              <input ref={repeatRef} id="reminder-repeat" className="bp6-input" type="number" min="1" max="20" step="1" value={repeat} aria-invalid={!validRepeat} disabled={!mutationAllowed}
                onChange={event => { setRepeat(event.target.value); setConfirmLoad(null); }} /></div>
          </div>
          {delaySeconds === null ? <p role="alert" className="reminder-problem">{t("Enter a delay between 1 second and 24 hours, e.g. 5m, 1h 30m, 90s or HH:mm:ss.")}</p>
            : <p className="reminder-hint">{t("Fires every {delay}, {count} time(s).", { delay: formatReminderDelay(delaySeconds), count: validRepeat ? repeat : "?" })}</p>}
          {!validRepeat && <p role="alert" className="reminder-problem">{t("Enter a whole repeat count between 1 and 20.")}</p>}
          <div className="reminder-actions">
            <Button ref={createTrigger} intent="primary" icon={<AppIcon name="reminder" size={15} />} data-epoch={target.epoch} data-session-id={target.sessionId}
              disabled={blocked || !content.trim() || content.length > 4096 || delaySeconds === null || !validRepeat}
              text={t("Create reminder")} title={"Ctrl+Enter"} onClick={() => void create()} />
          </div>
        </section> : <section className="reminder-editor selected-reminder" aria-label={t("Selected reminder")}>
          <header className="reminder-editor-heading"><h3>{t("Selected reminder")}</h3>
            <span className="reminder-state" data-state={row.state}>{stateLabel(row.state)}</span></header>
          <dl className="reminder-facts">
            <div><dt>{t("Attempts")}</dt><dd>{row.firedCount} / {row.repeatCount}</dd></div>
            <div><dt>{t("Delay")}</dt><dd>{formatReminderDelay(row.delaySeconds)}</dd></div>
            <div><dt>{t("Next due")}</dt><dd>{dueText(row.dueAt)}</dd></div>
            <div><dt>{t("Last send exit code")}</dt><dd>{row.lastExitCode ?? t("None")}</dd></div>
          </dl>
          {row.lastError && <Callout compact intent="danger">{t("Last error:")} {row.lastError}</Callout>}
          {!shownDetail && !detailError && <p role="status" className="reminder-hint">{t("Loading full reminder message.")}</p>}
          {detailError && <Callout role="alert" compact intent="warning">{t(detailError)}</Callout>}
          {shownDetail && <>
            <label htmlFor="reminder-edit">{t(row.state === "active" ? "Message" : "Message (completed reminders cannot be edited)")}</label>
            <textarea ref={editorRef} id="reminder-edit" className="bp6-input reminder-message" aria-label={t("Full reminder message")} data-epoch={target.epoch} data-session-id={target.sessionId}
              data-reminder-id={row.id} maxLength={4096} value={editor?.text ?? ""} readOnly={row.state !== "active"}
              disabled={blocked && row.state === "active"} onChange={event => { setEditor(previous => previous && { ...previous, text: event.target.value }); setConfirmDiscardEdit(false); }} />
            <div className="reminder-actions">
              <Button ref={saveTrigger} intent="primary" data-epoch={target.epoch} data-session-id={target.sessionId}
                data-reminder-id={row.id} data-edit-revision={editor?.base.editRevision ?? ""}
                disabled={blocked || row.state !== "active" || !dirtyEdit || !editor?.text.trim() ||
                editor.text.length > 4096 || editor.base.editRevision !== shownDetail.editRevision}
                text={t("Save message")} onClick={() => void save()} />
              {dirtyEdit && (confirmDiscardEdit ? <><span role="alert">{t("Discard unsaved message changes?")}</span>
                <Button size="small" intent="danger" disabled={blocked} text={t("Confirm discard edit")} onClick={() => { setEditor({ base: shownDetail, text: shownDetail.content! }); setConfirmDiscardEdit(false); }} />
                <Button size="small" text={t("Keep edit draft")} onClick={() => setConfirmDiscardEdit(false)} /></>
                : <Button variant="minimal" disabled={blocked} text={t("Discard edit draft")} onClick={() => setConfirmDiscardEdit(true)} />)}
              <span className="reminder-spacer" />
              <Button variant="outlined" icon={<AppIcon name="copy" size={15} />} disabled={blocked} text={t("Use as new reminder")} onClick={() => useAsNew(false)} />
            </div>
            {confirmLoad === row.id && <Callout role="alert" compact intent="warning">{t("The Create form has edits. Discard them to load this reminder without changing its schedule.")}
              <div className="reminder-actions"><Button size="small" intent="danger" disabled={blocked} text={t("Discard draft and use reminder")} onClick={() => useAsNew(true)} />
                <Button size="small" text={t("Keep draft")} onClick={() => setConfirmLoad(null)} /></div></Callout>}
          </>}
          <div className="reminder-actions reminder-danger">
            {confirmation === row.id ? <><span role="alert">{t("Delete this reminder?")}</span>
              <Button size="small" intent="danger" disabled={blocked} text={t("Delete reminder")} onClick={() => void remove()} />
              <Button size="small" text={t("Cancel")} onClick={() => setConfirmation("")} /></>
              : <Button ref={confirmationTrigger} variant="outlined" intent="danger" icon={<AppIcon name="trash" size={15} />} data-epoch={target.epoch} data-session-id={target.sessionId}
                data-reminder-id={row.id} disabled={blocked} text={t("Delete reminder")} onClick={() => setConfirmation(row.id)} />}
          </div>
        </section>}
      </div>}
      <p className="reminder-note">{t("Reminders are kept in memory and are lost when CodeAlta stops.")}</p>
    </>}
  </main>;
}
