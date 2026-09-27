import { useEffect, useState } from "react";
import { type InputEntry, type InputAnswer, type createUserInputReviewer } from "./sessionUserInput";
import type { createMutationCapability } from "./sessionOperations";
import { useShellLanguage } from "./shellLanguage";

type Props = { epoch: string; sessionId: string; reviewer: ReturnType<typeof createUserInputReviewer>; capability: ReturnType<typeof createMutationCapability> };
export function UserInputPanel({ epoch, sessionId, reviewer, capability }: Props) {
  const { t } = useShellLanguage();
  const [, changed] = useState(0);
  const [view, setView] = useState<ReturnType<Props["reviewer"]["forSelection"]>>();
  useEffect(() => {
    const controller = new AbortController();
    setView(reviewer.forSelection(epoch, sessionId, controller.signal, () => changed(n => n + 1),
      () => capability.observe({ status: "stale_epoch", epoch }), () => capability.canSubmit({ expectedEpoch: epoch })));
    return () => controller.abort();
  }, [epoch, sessionId, reviewer, capability]);
  const original = reviewer.original(); const page = view?.page(); const blocked = reviewer.blocked() || !capability.canMutate();
  return <section aria-label={t("Nonsecret provider input")}>
    <h3>{t("Nonsecret provider input")}</h3>
    <p>{t("Not credential entry. Do not enter secrets: answers may persist in provider tool results and history. This grants no command or file permission.")}</p>
    <p>{t("Refresh manually. Closing this panel or reloading the renderer retains host-pending attempts, not lost decision outcomes. Closing the application invalidates pending attempts. Accepted means only an owner decision, not provider continuation or persistence success.")}</p>
    <button type="button" disabled={!view || !capability.canMutate()} onClick={() => { void view?.refresh(); }}>{t("Refresh input")}</button>
    {!page && <p>{t("No current validated input page. Refresh explicitly; absence cannot recover a lost decision.")}</p>}
    {page?.hasMore && <p>{t("More forms remain; refresh after handling this page.")}</p>}
    {page?.entries.map(entry => <InputForm key={entry.handle.attemptId} entry={entry} blocked={blocked}
      resolve={answers => { void view?.resolve(entry.handle, answers); }} cancel={() => { void view?.cancel(entry.handle); }} />)}
    {original && <div role="status"><p>{t("Original {action} for {session}: {status}. No automatic replay.", { action: original.action, session: original.sessionId, status: original.status })}</p>
      <button type="button" onClick={() => { reviewer.observeOriginal(); changed(n => n + 1); }}>{t("Observe original locally (no RPC)")}</button>
      <button type="button" disabled={original.kind !== "terminal"} onClick={() => { reviewer.acknowledge(); }}>{t("Acknowledge observed terminal original")}</button>
      {original.kind === "uncertain" && <p>{t("Genuine uncertainty is retained. Listing an absent attempt cannot establish its decision.")}</p>}
    </div>}
  </section>;
}

function InputForm({ entry, blocked, resolve, cancel }: { entry: InputEntry; blocked: boolean; resolve: (answers: InputAnswer[]) => void; cancel: () => void }) {
  const { t } = useShellLanguage();
  // Missing differs from deliberately entered empty freeform text. No implicit blank submission.
  const [values, setValues] = useState<Record<string, string>>({});
  const set = (id: string, value: string) => setValues(current => ({ ...current, [id]: value }));
  const ready = entry.prompts.every(p => Object.hasOwn(values, p.id));
  return <fieldset disabled={blocked}><legend>{entry.providerId} · {entry.handle.interactionId}</legend>
    {entry.prompts.map(p => <div key={p.id}>
      {p.header && <h4>{p.header}</h4>}<p>{p.question}</p>
      {p.options.map(o => <button type="button" key={o.label} aria-pressed={values[p.id] === o.label} onClick={() => set(p.id, o.label)}>{o.label}{o.description && <span> — {o.description}</span>}</button>)}
      {p.allowFreeform && <label>{p.id}<textarea maxLength={2048} value={Object.hasOwn(values, p.id) ? values[p.id] : ""} onChange={event => set(p.id, event.target.value)} />
        <button type="button" onClick={() => set(p.id, "")}>{t("Deliberately answer with empty text")}</button></label>}
    </div>)}
    <button type="button" disabled={!ready} onClick={() => resolve(entry.prompts.map(p => ({ promptId: p.id, value: values[p.id]! })))}>{t("Submit literal answers")}</button>
    <button type="button" onClick={cancel}>{t("Cancel this attempt only")}</button>
  </fieldset>;
}
