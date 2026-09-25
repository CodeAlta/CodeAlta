import { useEffect, useRef, useState, useSyncExternalStore, type MouseEvent as ReactMouseEvent } from "react";
import { sessionAsks } from "#neoastra";
import { askWireHandle, captureAskAction, type AskHandle, type AskPage, type AskQuestion, type createAskActions } from "./sessionAsks";
import type { createMutationCapability } from "./sessionOperations";
import { showAskDetails } from "./workspacePresentation";

type Props = { epoch: string; sessionId: string; actions: ReturnType<typeof createAskActions>; capability: ReturnType<typeof createMutationCapability> };
type Draft = { id: number; epoch: string; sessionId: string; source: string; handle: AskHandle;
  questions: readonly AskQuestion[]; text: Record<number, string>; choices: Record<number, number[]>; detached: boolean };
type RetainedAction = ReturnType<ReturnType<typeof createAskActions>["forSession"]>[number];
const maximumDrafts = 8;

// The parsed page is a bounded projection. Include every validated handle and question/option/freeform field,
// not only askId: the same ID can describe a changed question or a later response generation.
function draftSource(epoch: string, sessionId: string, head: NonNullable<AskPage["head"]>): string {
  return JSON.stringify([epoch, sessionId, askWireHandle(head.handle), head.request.questions]);
}

export function AskPanel({ epoch, sessionId, actions, capability }: Props) {
  const [pageState, setPage] = useState<{ epoch: string; sessionId: string; version: number; page: AskPage }>();
  const [readPending, setReadPending] = useState<{ epoch: string; sessionId: string; version: number } | null>(null);
  const [notice, setNotice] = useState("Refresh asks to read the retained backend state.");
  const [revision, setRevision] = useState(0);
  const [, repaint] = useState(0);
  const [drafts, setDrafts] = useState<Draft[]>([]);
  const [questionSelection, setQuestionSelection] = useState<{ source: string; index: number } | null>(null);
  const [discard, setDiscard] = useState<number | null>(null);
  const nextDraft = useRef(0);
  const readVersion = useRef(0);
  const sourceAuthority = useRef<string | null>(null);
  const focusGeneration = useRef(0);
  const scope = JSON.stringify([epoch, sessionId]);
  const previousScope = useRef(scope);
  useEffect(() => {
    if (previousScope.current === scope) return;
    const old = previousScope.current;
    previousScope.current = scope;
    readVersion.current++;
    focusGeneration.current++;
    sourceAuthority.current = null;
    setQuestionSelection(null);
    setPage(undefined);
    setNotice("Refresh asks to read the retained backend state.");
    setDrafts(current => current.map(d => JSON.stringify([d.epoch, d.sessionId]) === old ? { ...d, detached: true } : d));
  }, [scope]);
  const canMutate = useSyncExternalStore(capability.subscribe, capability.canMutate);
  useEffect(() => actions.subscribe(() => repaint(value => value + 1)), [actions]);
  useEffect(() => {
    const controller = new AbortController();
    const version = readVersion.current;
    setReadPending({ epoch, sessionId, version });
    // Read only; selection cancellation cannot reach any action or its app-owned original waiter.
    const original = sessionAsks.list({ expectedHostEpoch: epoch, sessionId }, { signal: controller.signal, timeoutMilliseconds: 8000 });
    const observer = original.then(value => {
      const next = actions.readPage(value, epoch, sessionId,
        () => !controller.signal.aborted && version === readVersion.current && scope === previousScope.current,
        () => { capability.observe({ status: "stale_epoch", epoch }); });
      if (!next) return;
      focusGeneration.current++;
      setReadPending(current => current?.epoch === epoch && current.sessionId === sessionId && current.version === version ? null : current);
      const source = next.head?.state === "pending" ? draftSource(epoch, sessionId, next.head) : null;
      if (sourceAuthority.current !== source) setQuestionSelection(null);
      sourceAuthority.current = source; // Fence old DOM handlers before the next page commits.
      setDrafts(current => current.map(d => !d.detached && d.epoch === epoch && d.sessionId === sessionId && d.source !== source
        ? { ...d, detached: true } : d));
      setPage({ epoch, sessionId, version, page: next });
      setNotice(next.head ? "Answer or cancel the original pending ask." : "No pending head reported. Absence is not acknowledgment.");
    }).catch(() => { if (!controller.signal.aborted && version === readVersion.current && scope === previousScope.current) {
      focusGeneration.current++;
      setQuestionSelection(null);
      setReadPending(current => current?.epoch === epoch && current.sessionId === sessionId && current.version === version ? null : current);
      sourceAuthority.current = null;
      setDrafts(current => current.map(d => !d.detached && d.epoch === epoch && d.sessionId === sessionId ? { ...d, detached: true } : d));
      setPage(undefined); setNotice("Ask read failed; no action outcome can be inferred.");
    } });
    return () => { focusGeneration.current++; controller.abort(); void observer; };
  }, [epoch, sessionId, revision, actions, capability, scope]);
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
  const navigate = (direction: -1 | 1, event: ReactMouseEvent<HTMLButtonElement>) => {
    if (blocked || !head || !source || sourceAuthority.current !== source || event.defaultPrevented) return;
    const next = Math.max(0, Math.min(questionIndex + direction, head.request.questions.length - 1));
    if (next === questionIndex) return;
    const button = event.currentTarget;
    const focus = document.activeElement === button;
    const version = readVersion.current;
    const generation = ++focusGeneration.current;
    setQuestionSelection({ source, index: next });
    if (!focus) return;
    requestAnimationFrame(() => {
      if (generation !== focusGeneration.current || version !== readVersion.current || scope !== previousScope.current
        || sourceAuthority.current !== source || !button.isConnected || document.activeElement !== button
        || document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) return;
      const input = button.closest("fieldset")?.querySelector<HTMLElement>(`[data-ask-question="${next}"] input, [data-ask-question="${next}"] textarea`);
      if (input?.isConnected && !input.matches(":disabled")) input.focus();
    });
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
    } catch { setNotice("The answer is invalid or exceeds the 8,192-character aggregate limit."); }
  };
  const retained = actions.forSession(sessionId).filter(entry => entry.request.expectedHostEpoch === epoch);
  const refresh = () => { focusGeneration.current++; const version = ++readVersion.current; setReadPending({ epoch, sessionId, version }); setRevision(value => value + 1); };
  const visible = recovery.length > 0 || showAskDetails(page, retained.length, notice.startsWith("Ask read failed"), !canMutate);
  if (!visible) return <button type="button" className="ask-refresh" onClick={refresh}>Check asks</button>;
  return <section aria-label="Owned asks">
    <h3>Pending asks</h3>
    {(!page?.head || notice.startsWith("Ask read failed") || notice.startsWith("The answer is invalid")) && <p role={notice.startsWith("Ask read failed") || notice.startsWith("The answer is invalid") ? "alert" : "status"}>{notice}</p>}
    {!canMutate && <p role="alert">Host identity changed. Reload required; retained ask actions cannot be retargeted.</p>}
    <p className="detail">Restricted caller-session asks only. Answer starts a new text submission; Cancel does not stop a run. No files, provider input, automatic retry or restart recovery.</p>
    <button type="button" onClick={refresh}>Refresh asks</button>
    {reading && <p className="detail" role="status">Ask refresh pending; answer and cancel are unavailable until this read settles.</p>}
    {drafts.length >= maximumDrafts && !active && <p className="detail">Local draft limit reached. Confirm discard of a recovery draft before editing another ask.</p>}
    {recovery.map(d => <div className="ask-draft-recovery" key={d.id}>
      <p>Local unsubmitted ask draft (read-only). The original ask changed, disappeared or could not be verified; this text cannot be submitted or silently rebound. Component-lifetime only.</p>
      <p className="detail">Host {d.epoch} · session {d.sessionId} · operation {d.handle.operationId} · runtime {d.handle.runtimeInstanceId} · attachment {d.handle.attachmentGeneration} · provider {d.handle.providerId} · run {d.handle.runId} · ask {d.handle.askId} · generation {d.handle.responseGeneration}</p>
      {d.questions.map((question, index) => <div key={index}>
        <p>{question.title}: {question.question}{question.description && ` — ${question.description}`}</p>
        <p>Selected choices: {(d.choices[index] ?? []).map(choice => {
          const option = question.choices[choice];
          return `${choice}: ${option?.title}${option?.description ? ` — ${option.description}` : ""}`;
        }).join(", ") || "None"}</p>
        {question.freeform && <><p>{question.freeform.title ?? "Answer"}</p>
          <pre aria-label={`Unsubmitted answer for ${question.title}`}>{d.text[index] ?? ""}</pre></>}
      </div>)}
      {discard === d.id ? <><button type="button" onClick={() => { setDrafts(current => current.filter(item => item.id !== d.id)); setDiscard(null); }}>Confirm discard local draft</button>
        <button type="button" onClick={() => setDiscard(null)}>Keep local draft</button></>
        : <button type="button" onClick={() => setDiscard(d.id)}>Discard local draft…</button>}
    </div>)}
    {page?.hasMore && <p>Additional retained asks or dispositions are omitted from this bounded view.</p>}
    {head && <fieldset disabled={blocked}>
      <legend>Original ask {head.handle.askId} · {head.state}</legend>
      {head.request.questions.length > 1 && <div className="ask-question-navigation" role="group" aria-label="Ask question navigation">
        <p aria-label="Ask question position">Question {questionIndex + 1} of {head.request.questions.length}: {head.request.questions[questionIndex].title}</p>
        <button type="button" disabled={questionIndex === 0} onKeyDown={event => {
          if (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229 || event.repeat) event.preventDefault();
        }} onClick={event => navigate(-1, event)}>Previous question</button>
        <button type="button" disabled={questionIndex === head.request.questions.length - 1} onKeyDown={event => {
          if (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229 || event.repeat) event.preventDefault();
        }} onClick={event => navigate(1, event)}>Next question</button>
      </div>}
      {head.request.questions.map((question, index) => index !== questionIndex ? null : <div key={index} data-ask-question={index}>
        <h4>{question.title}</h4><p>{question.question}</p>{question.description && <p>{question.description}</p>}
        {question.choices.map((choice, choiceIndex) => <label key={choiceIndex}>
          <input type="checkbox" checked={(active?.choices[index] ?? []).includes(choiceIndex)} onChange={event => { const checked = event.target.checked; edit(current => ({ ...current,
            choices: { ...current.choices, [index]: checked ? [...(current.choices[index] ?? []), choiceIndex]
              : (current.choices[index] ?? []).filter(v => v !== choiceIndex) } })); }} />
          {choice.title}{choice.description && <span> — {choice.description}</span>}
        </label>)}
        {question.freeform && <label>{question.freeform.title ?? "Answer"}<textarea maxLength={8192} value={active?.text[index] ?? ""}
          placeholder={question.freeform.placeholder ?? undefined} onChange={event => { const value = event.target.value; edit(current => ({ ...current,
            text: { ...current.text, [index]: value } })); }} /></label>}
      </div>)}
      <button type="button" disabled={reading || !pageUsable} onClick={() => submit("answer")}>Answer original ask</button>
      <button type="button" disabled={reading || !pageUsable} onClick={() => submit("cancel")}>Cancel original ask</button>
    </fieldset>}
    {page?.latest && <p>Latest backend disposition: {page.latest.status} · ask {page.latest.handle.askId}. This is not acknowledgment of an earlier transport request.</p>}
    {retained.map(entry => <div key={entry.request.action.actionId}>
      <p>Original {entry.kind} · ask {entry.request.action.handle.askId} · transport: {entry.transport}{entry.result && ` · ${entry.result.status}`}</p>
      {entry.kind === "answer" && <CapturedAnswer entry={entry} />}
      {entry.observed && <p>Separate backend observation: {entry.observed.status}{entry.observed.runId && ` · run ${entry.observed.runId}`}</p>}
      <button type="button" onClick={() => { void actions.observeRemote(entry.request.action.actionId, request => sessionAsks.observe({
        expectedHostEpoch: request.expectedHostEpoch, actionId: request.action.actionId, handle: askWireHandle(request.action.handle),
      }, { timeoutMilliseconds: 8000 }), () => { capability.observe({ status: "stale_epoch", epoch }); }); }}>Observe original action</button>
    </div>)}
  </section>;
}

function CapturedAnswer({ entry }: { entry: RetainedAction }) {
  const request = entry.request;
  const handle = request.action.handle;
  return <section className="ask-captured-answer" aria-label="Captured original ask answer">
    <p>Original captured answer (read-only). Transport {entry.transport}; admission is not run completion. Question and choice wording was not captured by this action; indexes below are exact, not inferred from a refreshed ask. This is owner evidence, not a discardable local draft.</p>
    <p className="detail">Host {request.expectedHostEpoch} · session {handle.sessionId} · operation {handle.operationId} · runtime {handle.runtimeInstanceId} · attachment {handle.attachmentGeneration} · provider {handle.providerId} · run {handle.runId} · ask {handle.askId} · generation {handle.responseGeneration} · action {request.action.actionId}</p>
    {request.action.answers.map(answer => <div key={answer.questionIndex}>
      <p>Question index {answer.questionIndex} · selected choice indexes: {answer.selectedChoiceIndexes.join(", ") || "None"}</p>
      {answer.freeformText !== null && <pre aria-label={`Captured answer for question ${answer.questionIndex}`}>{answer.freeformText}</pre>}
    </div>)}
  </section>;
}
