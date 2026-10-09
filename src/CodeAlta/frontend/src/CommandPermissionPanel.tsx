import { useEffect, useLayoutEffect, useRef, useState } from "react";
import type { SessionPermissionCommand } from "#neoastra";
import type { createPermissionReviewer, PermissionReviewState } from "./sessionPermissions";
import { useShellLanguage } from "./shellLanguage";
import { createPaletteFocusRestoration } from "./paletteActions";
import { keptOnOutsidePress, modalDialogOpen } from "./modalDialogs";

// How often the pending requests of a running session are read while none is shown.
const pendingReadInterval = 1_500;

/**
 * The requests of a session that wait for the user's permission. It shows only while one waits, a decision is sent or
 * reading them failed. It reads them by itself while the session runs and none is shown, once more when the run ends
 * with some shown, and after the answer to a decision, which it acknowledges itself; never while one is reviewed.
 */
export function CommandPermissionPanel({ reviewer, epoch, sessionId, canReview, running }: {
  reviewer: ReturnType<typeof createPermissionReviewer>; epoch: string; sessionId: string;
  canReview: () => boolean;
  /** Whether the session has a run that may ask for permission. */
  running: boolean;
}) {
  const { t } = useShellLanguage();
  const [state, setState] = useState<PermissionReviewState>();
  // The requests last read: a read in progress keeps them on screen.
  const [shown, setShown] = useState<Extract<PermissionReviewState, { kind: "ready" }> | null>(null);
  const [poll, setPoll] = useState(0);
  const notAttempted = useRef({});
  const attempted = useRef<PermissionReviewState | undefined | object>(notAttempted.current);
  const scope = useRef<ReturnType<typeof reviewer.forSelection> | null>(null);
  const currentState = useRef<PermissionReviewState | undefined>(undefined);
  const currentView = useRef(canReview);
  currentView.current = canReview;
  type Review = { id: number; entry: SessionPermissionCommand; state: PermissionReviewState; scope: NonNullable<typeof scope.current>;
    epoch: string; view: () => boolean; origin: HTMLButtonElement; valid: boolean };
  const [review, setReview] = useState<Review | null>(null);
  const activeReview = useRef<Review | null>(null);
  const dialog = useRef<HTMLDialogElement>(null);
  const composing = useRef(false);
  const generation = useRef(0);
  const [focus] = useState(createPaletteFocusRestoration);
  const [, renderInvalidation] = useState(0);
  const current = (value: Review) => activeReview.current === value && value.valid && value.scope === scope.current
    && currentState.current === value.state && value.view() && currentView.current()
    && !!dialog.current?.open && dialog.current.isConnected && !dialog.current.closest("[inert]");
  function close() {
    const value = activeReview.current;
    activeReview.current = null;
    setReview(null);
    if (dialog.current?.open) dialog.current.close();
    if (value) focus.schedule(value.origin, () => scope.current === value.scope && value.view() && currentView.current(),
      () => !!modalDialogOpen());
  }
  useLayoutEffect(() => {
    const controller = new AbortController();
    activeReview.current = null; setReview(null); currentState.current = undefined;
    setState(undefined); setShown(null); attempted.current = notAttempted.current;
    scope.current = reviewer.forSelection({ expectedHostEpoch: epoch, sessionId }, controller.signal, value => {
      currentState.current = value; setState(value);
      if (value.kind !== "loading") setShown(value.kind === "ready" ? value : null);
    });
    return () => { activeReview.current = null; controller.abort(); scope.current = null; focus.cancel(); };
  }, [reviewer, epoch, sessionId]);
  useEffect(() => {
    const current = scope.current;
    if (!current || review || state?.kind === "loading" || state?.kind === "resolving"
      || (state?.kind === "error" && state.reloadRequired)) return;
    // The answer to a decision is final: acknowledging it lets the next read show what waits now.
    if (state?.kind === "result") current.observeDecision();
    // A request that is shown waits for the user: it is not read again before an answer or the end of the run.
    else if (!running || !!shown?.entries.length) return;
    // A selection or an answer is read at once, a read that could not start again later.
    const immediate = (state === undefined || state.kind === "result") && attempted.current !== state;
    const timer = setTimeout(() => {
      attempted.current = state;
      if (canReview()) void current.refresh();
      setPoll(value => value + 1);
    }, immediate ? 0 : pendingReadInterval);
    return () => clearTimeout(timer);
  }, [state, shown, review, running, poll]);
  const wasRunning = useRef(running);
  useEffect(() => {
    // A run that ends takes its requests with it: one read shows what is left.
    if (wasRunning.current && !running && shown?.entries.length && !activeReview.current && canReview()) void scope.current?.refresh();
    wasRunning.current = running;
  }, [running]);
  useEffect(() => {
    const transition = (event: Event) => {
      if (!(event.target instanceof HTMLDialogElement) || !activeReview.current) return;
      // Our initial showModal is presentation only. Every subsequent modal transition,
      // including close/reopen in the same task, retires these particular controls.
      if (event.target === dialog.current && (event as ToggleEvent).newState === "open" && !dialog.current.open) return;
      activeReview.current.valid = false;
      renderInvalidation(value => value + 1);
    };
    document.addEventListener("beforetoggle", transition, true);
    return () => document.removeEventListener("beforetoggle", transition, true);
  }, []);
  useLayoutEffect(() => {
    if (!review) return;
    const element = dialog.current!;
    composing.current = false;
    element.showModal();
    renderInvalidation(value => value + 1);
    return () => { if (element.open) element.close(); };
  }, [review]);
  const outcome = <>
    {state?.kind === "resolving" && <p role="status">{t("Sending your decision…")}</p>}
    {state?.kind === "result" && <p role="status">{t(state.code === "resolved" ? "Your decision was sent." : "This request no longer waits: your decision was not used.")}</p>}
    {state?.kind === "error" && <p role="alert">{t("Command review unavailable ({code}).", { code: state.code })} {state.reloadRequired && t("Reload the window to review requests again. A decision already sent stays sent.")}</p>}
  </>;
  if (!review && state?.kind !== "resolving" && state?.kind !== "error" && !shown?.entries.length) return null;
  const details = (entry: SessionPermissionCommand) => <>
    <dl>
      <dt>{t("Provider")}</dt><dd>{entry.providerId}</dd>
      <dt>{t("Session ID")}</dt><dd>{entry.handle.sessionId}</dd>
      <dt>{t("Submission operation")}</dt><dd>{entry.handle.operationId}</dd>
      <dt>{t("Runtime / attachment")}</dt><dd>{entry.handle.runtimeInstanceId} / {entry.handle.attachmentGeneration}</dd>
      <dt>{t("Run")}</dt><dd>{entry.handle.runId ?? t("Not supplied by provider")}</dd>
      <dt>{t("Interaction / attempt")}</dt><dd>{entry.handle.interactionId} / {entry.handle.attemptId}</dd>
      {entry.kind === "commandExecution"
        && <><dt>{t("Working directory")}</dt><dd><pre data-permission-directory>{entry.workingDirectory}</pre></dd></>}
    </dl>
    {entry.kind === "commandExecution"
      ? <><p>{t("Command (complete)")}</p><pre data-permission-command>{entry.command}</pre></>
      : <><p>{t("File change under (complete)")}</p><pre data-permission-grant-root>{entry.grantRoot}</pre></>}
    {entry.reason !== null && <><p>{t("Reason")}</p><pre data-permission-reason>{entry.reason}</pre></>}
  </>;
  return <section className="command-permission-panel" aria-label={t("Pending command permissions")}>
    {review && <dialog key={review.id} ref={dialog} className="app-dialog permission-review-dialog" aria-modal="true" aria-labelledby="permission-review-title" {...keptOnOutsidePress}
      onClose={() => { if (activeReview.current === review) close(); }} onCancel={event => { event.preventDefault(); if (!composing.current) close(); }}
      onCompositionStart={() => { composing.current = true; }} onCompositionEnd={() => { composing.current = false; }}
      onKeyDown={event => {
        event.stopPropagation();
        if (event.defaultPrevented) return;
        if (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229 || composing.current || event.repeat) { event.preventDefault(); return; }
        if (event.key === "Escape") { event.preventDefault(); close(); }
        else if (event.key === "Enter" && !(event.target instanceof HTMLButtonElement)) event.preventDefault();
      }}>
      <header><h2 id="permission-review-title">{t("Review command permission")}</h2><button autoFocus type="button" onClick={close}>{t("Close")}</button></header>
      <p>{t("Closing this dialog does not answer the request.")}</p>
      <p>{t("Allow once can execute with the host's privileges. No session grant or sandbox is provided. Deny and Cancel resolve only this permission, not the run.")}</p>
      <dl><dt>{t("Host epoch")}</dt><dd>{review.epoch}</dd></dl>
      {details(review.entry)}
      {outcome}
      {!current(review) && (!state || state.kind === "ready" || state.kind === "loading") && <p role="status">{t("This review changed. Close and review a current entry; no decision was sent by dismissal.")}</p>}
      <footer>{(["allow_once", "deny", "cancel"] as const).map(decision => <button key={decision} type="button" data-permission-decision={decision}
        disabled={!current(review)} onClick={event => {
          if (event.defaultPrevented || composing.current || !current(review)) return;
          review.valid = false;
          void review.scope.decide(review.entry, decision);
        }}>{t(decision === "allow_once" ? "Allow once" : decision === "deny" ? "Deny" : "Cancel")}</button>)}</footer>
    </dialog>}
    <h3>{t("Waiting for your permission")}</h3>
    <p className="detail">{t("The session waits until you answer each request.")}</p>
    {outcome}
    {shown && shown.entries.length > 0 && <>
      <ol className="history-records">{shown.entries.map(entry => <li key={entry.handle.attemptId}>
        <p>{t(entry.kind === "commandExecution" ? "Run a command" : "Change files under")}</p>
        <pre>{entry.kind === "commandExecution" ? entry.command : entry.grantRoot}</pre>
        <div className="history-controls">
          <button type="button" data-permission-review disabled={!canReview() || state !== shown} onKeyDown={event => {
            if ((event.key === "Enter" || event.key === " ") && (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229 || event.repeat)) event.preventDefault();
          }} onClick={event => {
            if (event.defaultPrevented || !event.currentTarget.isConnected || event.currentTarget.closest("[inert]")
              || !canReview() || currentState.current !== shown || !scope.current || activeReview.current
              || modalDialogOpen()) return;
            focus.cancel();
            const value = { id: ++generation.current, entry, state: shown, scope: scope.current, epoch, view: canReview, origin: event.currentTarget, valid: true };
            activeReview.current = value; setReview(value);
          }}>{t("Review command permission")}</button>
        </div>
      </li>)}</ol>
      {shown.hasMore && <p role="status">{t("More requests are waiting after these.")}</p>}
    </>}
  </section>;
}
