import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent } from "react";
import type { SessionPermissionCommand } from "#neoastra";
import type { CommandDecision, createPermissionReviewer, PermissionReviewState } from "./sessionPermissions";
import { useShellLanguage } from "./shellLanguage";

// How often the pending requests of a running session are read while none is shown.
const pendingReadInterval = 1_500;
// A request that just appeared takes the place the pointer may be about to click: its decisions wait this long.
const armDelay = 400;

/**
 * The requests of a session that wait for the user's permission, above its composer. It shows the first one with its
 * decisions while one waits, and a line while a decision is sent or reading them failed. It reads them by itself
 * while the session runs and none is shown, once more when the run ends with some shown, and after the answer to a
 * decision, which it acknowledges itself.
 */
export function CommandPermissionPanel({ reviewer, epoch, sessionId, canReview, running, instruct, takeFocus }: {
  reviewer: ReturnType<typeof createPermissionReviewer>; epoch: string; sessionId: string;
  canReview: () => boolean;
  /** Whether the session has a run that may ask for permission. */
  running: boolean;
  /**
   * Tells the agent what to do instead of a denied request: steered into the run when the denial was taken, queued
   * for the next turn otherwise. Without it, the panel offers no such text.
   */
  instruct?: (kind: "Steer" | "Queue", text: string) => void;
  /** Whether a request that appears may take the focus from the composer: it has no draft being written. */
  takeFocus?: () => boolean;
}) {
  const { t } = useShellLanguage();
  const [state, setState] = useState<PermissionReviewState>();
  // The requests last read: a read in progress keeps them on screen.
  const [shown, setShown] = useState<Extract<PermissionReviewState, { kind: "ready" }> | null>(null);
  const [poll, setPoll] = useState(0);
  const [armed, setArmed] = useState<string | null>(null);
  const [instead, setInstead] = useState("");
  const notAttempted = useRef({});
  const attempted = useRef<PermissionReviewState | undefined | object>(notAttempted.current);
  const scope = useRef<ReturnType<typeof reviewer.forSelection> | null>(null);
  const currentState = useRef<PermissionReviewState | undefined>(undefined);
  const card = useRef<HTMLElement>(null);
  const choices = useRef<HTMLDivElement>(null);
  useLayoutEffect(() => {
    const controller = new AbortController();
    currentState.current = undefined;
    setState(undefined); setShown(null); setArmed(null); attempted.current = notAttempted.current;
    scope.current = reviewer.forSelection({ expectedHostEpoch: epoch, sessionId }, controller.signal, value => {
      currentState.current = value; setState(value);
      if (value.kind !== "loading") setShown(value.kind === "ready" ? value : null);
    });
    return () => { controller.abort(); scope.current = null; };
  }, [reviewer, epoch, sessionId]);
  useEffect(() => {
    const current = scope.current;
    if (!current || state?.kind === "loading" || state?.kind === "resolving"
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
  }, [state, shown, running, poll]);
  const wasRunning = useRef(running);
  useEffect(() => {
    // A run that ends takes its requests with it: one read shows what is left.
    if (wasRunning.current && !running && shown?.entries.length && canReview()) void scope.current?.refresh();
    wasRunning.current = running;
  }, [running]);
  const entry = shown?.entries[0];
  const attempt = entry?.handle.attemptId ?? null;
  useEffect(() => {
    // A request that comes back after it was gone is a new one on screen: it arms again.
    if (attempt === null) { setArmed(null); return; }
    const timer = setTimeout(() => setArmed(attempt), armDelay);
    return () => clearTimeout(timer);
  }, [attempt]);
  useEffect(() => {
    if (armed === null || armed !== attempt) return;
    // Answering with the keyboard alone: the list takes the focus when nothing else is being written. The list, not a
    // choice: a space or an Enter typed at that moment answers nothing, the arrows go to the choices.
    const focused = document.activeElement;
    const region = card.current?.closest(".composer-region") ?? card.current;
    if ((focused === document.body || focused === null || !!region?.contains(focused)) && (takeFocus?.() ?? false))
      choices.current?.focus();
  }, [armed]);
  const outcome = <>
    {state?.kind === "resolving" && <p role="status">{t("Sending your decision…")}</p>}
    {state?.kind === "error" && <p role="alert">{t("Command review unavailable ({code}).", { code: state.code })} {state.reloadRequired && t("Reload the window to review requests again. A decision already sent stays sent.")}</p>}
  </>;
  if (!entry) return state?.kind === "resolving" || state?.kind === "error"
    ? <section className="command-permission-panel" aria-label={t("Pending command permissions")}>{outcome}</section> : null;
  const live = (target: HTMLElement) => target.isConnected && !target.closest("[inert]") && canReview() && currentState.current === shown
    && armed === attempt && !!scope.current;
  function decide(target: HTMLElement, value: SessionPermissionCommand, decision: CommandDecision, text = "") {
    const current = scope.current;
    if (!live(target) || !current) return;
    void current.decide(value, decision).then(() => {
      // Another selection has its own composer: the text never goes to it.
      if (!text || !instruct || scope.current !== current) return;
      const original = reviewer.readOriginal();
      // A denial the agent took is followed by what to do instead; otherwise the text waits for the next turn.
      instruct(original?.state === "resolved" && original.origin.handle.attemptId === value.handle.attemptId ? "Steer" : "Queue", text);
    });
    if (text) setInstead("");
  }
  const ready = canReview() && state === shown && armed === attempt;
  // Deny is the answer that means the same for every provider: the run goes on without the action. Stopping the run
  // is the Stop button of the composer.
  const decisions = [["allow_once", "Allow once"], ["deny", "Deny"]] as const;
  // The choices are one list: the arrows move along it, Enter answers with the focused one, Escape denies.
  function move(event: KeyboardEvent<HTMLElement>) {
    const field = event.target instanceof HTMLInputElement;
    if (event.key === "Escape") {
      if (event.repeat || event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229) return;
      event.preventDefault();
      // Escape first takes back the text being written, as in any field.
      if (field && instead) setInstead("");
      else choices.current?.querySelector<HTMLElement>("[data-permission-decision=deny]")?.click();
      return;
    }
    const step = event.key === "ArrowDown" || (!field && event.key === "ArrowRight") ? 1
      : event.key === "ArrowUp" || (!field && event.key === "ArrowLeft") ? -1 : 0;
    const items = Array.from(choices.current?.querySelectorAll<HTMLElement>("[data-permission-choice]") ?? []);
    const index = items.indexOf(event.target as HTMLElement);
    const chosen = field || event.repeat ? -1 : ["1", "2"].indexOf(event.key);
    if (chosen >= 0 && index >= 0) { event.preventDefault(); items[chosen].click(); return; }
    if (!step) return;
    // From the list itself, the arrows enter it at its first or its last choice.
    if (index < 0 && event.target !== choices.current) return;
    event.preventDefault();
    items[index < 0 ? (step > 0 ? 0 : items.length - 1) : (index + step + items.length) % items.length].focus();
  }
  return <section ref={card} className="command-permission-panel" aria-label={t("Pending command permissions")}>
    <h3 id={`${sessionId}-permission-question`}>{t(entry.kind === "commandExecution" ? "Allow this command?" : "Allow file changes under this folder?")}</h3>
    <p className="sr-only" role="status">{t("A request waits for your permission.")}</p>
    {entry.kind === "commandExecution"
      ? <><pre data-permission-command>{entry.command}</pre>
        <p className="detail"><code data-permission-directory>{entry.workingDirectory}</code></p></>
      : <pre data-permission-grant-root>{entry.grantRoot}</pre>}
    {entry.reason !== null && entry.reason.trim() && <p className="detail" data-permission-reason>{entry.reason}</p>}
    <div ref={choices} className="permission-choices" role="group" tabIndex={-1} aria-labelledby={`${sessionId}-permission-question`} onKeyDown={move}>
      {decisions.map(([decision, label], index) => <button key={decision} type="button" data-permission-choice data-permission-decision={decision}
        disabled={!ready} tabIndex={index === 0 ? 0 : -1} onKeyDown={event => { if (event.repeat && event.key === "Enter") event.preventDefault(); }}
        onClick={event => decide(event.currentTarget, entry, decision)}>
        <span className="permission-choice-index" aria-hidden="true">{index + 1}</span>{t(label)}</button>)}
      {instruct && <form className="permission-instead" onSubmit={event => {
        event.preventDefault();
        const text = instead.trim();
        if (text) decide(event.currentTarget, entry, "deny", text);
      }}>
        <input className="bp6-input" data-permission-choice data-permission-instead value={instead} tabIndex={-1}
          disabled={!canReview() || state !== shown} aria-label={t("Or deny, and tell the agent what to do instead")}
          placeholder={t("Or deny, and tell the agent what to do instead")} onChange={event => setInstead(event.target.value)}
          onKeyDown={event => { if (event.key === "Enter" && (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229 || event.repeat)) event.preventDefault(); }} />
      </form>}
    </div>
    <p className="detail permission-keys">{t("Arrow keys choose, Enter answers, Escape denies.")}</p>
    {outcome}
    {(shown.entries.length > 1 || shown.hasMore) && <p className="detail" role="status">{t("More requests are waiting after this one.")}</p>}
  </section>;
}
