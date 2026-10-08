import { useEffect, useLayoutEffect, useRef, useState } from "react";
import type { SessionPermissionCommand } from "#neoastra";
import type { createPermissionReviewer, PermissionDecisionObservation, PermissionReviewState } from "./sessionPermissions";
import { useShellLanguage } from "./shellLanguage";
import { createPaletteFocusRestoration } from "./paletteActions";
import { modalDialogOpen } from "./modalDialogs";

export function CommandPermissionPanel({ reviewer, epoch, sessionId, canReview }: {
  reviewer: ReturnType<typeof createPermissionReviewer>; epoch: string; sessionId: string;
  canReview: () => boolean;
}) {
  const { t } = useShellLanguage();
  const [state, setState] = useState<PermissionReviewState>();
  const [observation, setObservation] = useState<PermissionDecisionObservation | null>();
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
    setState(undefined);
    setObservation(undefined); // Presentation only; never acknowledge the App-owned record on mount/selection.
    scope.current = reviewer.forSelection({ expectedHostEpoch: epoch, sessionId }, controller.signal, value => {
      currentState.current = value; setState(value);
    });
    return () => { activeReview.current = null; controller.abort(); scope.current = null; focus.cancel(); };
  }, [reviewer, epoch, sessionId]);
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
    {state?.kind === "resolving" && <p role="status">{t("Decision response pending; no retry. Use local observation after selection or remount.")}</p>}
    {state?.kind === "error" && <p role="alert">{t("Command review unavailable ({code}).", { code: state.code })} {state.reloadRequired ? t("Reload the renderer, then refresh pending commands for the selected session. Until reload, refresh and review actions are disabled across selections. An uncertain response may already have committed. Reload does not revoke it.") : t("No permission was inferred. Refresh explicitly to retry the read.")}</p>}
    {state?.kind === "result" && <p role="status">{t(state.code === "resolved" ? "Decision accepted — not proof of command execution or run completion." : "Decision rejected — the attempt may be stale, canceled or already resolved.")} {t("Live publication is not acknowledgment. Explicitly observe the retained decision, then refresh for a fresh review.")}</p>}
  </>;
  const details = (entry: SessionPermissionCommand) => <>
    <dl>
      <dt>{t("Provider")}</dt><dd>{entry.providerId}</dd>
      <dt>{t("Session ID")}</dt><dd>{entry.handle.sessionId}</dd>
      <dt>{t("Submission operation")}</dt><dd>{entry.handle.operationId}</dd>
      <dt>{t("Runtime / attachment")}</dt><dd>{entry.handle.runtimeInstanceId} / {entry.handle.attachmentGeneration}</dd>
      <dt>{t("Run")}</dt><dd>{entry.handle.runId ?? t("Not supplied by provider")}</dd>
      <dt>{t("Interaction / attempt")}</dt><dd>{entry.handle.interactionId} / {entry.handle.attemptId}</dd>
      <dt>{t("Working directory")}</dt><dd><pre data-permission-directory>{entry.workingDirectory}</pre></dd>
    </dl>
    <p>{t("Command (complete)")}</p><pre data-permission-command>{entry.command}</pre>
    {entry.reason !== null && <><p>{t("Reason")}</p><pre data-permission-reason>{entry.reason}</pre></>}
  </>;
  return <section className="command-permission-panel" aria-label={t("Pending command permissions")}>
    {review && <dialog key={review.id} ref={dialog} className="app-dialog permission-review-dialog" aria-modal="true" aria-labelledby="permission-review-title"
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
      <p>{t("Closing this dialog is not the Cancel decision. This is a manually observed pending command, not proof it is still pending.")}</p>
      <p>{t("Allow once can execute with the host's privileges. No session grant or sandbox is provided. Deny and Cancel resolve only this permission, not the run.")}</p>
      <dl><dt>{t("Host epoch")}</dt><dd>{review.epoch}</dd></dl>
      {details(review.entry)}
      {outcome}
      {!current(review) && (!state || state.kind === "ready" || state.kind === "loading") && <p role="status">{t("This review changed. Close and review a current entry; no decision was sent by dismissal.")}</p>}
      <footer>{(["allow_once", "deny", "cancel"] as const).map(decision => <button key={decision} type="button" data-permission-decision={decision}
        disabled={!current(review)} onClick={event => {
          if (event.defaultPrevented || composing.current || !current(review)) return;
          review.valid = false; setObservation(undefined);
          void review.scope.decide(review.entry, decision);
        }}>{t(decision === "allow_once" ? "Allow once" : decision === "deny" ? "Deny" : "Cancel")}</button>)}</footer>
    </dialog>}
    <h3>{t("Pending plain commands — explicit review enabled")}</h3>
    <p className="detail">{t("Manual refresh only, at most four pending commands for this exact session. Review the complete command and directory before allowing it. Allow once can execute with the host's privileges; these roots are not a sandbox. Unsupported permission kinds/extensions remain denied. Only in-process built-in tools honor the per-send callback; other providers and custom tools are not implicitly rebound.")}</p>
    <p className="detail">{t("Deny and Cancel resolve this permission, not the entire run. Switching sessions or closing the review does not cancel pending permissions or revoke an accepted decision. Use the exact submission's Abort control separately. There is no durable recovery or execution acknowledgment.")}</p>
    <p className="detail">{t("One original decision response is retained in this renderer across selection and remount. Observe it explicitly before refreshing for another review; pending or uncertain decisions cannot be replaced or resent. Observation is local only. Renderer reload loses this record and permits only fresh manual pending reads in the same host; an empty list cannot recover a decision. Host restart recovers no old authority.")}</p>
    <button type="button" data-permission-observe onClick={() => setObservation(scope.current?.observeDecision() ?? null)}>{t("Observe retained decision")}</button>
    {observation === null && <p role="status">{t("No decision is retained in this renderer. No host state was read or inferred.")}</p>}
    {observation && <section aria-label={t("Original permission decision observation")}>
      <h4>{t("Original decision — last explicit local observation")}</h4>
      <dl>
        <dt>{t("Original session (not the selected review)")}</dt><dd>{observation.origin.handle.sessionId}</dd>
        <dt>{t("Clicked decision")}</dt><dd>{observation.origin.decision}</dd>
        <dt>{t("Host epoch")}</dt><dd>{observation.origin.expectedHostEpoch}</dd>
        <dt>{t("Submission operation")}</dt><dd>{observation.origin.handle.operationId}</dd>
        <dt>{t("Runtime / attachment")}</dt><dd>{observation.origin.handle.runtimeInstanceId} / {observation.origin.handle.attachmentGeneration}</dd>
        <dt>{t("Run")}</dt><dd>{observation.origin.handle.runId ?? t("Not supplied by provider")}</dd>
        <dt>{t("Interaction / attempt")}</dt><dd>{observation.origin.handle.interactionId} / {observation.origin.handle.attemptId}</dd>
      </dl>
      <p role="status">{observation.state === "pending" ? t("Original response still pending. This observation does not acknowledge a terminal result or enable another decision. Observe again explicitly to check local state.")
        : observation.state === "error" ? t("Original response unavailable ({code}). Observation cannot clear uncertainty or epoch invalidation; review remains disabled until reload. Reload does not revoke a decision.", { code: String(observation.code) })
        : observation.state === "resolved" ? t("Host accepted this original decision, not proof of command execution, run completion, rollback or revocation. Terminal response now explicitly observed; refresh the selected session for a fresh review.")
        : t("Host rejected this original decision request; this does not identify an earlier decision. Terminal response now explicitly observed; refresh the selected session for a fresh review.")}</p>
    </section>}
    <h4>{t("Selected-session pending review: {session}", { session: sessionId })}</h4>
    <button type="button" data-permission-refresh disabled={!canReview() || state?.kind === "resolving" || (state?.kind === "error" && state.reloadRequired)} onClick={() => { if (canReview()) void scope.current?.refresh(); }}>{t("Refresh pending commands")}</button>
    {state?.kind === "loading" && <p role="status">{t("Reading pending commands…")}</p>}
    {outcome}
    {state?.kind === "ready" && <>
      {state.entries.length === 0 && <p role="status">{t("No pending supported commands observed for this session. This does not mean the provider is idle.")}</p>}
      {state.hasMore && <p role="status">{t("More commands are pending. Resolve entries and refresh to see the next window.")}</p>}
      <ol className="history-records">{state.entries.map(entry => <li key={entry.handle.attemptId}>
        {details(entry)}
        <div className="history-controls">
          <button type="button" data-permission-review disabled={!canReview()} onKeyDown={event => {
            if ((event.key === "Enter" || event.key === " ") && (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229 || event.repeat)) event.preventDefault();
          }} onClick={event => {
            if (event.defaultPrevented || !event.currentTarget.isConnected || event.currentTarget.closest("[inert]")
              || !canReview() || currentState.current !== state || !scope.current || activeReview.current
              || modalDialogOpen()) return;
            focus.cancel();
            const value = { id: ++generation.current, entry, state, scope: scope.current, epoch, view: canReview, origin: event.currentTarget, valid: true };
            activeReview.current = value; setReview(value);
          }}>{t("Review command permission")}</button>
        </div>
      </li>)}</ol>
    </>}
  </section>;
}
