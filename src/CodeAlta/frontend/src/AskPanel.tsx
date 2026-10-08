import { useEffect, useLayoutEffect, useRef, useState, useSyncExternalStore, type RefObject, type KeyboardEvent as ReactKeyboardEvent } from "react";
import { createPortal, flushSync } from "react-dom";
import { sessionAsks } from "#neoastra";
import { askWireHandle, captureAskAction, type AskHandle, type AskPage, type AskQuestion, type createAskActions } from "./sessionAsks";
import type { createMutationCapability } from "./sessionOperations";
import { showAskDetails } from "./workspacePresentation";
import { useShellLanguage } from "./shellLanguage";
import type { MessageKey } from "./localization";
import { ObservationStatus } from "./ObservationStatus";
import { Button, Dialog, DialogBody, DialogFooter, TextArea } from "@blueprintjs/core";
import { AppIcon } from "./AppIcon";
import { showToast } from "./appToaster";
import { AskFileReview, type AskFileReviewHandle } from "./AskFileReview";
import { questionTabTitle, selectedChoice, submitStep } from "./askReview";
import { modalDialogOpen } from "./modalDialogs";

type Notice = "refresh" | "pending" | "empty" | "failed" | "invalid";
const notices = Object.freeze({
  invalid: "The answer is invalid or exceeds the 8,192-character aggregate limit.",
} satisfies Partial<Record<Notice, MessageKey>>);

/** What an open ask takes over in its session: nothing, the prompt (questions), or the prompt and the timeline (a file to review). */
export type AskMode = "none" | "questions" | "file";
type Props = { epoch: string; sessionId: string; actions: ReturnType<typeof createAskActions>; capability: ReturnType<typeof createMutationCapability>; refreshTrigger?: RefObject<(() => void) | null>; observing?: boolean;
  /** The session's project, whose folder holds the file an ask gives for review; null for a session without one. */
  projectId?: string | null;
  /** No run of the session is active. An ask is presented once the run that asked has ended. */
  idle?: boolean;
  /** Where the questions go: the place of the prompt. Without it they are shown where this panel is. */
  formSlot?: HTMLElement | null;
  /** Where the file under review goes: the place of the timeline. */
  fileSlot?: HTMLElement | null;
  onMode?: (mode: AskMode) => void };
type Confirmation = "cancel" | "cancelUnsaved" | "submitUnsaved";
type Draft = { id: number; epoch: string; sessionId: string; source: string; handle: AskHandle;
  questions: readonly AskQuestion[]; text: Record<number, string>; choices: Record<number, number[]>; detached: boolean };
type RetainedAction = ReturnType<ReturnType<typeof createAskActions>["forSession"]>[number];
const maximumDrafts = 8;

// The parsed page is a bounded projection. Include every validated handle and question/option/freeform field,
// not only askId: the same ID can describe a changed question or a later response generation.
function draftSource(epoch: string, sessionId: string, head: NonNullable<AskPage["head"]>): string {
  return JSON.stringify([epoch, sessionId, askWireHandle(head.handle), head.request.file, head.request.questions]);
}

/**
 * The asks of a session. A pending ask takes the place of the prompt with its questions, one per tab, and,
 * when it gives a file to review (a plan), the place of the timeline with that file, where lines can be
 * commented and the file edited. Submit sends the answers and the review as the next prompt of the session.
 */
export function AskPanel({ epoch, sessionId, actions, capability, refreshTrigger, observing = true, projectId = null, idle = true, formSlot, fileSlot, onMode }: Props) {
  const { t } = useShellLanguage();
  const [pageState, setPage] = useState<{ epoch: string; sessionId: string; version: number; page: AskPage }>();
  const [readPending, setReadPending] = useState<{ epoch: string; sessionId: string; version: number } | null>(null);
  const [notice, setNotice] = useState<Notice>("refresh");
  const [revision, setRevision] = useState(0);
  const [, repaint] = useState(0);
  const [drafts, setDrafts] = useState<Draft[]>([]);
  const [questionSelection, setQuestionSelection] = useState<{ source: string; index: number } | null>(null);
  const [discard, setDiscard] = useState<number | null>(null);
  const [visited, setVisited] = useState<{ source: string; indexes: ReadonlySet<number> } | null>(null);
  const [presented, setPresented] = useState<string | null>(null);
  const [confirmation, setConfirmation] = useState<Confirmation | null>(null);
  const [saving, setSaving] = useState(false);
  const review = useRef<AskFileReviewHandle | null>(null);
  const form = useRef<HTMLFieldSetElement>(null);
  const nextDraft = useRef(0);
  const readVersion = useRef(0);
  const sourceAuthority = useRef<string | null>(null);
  const scope = JSON.stringify([epoch, sessionId]);
  const previousScope = useRef(scope);
  useEffect(() => {
    if (previousScope.current === scope) return;
    const old = previousScope.current;
    previousScope.current = scope;
    readVersion.current++;
    sourceAuthority.current = null;
    setQuestionSelection(null);
    setPage(undefined);
    setNotice("refresh");
    setDrafts(current => current.map(d => JSON.stringify([d.epoch, d.sessionId]) === old ? { ...d, detached: true } : d));
  }, [scope]);
  const canMutate = useSyncExternalStore(capability.subscribe, capability.canMutate);
  useEffect(() => actions.subscribe(() => repaint(value => value + 1)), [actions]);
  useEffect(() => {
    if (!canMutate || !observing) return;
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    const version = readVersion.current;
    setReadPending({ epoch, sessionId, version });
    // Read only; selection cancellation cannot reach any action or its app-owned original waiter.
    const original = sessionAsks.list({ expectedHostEpoch: epoch, sessionId }, { signal: controller.signal, timeoutMilliseconds: 8000 });
    const observer = original.then(value => {
      const next = actions.readPage(value, epoch, sessionId,
        () => !controller.signal.aborted && version === readVersion.current && scope === previousScope.current,
        () => { capability.observe({ status: "stale_epoch", epoch }); });
      if (!next) return;
      setReadPending(current => current?.epoch === epoch && current.sessionId === sessionId && current.version === version ? null : current);
      const source = next.head?.state === "pending" ? draftSource(epoch, sessionId, next.head) : null;
      if (sourceAuthority.current !== source) setQuestionSelection(null);
      sourceAuthority.current = source; // Fence old DOM handlers before the next page commits.
      setDrafts(current => current.map(d => !d.detached && d.epoch === epoch && d.sessionId === sessionId && d.source !== source
        ? { ...d, detached: true } : d));
      setPage({ epoch, sessionId, version, page: next });
      setNotice(next.head ? "pending" : "empty");
    }).catch(() => { if (!controller.signal.aborted && version === readVersion.current && scope === previousScope.current) {
      setQuestionSelection(null);
      setReadPending(current => current?.epoch === epoch && current.sessionId === sessionId && current.version === version ? null : current);
      sourceAuthority.current = null;
      // Keep the last page/draft through a transient transport failure, but revoke action
      // authority until a successful read validates this exact head again.
      setNotice("failed");
    } }).finally(() => {
      if (!controller.signal.aborted) timer = setTimeout(() => setRevision(value => value + 1), 1000);
    });
    return () => { clearTimeout(timer); controller.abort(); void observer; };
  }, [epoch, sessionId, revision, actions, capability, scope, canMutate, observing]);
  const page = pageState?.epoch === epoch && pageState.sessionId === sessionId ? pageState.page : undefined;
  const head = page?.head;
  const source = head?.state === "pending" ? draftSource(epoch, sessionId, head) : null;
  const questionIndex = questionSelection?.source === source && head && questionSelection.index < head.request.questions.length
    ? questionSelection.index : 0;
  const active = drafts.find(d => !d.detached && d.epoch === epoch && d.sessionId === sessionId && d.source === source);
  const recovery = drafts.filter(d => d.detached && d.epoch === epoch && d.sessionId === sessionId);
  const blocked = notice === "failed" || !capability.canMutate() || !head || head.state !== "pending" || actions.blocked(head.handle)
    || (!active && drafts.length >= maximumDrafts);
  const reading = readPending?.epoch === epoch && readPending.sessionId === sessionId && readPending.version === readVersion.current;
  const pageUsable = pageState?.epoch === epoch && pageState.sessionId === sessionId && pageState.version === readVersion.current;
  const count = head?.request.questions.length ?? 0;
  const file = head?.request.file ?? null;
  // An ask is presented once the run that asked has ended, and stays until it is answered, cancelled or replaced.
  const presentable = !!head && head.state === "pending" && !!source && !actions.blocked(head.handle);
  useEffect(() => {
    if (presentable && source !== presented) { if (idle) setPresented(source); }
    else if (!presentable && presented !== null) setPresented(null);
  }, [presentable, source, presented, idle]);
  // Without a place for the questions (a host that gives none) they are shown where this panel is.
  const inPlace = formSlot === undefined;
  const asking = presentable && (inPlace || (presented === source && !!formSlot));
  const reviewing = asking && !!file && !!fileSlot;
  const mode: AskMode = !asking || inPlace ? "none" : reviewing ? "file" : "questions";
  const modeChanged = useRef(onMode); modeChanged.current = onMode;
  useLayoutEffect(() => { modeChanged.current?.(mode); }, [mode]);
  useEffect(() => () => modeChanged.current?.("none"), []);
  // The questions take the keyboard when they appear, as the prompt they replace had it. Their place is
  // shown by the session a moment after this panel asks for it, and only the session in front has one.
  useEffect(() => {
    if (mode === "none") return;
    let attempts = 12;
    let frame = requestAnimationFrame(function focus() {
      const input = form.current?.querySelector<HTMLElement>("[data-ask-question] input:checked, [data-ask-question] textarea, [data-ask-question] input");
      if (modalDialogOpen()) return;
      if (input && input.offsetParent !== null && !input.matches(":disabled")) {
        // Text being typed elsewhere (another session's prompt, a search field) keeps the keyboard.
        const active = document.activeElement;
        if (!active || active === document.body || active.getBoundingClientRect().width === 0) input.focus();
      } else if (--attempts > 0) frame = requestAnimationFrame(focus);
    });
    return () => cancelAnimationFrame(frame);
  }, [mode, source]);
  const seen = visited?.source === source ? visited.indexes : new Set<number>();
  useEffect(() => {
    if (!asking || !source) return;
    setVisited(current => current?.source === source && current.indexes.has(questionIndex) ? current
      : { source, indexes: new Set([...(current?.source === source ? current.indexes : []), questionIndex]) });
  }, [asking, source, questionIndex]);
  useEffect(() => { setConfirmation(null); }, [source]);

  const show = (next: number, origin: HTMLElement | null): boolean => {
    if (blocked || !head || !source || sourceAuthority.current !== source || scope !== previousScope.current
      || next < 0 || next >= head.request.questions.length || next === questionIndex) return false;
    const fieldset = form.current;
    if (!fieldset?.isConnected) return false;
    const focus = !origin || document.activeElement === origin || fieldset.contains(document.activeElement);
    const version = readVersion.current;
    // Commit the selected question before restoring focus: an input in the old question unmounts.
    // Doing both within this explicit event leaves no deferred callback to steal newer focus.
    flushSync(() => setQuestionSelection({ source, index: next }));
    if (version !== readVersion.current || scope !== previousScope.current || sourceAuthority.current !== source
      || !fieldset.isConnected || !fieldset.querySelector(`[data-ask-question="${next}"]`)) return false;
    if (focus && !modalDialogOpen()) {
      const input = fieldset.querySelector<HTMLElement>(`[data-ask-question="${next}"] input:checked, [data-ask-question="${next}"] textarea`)
        ?? fieldset.querySelector<HTMLElement>(`[data-ask-question="${next}"] input`);
      if (input?.isConnected && !input.matches(":disabled")) input.focus();
    }
    return true;
  };
  const edit = (update: (draft: Draft) => Draft) => {
    if (blocked || !head || !source || sourceAuthority.current !== source) return;
    const captured = head;
    setDrafts(current => {
      const index = current.findIndex(d => !d.detached && d.epoch === epoch && d.sessionId === sessionId && d.source === source);
      if (index < 0 && current.length >= maximumDrafts) return current;
      const base: Draft = index < 0 ? { id: ++nextDraft.current, epoch, sessionId, source, handle: captured.handle,
        questions: captured.request.questions, text: {}, choices: {}, detached: false } : current[index];
      const next = [...current];
      if (index < 0) next.push(update(base)); else next[index] = update(base);
      return next;
    });
  };
  const submit = (kind: "answer" | "cancel") => {
    if (blocked || reading || !pageUsable || !head || !source || sourceAuthority.current !== source
      || draftSource(epoch, sessionId, head) !== source) return;
    try {
      const answers = kind === "cancel" ? [] : head.request.questions.map((item, index) => {
        const choice = selectedChoice(item, active?.choices[index]);
        return { questionIndex: index, selectedChoiceIndexes: choice === null ? [] : [choice], freeformText: active?.text[index] || null };
      });
      const fileReview = kind === "answer" && head.request.file ? review.current?.review() ?? null : null;
      const original = captureAskAction(epoch, head.handle, answers, crypto.randomUUID(), fileReview);
      void actions.submit(kind, original, () => capability.canSubmit({ expectedEpoch: epoch }),
        () => { capability.observe({ status: "stale_epoch", epoch }); });
      // Only a captured owner entry owns a submitted answer. A cancelled ask retains its unsent
      // local text as read-only recovery; neither path acknowledges or removes the owner entry.
      if (actions.get(original.action.actionId)) setDrafts(current => kind === "answer"
        ? current.filter(d => d.id !== active?.id) : current.map(d => d.id === active?.id ? { ...d, detached: true } : d));
    } catch { setNotice("invalid"); }
  };
  // Submit advances before it submits: every question is shown once before the answer leaves.
  const submitOrAdvance = (origin: HTMLElement | null) => {
    if (blocked || !head) return;
    const step = submitStep(seen, questionIndex, count);
    if (step.kind === "show") { show(step.index, origin); return; }
    if (review.current && !review.current.fits()) { setNotice("invalid"); return; }
    if (review.current?.dirty()) setConfirmation("submitUnsaved"); else submit("answer");
  };
  const requestCancel = () => { if (!blocked && !reading && pageUsable) setConfirmation(review.current?.dirty() ? "cancelUnsaved" : "cancel"); };
  const confirm = async (kind: "answer" | "cancel", save: boolean) => {
    if (save) {
      setSaving(true);
      const written = await review.current?.save().finally(() => setSaving(false));
      // A file that could not be saved keeps the ask open: the editor says why.
      if (!written) { setConfirmation(null); return; }
    }
    setConfirmation(null);
    submit(kind);
  };
  const formKey = (event: ReactKeyboardEvent<HTMLFieldSetElement>) => {
    if (event.defaultPrevented || event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229 || event.altKey || event.metaKey || !head) return;
    const target = event.target;
    if (!(target instanceof HTMLElement) || target.matches(":disabled")) return;
    const key = event.key.toLowerCase();
    const text = target instanceof HTMLTextAreaElement || (target instanceof HTMLInputElement && target.type !== "radio");
    const handled = () => { event.preventDefault(); event.stopPropagation(); };
    const options = head.request.questions[questionIndex]?.choices.length ?? 0;
    if (!text && !event.ctrlKey && !event.shiftKey && /^[1-9]$/.test(event.key) && Number(event.key) <= options && !target.closest('[role="tablist"]')) {
      const value = Number(event.key) - 1;
      edit(current => ({ ...current, choices: { ...current.choices, [questionIndex]: [value] } }));
      form.current?.querySelector<HTMLElement>(`[data-ask-question] input[value="${value}"]`)?.focus();
      handled();
    } else if (event.ctrlKey && !event.shiftKey && (key === "n" || key === "p")) {
      if (!event.repeat) show(questionIndex + (key === "n" ? 1 : -1), target);
      handled();
    } else if (!event.ctrlKey && !event.shiftKey && !text && (event.key === "ArrowLeft" || event.key === "ArrowRight")) {
      // Left and Right change the question; Up and Down stay with the choices.
      if (target.closest('[role="tablist"]')) return;
      show(questionIndex + (event.key === "ArrowRight" ? 1 : -1), target);
      handled();
    } else if (event.key === "Enter" && !event.ctrlKey && !event.shiftKey && !(target instanceof HTMLButtonElement) && !target.closest('[role="tablist"]')) {
      if (!event.repeat) submitOrAdvance(target);
      handled();
    } else if (event.key === "Escape" && !event.ctrlKey && !event.shiftKey) {
      requestCancel();
      handled();
    }
  };
  const retained = actions.forSession(sessionId).filter(entry => entry.request.expectedHostEpoch === epoch);
  // An action that went through needs no row: the ask leaves the panel and its answer is in the timeline.
  const unsettled = retained.filter(entry => entry.transport === "uncertain"
    || (entry.transport === "settled" && entry.result?.status !== "admitted" && entry.result?.status !== "cancelled"));
  const refresh = () => { const version = ++readVersion.current; setReadPending({ epoch, sessionId, version }); setRevision(value => value + 1); };
  useLayoutEffect(() => {
    if (!refreshTrigger) return;
    refreshTrigger.current = refresh;
    return () => { refreshTrigger.current = null; };
  });
  useEffect(() => {
    if (notice !== "invalid") return;
    showToast({ message: t(notices.invalid), intent: "danger", icon: "error", timeout: 8000 });
    setNotice(head ? "pending" : "empty");
  }, [notice, head, t]);
  const question = head?.request.questions[questionIndex];
  const choice = question ? selectedChoice(question, active?.choices[questionIndex]) : null;
  const remaining = submitStep(seen, questionIndex, count).kind === "show";
  const choose = (value: number) => edit(current => ({ ...current, choices: { ...current.choices, [questionIndex]: [value] } }));
  const questions = asking && head && question ? <fieldset className="ask-card ask-form" ref={form} disabled={blocked} onKeyDown={formKey} data-ask-keys="">
    <legend className="sr-only">{t("Original ask {id} · {state}", { id: head.handle.askId, state: t("pending") })}</legend>
    <header className="ask-head">
      <span className="ask-badge"><AppIcon name="ask" size={14} />{t(count === 1 ? "A question for you" : "Questions for you")}</span>
      {count > 1 && <ol className="ask-steps" role="tablist" aria-label={t("Questions")} onKeyDown={event => {
        if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") return;
        show(questionIndex + (event.key === "ArrowRight" ? 1 : -1), event.target instanceof HTMLElement ? event.target : null);
        event.preventDefault(); event.stopPropagation();
      }}>
        {head.request.questions.map((item, index) => <li key={index}>
          <button type="button" role="tab" aria-selected={index === questionIndex} aria-label={questionTabTitle(item.title, index, count)} title={item.title} disabled={blocked}
            tabIndex={index === questionIndex ? 0 : -1} data-state={index === questionIndex ? "current" : seen.has(index) ? "seen" : "ahead"}
            onClick={event => { show(index, event.currentTarget); }}>
            <span className="ask-step-mark">{index !== questionIndex && seen.has(index) ? <AppIcon name="check" size={12} /> : index + 1}</span>
            <span className="ask-step-title">{item.title}</span>
          </button></li>)}
      </ol>}
    </header>
    <div className="ask-question" data-ask-question={questionIndex} role="group" aria-label={question.title}>
      <h4>{question.question}</h4>{question.description && <p className="detail">{question.description}</p>}
      {question.choices.length > 0 && <div className="ask-choices" role="radiogroup" aria-label={question.title}>
        {question.choices.map((item, choiceIndex) => <label key={choiceIndex} className="ask-choice" data-selected={choice === choiceIndex || undefined}>
          <input type="radio" className="sr-only" name={`ask-${head.handle.askId}-${questionIndex}`} value={choiceIndex} checked={choice === choiceIndex} disabled={blocked}
            onChange={() => choose(choiceIndex)} />
          <span className="ask-choice-number" aria-hidden="true">{choiceIndex + 1}</span>
          <span className="ask-choice-text"><strong>{item.title}</strong>{item.description && <small>{item.description}</small>}</span>
          <span className="ask-choice-check" aria-hidden="true"><AppIcon name="check" size={14} /></span>
        </label>)}
      </div>}
      {question.freeform && <label className="ask-freeform"><span>{question.freeform.title ?? t("Answer")}</span><TextArea fill autoResize rows={1} maxLength={8192} aria-label={question.freeform.title ?? t("Answer")} value={active?.text[questionIndex] ?? ""}
        placeholder={question.freeform.placeholder ?? undefined} onChange={event => { const value = event.target.value; edit(current => ({ ...current,
          text: { ...current.text, [questionIndex]: value } })); }} /></label>}
    </div>
    <footer className="ask-actions">
      <small className="ask-hint"><kbd>Enter</kbd>{t(remaining ? "next" : "send")}{question.choices.length > 1 && <><kbd>1</kbd>–<kbd>{Math.min(question.choices.length, 9)}</kbd>{t("choose")}</>}<kbd>Esc</kbd>{t("cancel")}</small>
      {reviewing && <Button size="small" variant="minimal" icon={<AppIcon name="fileText" size={14} />} text={t("Review file")} title="Ctrl+G Ctrl+E" onClick={() => review.current?.focus()} />}
      <Button size="small" variant="minimal" disabled={reading || !pageUsable} text={t("Cancel")} onClick={requestCancel} />
      {count > 1 && <Button size="small" variant="outlined" disabled={questionIndex === 0} icon={<AppIcon name="chevronLeft" size={14} />} text={t("Back")} onClick={event => { show(questionIndex - 1, event.currentTarget); }} />}
      <Button className="ask-answer" intent="primary" size="small" disabled={reading || !pageUsable} text={t(remaining ? "Next" : "Submit")}
        endIcon={<AppIcon name={remaining ? "chevronRight" : "send"} size={14} />} onClick={event => submitOrAdvance(event.currentTarget)} />
    </footer>
  </fieldset> : null;
  const dialog = confirmation && <Dialog isOpen className="ask-confirmation" title={t(confirmation === "submitUnsaved" ? "Submit Ask" : "Cancel Ask")}
    isCloseButtonShown={false} canOutsideClickClose={false} onClose={() => setConfirmation(null)}>
    <DialogBody>{confirmation === "cancel" ? <><p>{t("Exit ask mode without sending a response?")}</p>
      <p className="detail">{t("The queued ask will be canceled and the session will return to the normal prompt editor.")}</p></>
      : t(confirmation === "submitUnsaved" ? "The attached file has unsaved edits. Save them before submitting the ask response?"
        : "The attached file has unsaved edits. Save them before exiting ask mode?")}</DialogBody>
    <DialogFooter actions={confirmation === "cancel" ? <>
      <Button disabled={saving} autoFocus text={t("Keep answering")} onClick={() => setConfirmation(null)} />
      <Button intent="danger" text={t("Exit without responding")} onClick={() => void confirm("cancel", false)} />
    </> : <>
      <Button disabled={saving} text={t("Keep answering")} onClick={() => setConfirmation(null)} />
      <Button intent="danger" disabled={saving} text={t(confirmation === "submitUnsaved" ? "Submit without saving" : "Exit without saving")}
        onClick={() => void confirm(confirmation === "submitUnsaved" ? "answer" : "cancel", false)} />
      <Button intent="primary" loading={saving} autoFocus text={t(confirmation === "submitUnsaved" ? "Save and submit" : "Save and exit")}
        onClick={() => void confirm(confirmation === "submitUnsaved" ? "answer" : "cancel", true)} />
    </>} />
  </Dialog>;
  const visible = recovery.length > 0 || unsettled.length > 0 || (inPlace && showAskDetails(page, unsettled.length));
  return <>
    {!inPlace && questions && formSlot && createPortal(questions, formSlot)}
    {reviewing && file && fileSlot && createPortal(<AskFileReview key={source} epoch={epoch} projectId={projectId} path={file.path} disabled={blocked}
      handle={review} onLeave={() => form.current?.querySelector<HTMLElement>("[data-ask-question] input:checked, [data-ask-question] textarea, [data-ask-question] input")?.focus()} />, fileSlot)}
    {dialog}
    {/* This component lives in the timeline. A failed read is an error, not evidence of an ask. */}
    {visible && <section className="ask-panel" aria-label={t("Owned asks")}>
      <h3 className="sr-only">{t(head?.state === "pending" ? "Pending asks" : "Owned asks")}</h3>
      <ObservationStatus unavailable={notice === "failed"} />
      {recovery.map(d => <div className="ask-draft-recovery" key={d.id}>
        <header><AppIcon name="edit" size={14} /><strong>{t("Unsent answer")}</strong>
          {discard === d.id ? <>
            <Button size="small" intent="danger" text={t("Discard")} onClick={() => { setDrafts(current => current.filter(item => item.id !== d.id)); setDiscard(null); }} />
            <Button size="small" variant="minimal" text={t("Keep")} onClick={() => setDiscard(null)} /></>
            : <Button size="small" variant="minimal" icon={<AppIcon name="trash" size={14} />} title={t("Discard")} aria-label={t("Discard")} onClick={() => setDiscard(d.id)} />}
        </header>
        {d.questions.map((item, index) => {
          const chosen = (d.choices[index] ?? []).map(value => item.choices[value]?.title).filter(Boolean);
          const text = d.text[index];
          return chosen.length || text ? <div key={index}>
            <p><strong>{item.title}</strong>{chosen.length > 0 && ` · ${chosen.join(", ")}`}</p>
            {text && <pre aria-label={t("Unsubmitted answer for {title}", { title: item.title })}>{text}</pre>}
          </div> : null;
        })}
      </div>)}
      {inPlace && questions}
      {unsettled.map(entry => <div className="ask-unsettled" key={entry.request.action.actionId} role="status">
        <AppIcon name="error" size={14} />
        <span>{t(entry.kind === "answer" ? "Answer not confirmed" : "Cancel not confirmed")}{(entry.observed ?? entry.result) && ` · ${(entry.observed ?? entry.result)!.status}`}</span>
        <Button size="small" variant="minimal" icon={<AppIcon name="refresh" size={14} />} text={t("Check")} onClick={() => { void actions.observeRemote(entry.request.action.actionId, request => sessionAsks.observe({
          expectedHostEpoch: request.expectedHostEpoch, actionId: request.action.actionId, handle: askWireHandle(request.action.handle),
        }, { timeoutMilliseconds: 8000 }), () => { capability.observe({ status: "stale_epoch", epoch }); }); }} />
      </div>)}
    </section>}
  </>;
}
