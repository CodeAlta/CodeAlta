import { useEffect, useLayoutEffect, useRef, useState, useSyncExternalStore, type RefObject, type KeyboardEvent as ReactKeyboardEvent } from "react";
import { flushSync } from "react-dom";
import { sessionAsks } from "#neoastra";
import { askWireHandle, captureAskAction, type AskHandle, type AskPage, type AskQuestion, type createAskActions } from "./sessionAsks";
import type { createMutationCapability } from "./sessionOperations";
import { showAskDetails } from "./workspacePresentation";
import { useShellLanguage } from "./shellLanguage";
import type { MessageKey } from "./localization";
import { ObservationStatus } from "./ObservationStatus";
import { Button, Checkbox, TextArea } from "@blueprintjs/core";
import { AppIcon } from "./AppIcon";
import { showToast } from "./appToaster";

type Notice = "refresh" | "pending" | "empty" | "failed" | "invalid";
const notices = Object.freeze({
  invalid: "The answer is invalid or exceeds the 8,192-character aggregate limit.",
} satisfies Partial<Record<Notice, MessageKey>>);

type Props = { epoch: string; sessionId: string; actions: ReturnType<typeof createAskActions>; capability: ReturnType<typeof createMutationCapability>; refreshTrigger?: RefObject<(() => void) | null>; observing?: boolean };
type Draft = { id: number; epoch: string; sessionId: string; source: string; handle: AskHandle;
  questions: readonly AskQuestion[]; text: Record<number, string>; choices: Record<number, number[]>; detached: boolean };
type RetainedAction = ReturnType<ReturnType<typeof createAskActions>["forSession"]>[number];
const maximumDrafts = 8;

// The parsed page is a bounded projection. Include every validated handle and question/option/freeform field,
// not only askId: the same ID can describe a changed question or a later response generation.
function draftSource(epoch: string, sessionId: string, head: NonNullable<AskPage["head"]>): string {
  return JSON.stringify([epoch, sessionId, askWireHandle(head.handle), head.request.questions]);
}

export function AskPanel({ epoch, sessionId, actions, capability, refreshTrigger, observing = true }: Props) {
  const { t } = useShellLanguage();
  const [pageState, setPage] = useState<{ epoch: string; sessionId: string; version: number; page: AskPage }>();
  const [readPending, setReadPending] = useState<{ epoch: string; sessionId: string; version: number } | null>(null);
  const [notice, setNotice] = useState<Notice>("refresh");
  const [revision, setRevision] = useState(0);
  const [, repaint] = useState(0);
  const [drafts, setDrafts] = useState<Draft[]>([]);
  const [questionSelection, setQuestionSelection] = useState<{ source: string; index: number } | null>(null);
  const [discard, setDiscard] = useState<number | null>(null);
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
  const navigate = (direction: -1 | 1, origin: HTMLElement): boolean => {
    if (blocked || !head || !source || sourceAuthority.current !== source || scope !== previousScope.current
      || document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) return false;
    const next = Math.max(0, Math.min(questionIndex + direction, head.request.questions.length - 1));
    if (next === questionIndex) return false;
    const fieldset = origin.closest("fieldset");
    if (!fieldset?.isConnected) return false;
    const focus = document.activeElement === origin;
    const version = readVersion.current;
    // Commit the selected question before restoring focus: an input in the old question unmounts.
    // Doing both within this explicit event leaves no deferred callback to steal newer focus.
    flushSync(() => setQuestionSelection({ source, index: next }));
    if (version !== readVersion.current || scope !== previousScope.current || sourceAuthority.current !== source
      || !fieldset.isConnected || !fieldset.querySelector(`[data-ask-question="${next}"]`)) return false;
    if (focus && document.activeElement === (origin.isConnected ? origin : document.body)
      && !document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) {
      const input = fieldset.querySelector<HTMLElement>(`[data-ask-question="${next}"] input, [data-ask-question="${next}"] textarea`);
      if (input?.isConnected && !input.matches(":disabled")) input.focus();
    }
    return true;
  };
  const questionChord = (event: ReactKeyboardEvent<HTMLFieldSetElement>) => {
    if (event.defaultPrevented || event.repeat || event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229
      || !event.ctrlKey || event.altKey || event.metaKey || event.shiftKey) return;
    const direction = event.key.toLowerCase() === "n" ? 1 : event.key.toLowerCase() === "p" ? -1 : null;
    if (!direction || !head || head.request.questions.length < 2) return;
    const target = event.target;
    if (!(target instanceof HTMLElement) || document.activeElement !== target || target.matches(":disabled")) return;
    const question = event.currentTarget.querySelector(`[data-ask-question="${questionIndex}"]`);
    const questionInput = question?.contains(target) && (target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement);
    const navigationButton = target instanceof HTMLButtonElement && target.closest('.ask-question-navigation')
      && event.currentTarget.contains(target);
    if (!questionInput && !navigationButton) return;
    const button = event.currentTarget.querySelector<HTMLButtonElement>(`[data-ask-direction="${direction}"]`);
    if (!button?.isConnected || button.matches(":disabled") || !navigate(direction, target)) return;
    event.preventDefault(); event.stopPropagation();
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
      const answers = kind === "cancel" ? [] : head.request.questions.map((_, index) => ({ questionIndex: index,
        selectedChoiceIndexes: active?.choices[index] ?? [], freeformText: active?.text[index] || null }));
      const original = captureAskAction(epoch, head.handle, answers, crypto.randomUUID());
      void actions.submit(kind, original, () => capability.canSubmit({ expectedEpoch: epoch }),
        () => { capability.observe({ status: "stale_epoch", epoch }); });
      // Only a captured owner entry owns a submitted answer. A cancelled ask retains its unsent
      // local text as read-only recovery; neither path acknowledges or removes the owner entry.
      if (actions.get(original.action.actionId)) setDrafts(current => kind === "answer"
        ? current.filter(d => d.id !== active?.id) : current.map(d => d.id === active?.id ? { ...d, detached: true } : d));
    } catch { setNotice("invalid"); }
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
  const visible = recovery.length > 0 || showAskDetails(page, unsettled.length);
  // This component lives in the timeline. A failed read is an error, not evidence of an ask.
  if (!visible) return null;
  const question = head?.request.questions[questionIndex];
  const count = head?.request.questions.length ?? 0;
  const guard = (event: ReactKeyboardEvent<HTMLButtonElement>) => {
    if (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229 || event.repeat) event.preventDefault();
  };
  return <section className="ask-panel" aria-label={t("Owned asks")}>
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
        const chosen = (d.choices[index] ?? []).map(choice => item.choices[choice]?.title).filter(Boolean);
        const text = d.text[index];
        return chosen.length || text ? <div key={index}>
          <p><strong>{item.title}</strong>{chosen.length > 0 && ` · ${chosen.join(", ")}`}</p>
          {text && <pre aria-label={t("Unsubmitted answer for {title}", { title: item.title })}>{text}</pre>}
        </div> : null;
      })}
    </div>)}
    {head && question && <fieldset className="ask-card" disabled={blocked} onKeyDown={questionChord}>
      <legend className="sr-only">{t("Original ask {id} · {state}", { id: head.handle.askId, state: head.state === "pending" || head.state === "submitting" || head.state === "indeterminate" ? t(head.state) : head.state })}</legend>
      <header className="ask-card-header">
        <AppIcon name="ask" size={15} />
        <h4>{question.title}</h4>
        {count > 1 && <span className="ask-question-navigation" role="group" aria-label={t("Ask question navigation")}>
          <small aria-label={t("Ask question position")} title={t("Question {index} of {count}: {title}", { index: questionIndex + 1, count, title: question.title })}>{questionIndex + 1} / {count}</small>
          <Button size="small" variant="minimal" data-ask-direction="-1" disabled={questionIndex === 0} icon={<AppIcon name="chevronLeft" size={15} />}
            title={`${t("Previous question")} (Ctrl+P)`} aria-label={t("Previous question")} onKeyDown={guard}
            onClick={event => { if (!event.defaultPrevented) navigate(-1, event.currentTarget); }} />
          <Button size="small" variant="minimal" data-ask-direction="1" disabled={questionIndex === count - 1} icon={<AppIcon name="chevronRight" size={15} />}
            title={`${t("Next question")} (Ctrl+N)`} aria-label={t("Next question")} onKeyDown={guard}
            onClick={event => { if (!event.defaultPrevented) navigate(1, event.currentTarget); }} />
        </span>}
      </header>
      <div className="ask-question" data-ask-question={questionIndex}>
        <p>{question.question}</p>{question.description && <p className="detail">{question.description}</p>}
        {question.choices.length > 0 && <div className="ask-choices">{question.choices.map((choice, choiceIndex) => <Checkbox key={choiceIndex}
          checked={(active?.choices[questionIndex] ?? []).includes(choiceIndex)} onChange={event => { const checked = event.currentTarget.checked; edit(current => ({ ...current,
            choices: { ...current.choices, [questionIndex]: checked ? [...(current.choices[questionIndex] ?? []), choiceIndex]
              : (current.choices[questionIndex] ?? []).filter(v => v !== choiceIndex) } })); }}>
          {choice.title}{choice.description && <span className="detail"> — {choice.description}</span>}
        </Checkbox>)}</div>}
        {question.freeform && <label className="ask-freeform">{question.freeform.title}<TextArea fill autoResize maxLength={8192} aria-label={question.freeform.title ?? t("Answer")} value={active?.text[questionIndex] ?? ""}
          placeholder={question.freeform.placeholder ?? undefined} onChange={event => { const value = event.target.value; edit(current => ({ ...current,
            text: { ...current.text, [questionIndex]: value } })); }} /></label>}
      </div>
      <Button className="ask-answer" intent="primary" size="small" disabled={reading || !pageUsable} text={t("Answer")} onClick={() => submit("answer")} />
      <Button size="small" variant="minimal" disabled={reading || !pageUsable} text={t("Cancel")} onClick={() => submit("cancel")} />
    </fieldset>}
    {unsettled.map(entry => <div className="ask-unsettled" key={entry.request.action.actionId} role="status">
      <AppIcon name="error" size={14} />
      <span>{t(entry.kind === "answer" ? "Answer not confirmed" : "Cancel not confirmed")}{(entry.observed ?? entry.result) && ` · ${(entry.observed ?? entry.result)!.status}`}</span>
      <Button size="small" variant="minimal" icon={<AppIcon name="refresh" size={14} />} text={t("Check")} onClick={() => { void actions.observeRemote(entry.request.action.actionId, request => sessionAsks.observe({
        expectedHostEpoch: request.expectedHostEpoch, actionId: request.action.actionId, handle: askWireHandle(request.action.handle),
      }, { timeoutMilliseconds: 8000 }), () => { capability.observe({ status: "stale_epoch", epoch }); }); }} />
    </div>)}
  </section>;
}
