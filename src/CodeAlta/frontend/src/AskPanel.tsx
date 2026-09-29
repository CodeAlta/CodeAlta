import { useEffect, useLayoutEffect, useRef, useState, useSyncExternalStore, type RefObject, type KeyboardEvent as ReactKeyboardEvent } from "react";
import { flushSync } from "react-dom";
import { sessionAsks } from "#neoastra";
import { askWireHandle, captureAskAction, type AskHandle, type AskPage, type AskQuestion, type createAskActions } from "./sessionAsks";
import type { createMutationCapability } from "./sessionOperations";
import { showAskDetails } from "./workspacePresentation";
import { useShellLanguage } from "./shellLanguage";
import type { MessageKey } from "./localization";

type Notice = "refresh" | "pending" | "empty" | "failed" | "invalid";
const notices = Object.freeze({
  refresh: "Refresh asks to read the retained backend state.",
  pending: "Answer or cancel the original pending ask.",
  empty: "No pending head reported. Absence is not acknowledgment.",
  failed: "Ask read failed; no action outcome can be inferred.",
  invalid: "The answer is invalid or exceeds the 8,192-character aggregate limit.",
} satisfies Record<Notice, MessageKey>);

type Props = { epoch: string; sessionId: string; actions: ReturnType<typeof createAskActions>; capability: ReturnType<typeof createMutationCapability>; refreshTrigger?: RefObject<(() => void) | null> };
type Draft = { id: number; epoch: string; sessionId: string; source: string; handle: AskHandle;
  questions: readonly AskQuestion[]; text: Record<number, string>; choices: Record<number, number[]>; detached: boolean };
type RetainedAction = ReturnType<ReturnType<typeof createAskActions>["forSession"]>[number];
const maximumDrafts = 8;

// The parsed page is a bounded projection. Include every validated handle and question/option/freeform field,
// not only askId: the same ID can describe a changed question or a later response generation.
function draftSource(epoch: string, sessionId: string, head: NonNullable<AskPage["head"]>): string {
  return JSON.stringify([epoch, sessionId, askWireHandle(head.handle), head.request.questions]);
}

export function AskPanel({ epoch, sessionId, actions, capability, refreshTrigger }: Props) {
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
    if (!canMutate) return;
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
      setDrafts(current => current.map(d => !d.detached && d.epoch === epoch && d.sessionId === sessionId ? { ...d, detached: true } : d));
      setPage(undefined); setNotice("failed");
    } }).finally(() => {
      if (!controller.signal.aborted) timer = setTimeout(() => setRevision(value => value + 1), 1000);
    });
    return () => { clearTimeout(timer); controller.abort(); void observer; };
  }, [epoch, sessionId, revision, actions, capability, scope, canMutate]);
  const page = pageState?.epoch === epoch && pageState.sessionId === sessionId ? pageState.page : undefined;
  const head = page?.head;
  const source = head?.state === "pending" ? draftSource(epoch, sessionId, head) : null;
  const questionIndex = questionSelection?.source === source && head && questionSelection.index < head.request.questions.length
    ? questionSelection.index : 0;
  const active = drafts.find(d => !d.detached && d.epoch === epoch && d.sessionId === sessionId && d.source === source);
  const recovery = drafts.filter(d => d.detached && d.epoch === epoch && d.sessionId === sessionId);
  const blocked = !capability.canMutate() || !head || head.state !== "pending" || actions.blocked(head.handle)
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
  const refresh = () => { const version = ++readVersion.current; setReadPending({ epoch, sessionId, version }); setRevision(value => value + 1); };
  useLayoutEffect(() => {
    if (!refreshTrigger) return;
    refreshTrigger.current = refresh;
    return () => { refreshTrigger.current = null; };
  });
  const visible = recovery.length > 0 || showAskDetails(page, retained.length, notice === "failed", !canMutate);
  if (!visible) return null;
  return <section aria-label={t("Owned asks")}>
    <h3>{t("Pending asks")}</h3>
    {(!page?.head || notice === "failed" || notice === "invalid") && <p role={notice === "failed" || notice === "invalid" ? "alert" : "status"}>{t(notices[notice])}</p>}
    {!canMutate && <p role="alert">{t("Host identity changed. Reload required; retained ask actions cannot be retargeted.")}</p>}
    <p className="detail">{t("Restricted caller-session asks only. Answer starts a new text submission; Cancel does not stop a run. No files, provider input, automatic retry or restart recovery.")}</p>
    {drafts.length >= maximumDrafts && !active && <p className="detail">{t("Local draft limit reached. Confirm discard of a recovery draft before editing another ask.")}</p>}
    {recovery.map(d => <div className="ask-draft-recovery" key={d.id}>
      <p>{t("Local unsubmitted ask draft (read-only). The original ask changed, disappeared or could not be verified; this text cannot be submitted or silently rebound. Component-lifetime only.")}</p>
      <p className="detail">{t("Host {epoch} · session {session} · operation {operation} · runtime {runtime} · attachment {attachment} · provider {provider} · run {run} · ask {ask} · generation {generation}", { epoch: d.epoch, session: d.sessionId, operation: d.handle.operationId, runtime: d.handle.runtimeInstanceId, attachment: d.handle.attachmentGeneration, provider: d.handle.providerId, run: d.handle.runId, ask: d.handle.askId, generation: d.handle.responseGeneration })}</p>
      {d.questions.map((question, index) => <div key={index}>
        <p>{question.title}: {question.question}{question.description && ` — ${question.description}`}</p>
        <p>{t("Selected choices:")} {(d.choices[index] ?? []).map(choice => {
          const option = question.choices[choice];
          return `${choice}: ${option?.title}${option?.description ? ` — ${option.description}` : ""}`;
        }).join(", ") || t("None")}</p>
        {question.freeform && <><p>{question.freeform.title ?? t("Answer")}</p>
          <pre aria-label={t("Unsubmitted answer for {title}", { title: question.title })}>{d.text[index] ?? ""}</pre></>}
      </div>)}
      {discard === d.id ? <><button type="button" onClick={() => { setDrafts(current => current.filter(item => item.id !== d.id)); setDiscard(null); }}>{t("Confirm discard local draft")}</button>
        <button type="button" onClick={() => setDiscard(null)}>{t("Keep local draft")}</button></>
        : <button type="button" onClick={() => setDiscard(d.id)}>{t("Discard local draft…")}</button>}
    </div>)}
    {page?.hasMore && <p>{t("Additional retained asks or dispositions are omitted from this bounded view.")}</p>}
    {head && <fieldset disabled={blocked} onKeyDown={questionChord}>
      <legend>{t("Original ask {id} · {state}", { id: head.handle.askId, state: head.state === "pending" || head.state === "submitting" || head.state === "indeterminate" ? t(head.state) : head.state })}</legend>
      {head.request.questions.length > 1 && <div className="ask-question-navigation" role="group" aria-label={t("Ask question navigation")}>
        <p aria-label={t("Ask question position")}>{t("Question {index} of {count}: {title}", { index: questionIndex + 1, count: head.request.questions.length, title: head.request.questions[questionIndex].title })}</p>
        <p className="detail">{t("Ctrl+N/P moves between questions only while the current question input or these navigation buttons have focus. Browser shortcuts are unchanged elsewhere.")}</p>
        <button type="button" data-ask-direction="-1" disabled={questionIndex === 0} onKeyDown={event => {
          if (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229 || event.repeat) event.preventDefault();
        }} onClick={event => { if (!event.defaultPrevented) navigate(-1, event.currentTarget); }}>{t("Previous question")}</button>
        <button type="button" data-ask-direction="1" disabled={questionIndex === head.request.questions.length - 1} onKeyDown={event => {
          if (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229 || event.repeat) event.preventDefault();
        }} onClick={event => { if (!event.defaultPrevented) navigate(1, event.currentTarget); }}>{t("Next question")}</button>
      </div>}
      {head.request.questions.map((question, index) => index !== questionIndex ? null : <div key={index} data-ask-question={index}>
        <h4>{question.title}</h4><p>{question.question}</p>{question.description && <p>{question.description}</p>}
        {question.choices.map((choice, choiceIndex) => <label key={choiceIndex}>
          <input type="checkbox" checked={(active?.choices[index] ?? []).includes(choiceIndex)} onChange={event => { const checked = event.target.checked; edit(current => ({ ...current,
            choices: { ...current.choices, [index]: checked ? [...(current.choices[index] ?? []), choiceIndex]
              : (current.choices[index] ?? []).filter(v => v !== choiceIndex) } })); }} />
          {choice.title}{choice.description && <span> — {choice.description}</span>}
        </label>)}
        {question.freeform && <label>{question.freeform.title ?? t("Answer")}<textarea maxLength={8192} value={active?.text[index] ?? ""}
          placeholder={question.freeform.placeholder ?? undefined} onChange={event => { const value = event.target.value; edit(current => ({ ...current,
            text: { ...current.text, [index]: value } })); }} /></label>}
      </div>)}
      <button type="button" disabled={reading || !pageUsable} onClick={() => submit("answer")}>{t("Answer original ask")}</button>
      <button type="button" disabled={reading || !pageUsable} onClick={() => submit("cancel")}>{t("Cancel original ask")}</button>
    </fieldset>}
    {page?.latest && <p>{t("Latest backend disposition: {status} · ask {id}. This is not acknowledgment of an earlier transport request.", { status: page.latest.status, id: page.latest.handle.askId })}</p>}
    {retained.map(entry => <div key={entry.request.action.actionId}>
      <p>{t("Original {kind} · ask {id} · transport: {transport}", { kind: t(entry.kind), id: entry.request.action.handle.askId, transport: t(entry.transport) })}{entry.result && ` · ${entry.result.status}`}</p>
      {entry.kind === "answer" && <CapturedAnswer entry={entry} />}
      {entry.observed && <p>{t("Separate backend observation:")} {entry.observed.status}{entry.observed.runId && t(" · run {id}", { id: entry.observed.runId })}</p>}
      <button type="button" onClick={() => { void actions.observeRemote(entry.request.action.actionId, request => sessionAsks.observe({
        expectedHostEpoch: request.expectedHostEpoch, actionId: request.action.actionId, handle: askWireHandle(request.action.handle),
      }, { timeoutMilliseconds: 8000 }), () => { capability.observe({ status: "stale_epoch", epoch }); }); }}>{t("Observe original action")}</button>
    </div>)}
  </section>;
}

function CapturedAnswer({ entry }: { entry: RetainedAction }) {
  const { t } = useShellLanguage();
  const request = entry.request;
  const handle = request.action.handle;
  return <section className="ask-captured-answer" aria-label={t("Captured original ask answer")}>
    <p>{t("Original captured answer (read-only). Transport {transport}; admission is not run completion. Question and choice wording was not captured by this action; indexes below are exact, not inferred from a refreshed ask. This is owner evidence, not a discardable local draft.", { transport: t(entry.transport) })}</p>
    <p className="detail">{t("Host {epoch} · session {session} · operation {operation} · runtime {runtime} · attachment {attachment} · provider {provider} · run {run} · ask {ask} · generation {generation}", { epoch: request.expectedHostEpoch, session: handle.sessionId, operation: handle.operationId, runtime: handle.runtimeInstanceId, attachment: handle.attachmentGeneration, provider: handle.providerId, run: handle.runId, ask: handle.askId, generation: handle.responseGeneration })}{t(" · action {id}", { id: request.action.actionId })}</p>
    {request.action.answers.map(answer => <div key={answer.questionIndex}>
      <p>{t("Question index {index} · selected choice indexes: {choices}", { index: answer.questionIndex, choices: answer.selectedChoiceIndexes.join(", ") || t("None") })}</p>
      {answer.freeformText !== null && <pre aria-label={t("Captured answer for question {index}", { index: answer.questionIndex })}>{answer.freeformText}</pre>}
    </div>)}
  </section>;
}
