import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { type InputEntry, type InputPage, type createUserInputReviewer } from "./sessionUserInput";
import type { createMutationCapability } from "./sessionOperations";
import { useShellLanguage } from "./shellLanguage";
import { createPaletteFocusRestoration } from "./paletteActions";
import { keptOnOutsidePress, modalDialogOpen } from "./modalDialogs";

type Props = { epoch: string; sessionId: string; reviewer: ReturnType<typeof createUserInputReviewer>; capability: ReturnType<typeof createMutationCapability>;
  canReview?: () => boolean };
export function UserInputPanel({ epoch, sessionId, reviewer, capability, canReview }: Props) {
  const { t } = useShellLanguage();
  const [, changed] = useState(0);
  type View = ReturnType<Props["reviewer"]["forSelection"]>;
  const view = useRef<View | undefined>(undefined);
  const reading = useRef(false);
  const allowed = () => capability.canSubmit({ expectedEpoch: epoch }) && (canReview?.() ?? true);
  const latestAllowed = useRef(allowed); latestAllowed.current = allowed;
  type Draft = { entry: InputEntry; page: InputPage; scope: View; epoch: string; allowed: () => boolean; valid: boolean; values: Record<string, string> };
  type Review = { draft: Draft; origin: HTMLButtonElement; valid: boolean };
  // One local form, never a cross-session draft store or an authority clone.
  const draft = useRef<Draft | null>(null);
  const active = useRef<Review | null>(null);
  const [review, setReview] = useState<Review | null>(null);
  const [lost, setLost] = useState(false);
  const dialog = useRef<HTMLDialogElement>(null);
  const composing = useRef(false);
  const [focus] = useState(createPaletteFocusRestoration);
  const draftCurrent = (d: Draft) => !reading.current && d.valid && d.scope === view.current && d.scope.page() === d.page
    && d.page.entries.includes(d.entry) && d.allowed() && latestAllowed.current() && !reviewer.blocked();
  const current = (r: Review) => active.current === r && r.valid && draftCurrent(r.draft)
    && !!dialog.current?.open && dialog.current.isConnected && !dialog.current.closest("[inert]");
  function discard() { if (draft.current) { draft.current.valid = false; draft.current = null; setLost(true); } }
  function close() {
    const value = active.current;
    if (value && !current(value)) discard();
    active.current = null; setReview(null);
    if (dialog.current?.open) dialog.current.close();
    if (value) focus.schedule(value.origin, () => value.draft.scope === view.current && value.draft.allowed() && latestAllowed.current(),
      () => !!modalDialogOpen());
  }
  useLayoutEffect(() => {
    const controller = new AbortController();
    active.current = null; setReview(null); discard(); reading.current = false;
    view.current = reviewer.forSelection(epoch, sessionId, controller.signal, () => changed(n => n + 1),
      () => capability.observe({ status: "stale_epoch", epoch }), () => latestAllowed.current());
    changed(n => n + 1);
    return () => { controller.abort(); view.current = undefined; active.current = null; focus.cancel(); };
  }, [epoch, sessionId, reviewer, capability]);
  useEffect(() => {
    const transition = (event: Event) => {
      if (!(event.target instanceof HTMLDialogElement)) return;
      if (active.current && event.target === dialog.current && (event as ToggleEvent).newState === "open" && !dialog.current.open) return;
      // Deliberate close clears active before native close and may retain this exact draft.
      // Other/native modal ABA retires controls and edits even when IDs/text recur.
      if (active.current) { active.current.valid = false; discard(); changed(n => n + 1); }
      else if (draft.current && event.target !== dialog.current) discard();
    };
    document.addEventListener("beforetoggle", transition, true);
    return () => document.removeEventListener("beforetoggle", transition, true);
  }, []);
  useLayoutEffect(() => {
    if (!review) return;
    const element = dialog.current!; composing.current = false; element.showModal(); changed(n => n + 1);
    return () => { if (element.open) element.close(); };
  }, [review]);
  useLayoutEffect(() => { if (draft.current && !draftCurrent(draft.current)) discard(); });
  const original = reviewer.original(); const page = view.current?.page();
  const provenance = (entry: InputEntry, hostEpoch: string) => <dl>
    <dt>{t("Host epoch")}</dt><dd>{hostEpoch}</dd>
    <dt>{t("Provider")}</dt><dd>{entry.providerId}</dd>
    <dt>{t("Session ID")}</dt><dd>{entry.handle.sessionId}</dd>
    <dt>{t("Submission operation")}</dt><dd>{entry.handle.operationId}</dd>
    <dt>{t("Runtime / attachment")}</dt><dd>{entry.handle.runtimeInstanceId} / {entry.handle.attachmentGeneration}</dd>
    <dt>{t("Run")}</dt><dd>{entry.handle.runId ?? t("Not supplied by provider")}</dd>
    <dt>{t("Interaction / attempt")}</dt><dd>{entry.handle.interactionId} / {entry.handle.attemptId}</dd>
  </dl>;
  const set = (id: string, value: string) => {
    if (!review || !current(review)) return;
    review.draft.values = { ...review.draft.values, [id]: value }; changed(n => n + 1);
  };
  return <section className="provider-input-panel" aria-label={t("Nonsecret provider input")}>
    {review && <dialog ref={dialog} className="app-dialog provider-input-dialog" aria-modal="true" aria-labelledby="provider-input-title" {...keptOnOutsidePress}
      onClose={() => { if (active.current === review) close(); }} onCancel={event => { event.preventDefault(); if (!composing.current) close(); }}
      onCompositionStart={() => { composing.current = true; }} onCompositionEnd={() => { composing.current = false; }}
      onKeyDown={event => {
        event.stopPropagation();
        if (event.defaultPrevented) return;
        if (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229 || composing.current || event.repeat) { event.preventDefault(); return; }
        if (event.key === "Escape") { event.preventDefault(); close(); }
        else if (event.key === "Enter" && !(event.target instanceof HTMLButtonElement) && !(event.target instanceof HTMLTextAreaElement)) event.preventDefault();
      }}>
      <header><h2 id="provider-input-title">{t("Review provider input")}</h2><button autoFocus type="button" onClick={close}>{t("Close")}</button></header>
      <p>{t("Close only dismisses this manually observed form; it does not cancel provider input. This is not a live pending-state guarantee.")}</p>
      <p>{t("Not credential entry. Do not enter secrets: answers may persist in provider tool results and history. This grants no command or file permission.")}</p>
      {provenance(review.draft.entry, review.draft.epoch)}
      {original && <p role="status">{t("Original {action} for {session}: {status}. No automatic replay.", { action: original.action, session: original.sessionId, status: original.status })}</p>}
      {!current(review) && <p role="status">{t("This input review is no longer current. Local edits cannot be reused; close and review a current entry.")}</p>}
      <fieldset disabled={!current(review)}>
        {review.draft.entry.prompts.map(p => <div key={p.id} data-input-prompt={p.id}>
          {p.header !== null && <h4>{p.header}</h4>}<pre data-input-question>{p.question}</pre>
          {p.options.map(o => <button type="button" key={o.label} aria-pressed={review.draft.values[p.id] === o.label}
            onClick={event => { if (!event.defaultPrevented && !composing.current) set(p.id, o.label); }}>{o.label}{o.description !== null && <span> — {o.description}</span>}</button>)}
          {p.allowFreeform && <label>{p.id}<textarea maxLength={2048} value={Object.hasOwn(review.draft.values, p.id) ? review.draft.values[p.id] : ""}
            onChange={event => set(p.id, event.target.value)} />
            <button type="button" onClick={event => { if (!event.defaultPrevented && !composing.current) set(p.id, ""); }}>{t("Deliberately answer with empty text")}</button></label>}
        </div>)}
        <button type="button" data-input-submit disabled={!review.draft.entry.prompts.every(p => Object.hasOwn(review.draft.values, p.id))}
          onClick={event => {
            if (event.defaultPrevented || composing.current || !current(review)) return;
            const d = review.draft;
            void d.scope.resolve(d.entry.handle, d.entry.prompts.map(p => ({ promptId: p.id, value: d.values[p.id]! })));
          }}>{t("Submit literal answers")}</button>
        <button type="button" data-input-cancel onClick={event => {
          if (!event.defaultPrevented && !composing.current && current(review)) void review.draft.scope.cancel(review.draft.entry.handle);
        }}>{t("Cancel this attempt only")}</button>
      </fieldset>
    </dialog>}
    <h3>{t("Nonsecret provider input")}</h3>
    <p>{t("Not credential entry. Do not enter secrets: answers may persist in provider tool results and history. This grants no command or file permission.")}</p>
    <p>{t("Refresh manually. Closing this panel or reloading the renderer retains host-pending attempts, not lost decision outcomes. Closing the application invalidates pending attempts. Accepted means only an owner decision, not provider continuation or persistence success.")}</p>
    <p>{t("Local answers belong only to this exact observed form. Refresh, another form or a changed scope discards them; closing alone does not.")}</p>
    {lost && <p role="status" data-input-draft-lost>{t("Local input draft discarded. No answer or cancellation was sent by dismissal.")}</p>}
    <button type="button" data-input-refresh disabled={!view.current || !allowed()} onClick={() => {
      const scope = view.current;
      if (!allowed() || !scope) return;
      discard(); reading.current = true; changed(n => n + 1);
      void scope.refresh().finally(() => { if (scope === view.current) { reading.current = false; changed(n => n + 1); } });
    }}>{t("Refresh input")}</button>
    {!page && <p>{t("No current validated input page. Refresh explicitly; absence cannot recover a lost decision.")}</p>}
    {page?.hasMore && <p>{t("More forms remain; refresh after handling this page.")}</p>}
    {page?.entries.map(entry => <div key={entry.handle.attemptId}>
      <p>{entry.providerId} / {entry.handle.interactionId}</p>
      <button type="button" data-input-review disabled={reading.current || !allowed() || reviewer.blocked()} onKeyDown={event => {
        if ((event.key === "Enter" || event.key === " ") && (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229 || event.repeat)) event.preventDefault();
      }} onClick={event => {
        if (event.defaultPrevented || !event.currentTarget.isConnected || event.currentTarget.closest("[inert]") || !allowed()
          || reading.current || reviewer.blocked() || !view.current || view.current.page() !== page || active.current
          || modalDialogOpen()) return;
        focus.cancel();
        if (!draft.current || draft.current.entry !== entry || !draftCurrent(draft.current)) {
          discard(); draft.current = { entry, page, scope: view.current, epoch, allowed, valid: true, values: {} };
        }
        setLost(false);
        const value = { draft: draft.current, origin: event.currentTarget, valid: true };
        active.current = value; setReview(value);
      }}>{t("Review provider input")}</button>
    </div>)}
    {original && <div role="status"><p>{t("Original {action} for {session}: {status}. No automatic replay.", { action: original.action, session: original.sessionId, status: original.status })}</p>
      <button type="button" onClick={() => { reviewer.observeOriginal(); changed(n => n + 1); }}>{t("Observe original locally (no RPC)")}</button>
      <button type="button" disabled={original.kind !== "terminal"} onClick={() => { reviewer.acknowledge(); }}>{t("Acknowledge observed terminal original")}</button>
      {original.kind === "uncertain" && <p>{t("Genuine uncertainty is retained. Listing an absent attempt cannot establish its decision.")}</p>}
    </div>}
  </section>;
}
