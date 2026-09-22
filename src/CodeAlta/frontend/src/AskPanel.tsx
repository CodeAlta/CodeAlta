import { useEffect, useState, useSyncExternalStore } from "react";
import { sessionAsks } from "#neoastra";
import { askWireHandle, captureAskAction, type AskPage, type createAskActions } from "./sessionAsks";
import type { createMutationCapability } from "./sessionOperations";
import { showAskDetails } from "./workspacePresentation";

type Props = { epoch: string; sessionId: string; actions: ReturnType<typeof createAskActions>; capability: ReturnType<typeof createMutationCapability> };

export function AskPanel({ epoch, sessionId, actions, capability }: Props) {
  const [page, setPage] = useState<AskPage>();
  const [notice, setNotice] = useState("Refresh asks to read the retained backend state.");
  const [revision, setRevision] = useState(0);
  const [, repaint] = useState(0);
  const [text, setText] = useState<Record<number, string>>({});
  const [choices, setChoices] = useState<Record<number, number[]>>({});
  const canMutate = useSyncExternalStore(capability.subscribe, capability.canMutate);
  useEffect(() => actions.subscribe(() => repaint(value => value + 1)), [actions]);
  useEffect(() => {
    const controller = new AbortController();
    // Read only; selection cancellation cannot reach any action or its app-owned original waiter.
    const original = sessionAsks.list({ expectedHostEpoch: epoch, sessionId }, { signal: controller.signal, timeoutMilliseconds: 8000 });
    const observer = original.then(value => {
      const next = actions.readPage(value, epoch, sessionId,
        () => !controller.signal.aborted, () => { capability.observe({ status: "stale_epoch", epoch }); });
      if (!next) return;
      setPage(next); setText({}); setChoices({});
      setNotice(next.head ? "Answer or cancel the original pending ask." : "No pending head reported. Absence is not acknowledgment.");
    }).catch(() => { if (!controller.signal.aborted) { setPage(undefined); setNotice("Ask read failed; no action outcome can be inferred."); } });
    return () => { controller.abort(); void observer; };
  }, [epoch, sessionId, revision, actions, capability]);
  const head = page?.head;
  const blocked = !capability.canMutate() || !head || head.state !== "pending" || actions.blocked(head.handle);
  const submit = (kind: "answer" | "cancel") => {
    if (blocked || !head) return;
    try {
      const answers = kind === "cancel" ? [] : head.request.questions.map((_, index) => ({ questionIndex: index,
        selectedChoiceIndexes: choices[index] ?? [], freeformText: text[index] || null }));
      const original = captureAskAction(epoch, head.handle, answers, crypto.randomUUID());
      void actions.submit(kind, original, () => capability.canSubmit({ expectedEpoch: epoch }),
        () => { capability.observe({ status: "stale_epoch", epoch }); });
    } catch { setNotice("The answer is invalid or exceeds the 8,192-character aggregate limit."); }
  };
  const retained = actions.forSession(sessionId);
  const refresh = () => setRevision(value => value + 1);
  const visible = showAskDetails(page, retained.length, notice.startsWith("Ask read failed"), !canMutate);
  if (!visible) return <button type="button" className="ask-refresh" onClick={refresh}>Check asks</button>;
  return <section aria-label="Owned asks">
    <h3>Pending asks</h3>
    {(!page?.head || notice.startsWith("Ask read failed") || notice.startsWith("The answer is invalid")) && <p role={notice.startsWith("Ask read failed") || notice.startsWith("The answer is invalid") ? "alert" : "status"}>{notice}</p>}
    {!canMutate && <p role="alert">Host identity changed. Reload required; retained ask actions cannot be retargeted.</p>}
    <p className="detail">Restricted caller-session asks only. Answer starts a new text submission; Cancel does not stop a run. No files, provider input, automatic retry or restart recovery.</p>
    <button type="button" onClick={refresh}>Refresh asks</button>
    {page?.hasMore && <p>Additional retained asks or dispositions are omitted from this bounded view.</p>}
    {head && <fieldset disabled={blocked}>
      <legend>Original ask {head.handle.askId} · {head.state}</legend>
      {head.request.questions.map((question, index) => <div key={index}>
        <h4>{question.title}</h4><p>{question.question}</p>{question.description && <p>{question.description}</p>}
        {question.choices.map((choice, choiceIndex) => <label key={choiceIndex}>
          <input type="checkbox" checked={(choices[index] ?? []).includes(choiceIndex)} onChange={event => setChoices(current => ({ ...current,
            [index]: event.target.checked ? [...(current[index] ?? []), choiceIndex] : (current[index] ?? []).filter(v => v !== choiceIndex) }))} />
          {choice.title}{choice.description && <span> — {choice.description}</span>}
        </label>)}
        {question.freeform && <label>{question.freeform.title ?? "Answer"}<textarea maxLength={8192} value={text[index] ?? ""}
          placeholder={question.freeform.placeholder ?? undefined} onChange={event => setText(current => ({ ...current, [index]: event.target.value }))} /></label>}
      </div>)}
      <button type="button" onClick={() => submit("answer")}>Answer original ask</button>
      <button type="button" onClick={() => submit("cancel")}>Cancel original ask</button>
    </fieldset>}
    {page?.latest && <p>Latest backend disposition: {page.latest.status} · ask {page.latest.handle.askId}. This is not acknowledgment of an earlier transport request.</p>}
    {retained.map(entry => <div key={entry.request.action.actionId}>
      <p>Original {entry.kind} · ask {entry.request.action.handle.askId} · transport: {entry.transport}{entry.result && ` · ${entry.result.status}`}</p>
      {entry.observed && <p>Separate backend observation: {entry.observed.status}{entry.observed.runId && ` · run ${entry.observed.runId}`}</p>}
      <button type="button" onClick={() => { void actions.observeRemote(entry.request.action.actionId, request => sessionAsks.observe({
        expectedHostEpoch: request.expectedHostEpoch, actionId: request.action.actionId, handle: askWireHandle(request.action.handle),
      }, { timeoutMilliseconds: 8000 }), () => { capability.observe({ status: "stale_epoch", epoch }); }); }}>Observe original action</button>
    </div>)}
  </section>;
}
