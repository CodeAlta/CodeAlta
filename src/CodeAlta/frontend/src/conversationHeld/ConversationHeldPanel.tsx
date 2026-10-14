import { useEffect, useLayoutEffect, useRef, useState } from "react";
import type { ConversationHeldEntry } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import { choiceLabels, holderDetail, offered, originKey, originKeyFromText, type createConversationHeldReviewer,
  type HeldChoice, type HeldNotice, type HeldState } from "./conversationHeld";

// How often the questions of a running session are read while none is shown.
const pendingReadInterval = 1_500;
// A question that just appeared takes the place the pointer may be about to click: its choices wait this long.
const armDelay = 400;
// How long the line that says an answer was not taken stays.
const noticeDuration = 8_000;

/**
 * The question of a run whose Claude Code conversation another process holds (VS Code, a terminal), in the timeline
 * notices of its session: the choices it offers, then, after Wait, the line that says the run waits, with Stop. It
 * reads the questions while the session runs and none is shown, once more when the run ends with one shown, and
 * after each answer, whatever its reply: an answer is never sent again.
 */
export function ConversationHeldPanel({ reviewer, epoch, sessionId, running, wait, canReview, onStop, stopDisabled = false }: {
  reviewer: ReturnType<typeof createConversationHeldReviewer>; epoch: string; sessionId: string;
  /** Whether the session has a run that may ask. */
  running: boolean;
  /** The wait the run reports, from its session updates: null when it does not wait. */
  wait: { originText: string } | null;
  canReview: () => boolean;
  /** Stops the run, as the Stop button of the composer. */
  onStop?: () => void;
  stopDisabled?: boolean;
}) {
  const { t } = useShellLanguage();
  const [state, setState] = useState<HeldState>();
  // The questions last read: a read in progress keeps them on screen.
  const [shown, setShown] = useState<Extract<HeldState, { kind: "ready" }> | null>(null);
  const [poll, setPoll] = useState(0);
  const [armed, setArmed] = useState<string | null>(null);
  // An answer that was not taken is said until the next question, even while the questions are read again.
  const [notice, setNotice] = useState<HeldNotice | null>(null);
  const scope = useRef<ReturnType<typeof reviewer.forSelection> | null>(null);
  useLayoutEffect(() => {
    const controller = new AbortController();
    setState(undefined); setShown(null); setArmed(null); setNotice(null);
    scope.current = reviewer.forSelection(epoch, sessionId, controller.signal, value => {
      setState(value);
      if (value.kind === "ready" && value.notice) setNotice(value.notice);
      else if (value.kind === "resolving" || (value.kind === "ready" && value.entries.length > 0)) setNotice(null);
      if (value.kind === "ready") setShown(value);
      else if (value.kind !== "loading") setShown(null);
    });
    return () => { controller.abort(); scope.current = null; };
  }, [reviewer, epoch, sessionId]);
  const entry = shown?.entries[0];
  useEffect(() => {
    const current = scope.current;
    if (!current || state?.kind === "loading" || state?.kind === "resolving" || (state?.kind === "error" && state.reloadRequired)) return;
    // A question that is shown waits for the user: it is not read again before an answer or the end of the run.
    if (!running || entry) return;
    const timer = setTimeout(() => {
      if (canReview()) void current.refresh();
      setPoll(value => value + 1);
    }, state === undefined ? 0 : pendingReadInterval);
    return () => clearTimeout(timer);
  }, [state, entry, running, poll]);
  const wasRunning = useRef(running);
  useEffect(() => {
    // A run that ends takes its question with it: one read shows what is left.
    if (wasRunning.current && !running && entry && canReview()) void scope.current?.refresh();
    wasRunning.current = running;
  }, [running]);
  useEffect(() => {
    if (!notice) return;
    const timer = setTimeout(() => setNotice(null), noticeDuration);
    return () => clearTimeout(timer);
  }, [notice]);
  const attempt = entry?.handle.attemptId ?? null;
  useEffect(() => {
    if (attempt === null) { setArmed(null); return; }
    const timer = setTimeout(() => setArmed(attempt), armDelay);
    return () => clearTimeout(timer);
  }, [attempt]);

  const origin = (value: ConversationHeldEntry) => { const key = originKey(value.holder.origin, value.holder.originText); return key ? t(key) : value.holder.originText; };
  const outcome = <>
    {state?.kind === "resolving" && <p role="status">{t("Sending your choice…")}</p>}
    {notice && <p role="status" data-held-notice={notice}>{t(notice === "rejected" || notice === "invalid_request"
      ? "Your choice was not taken: the question had ended or changed." : "Whether your choice was taken is not known: what still waits is shown.")}</p>}
    {state?.kind === "error" && <p role="alert">{t("The question of the held conversation is unavailable ({code}).", { code: state.code })} {state.reloadRequired && t("Reload the window to answer it.")}</p>}
  </>;
  if (!entry) {
    const waitKey = wait ? originKeyFromText(wait.originText) : null;
    const waiting = running && wait !== null && state?.kind !== "resolving";
    if (!waiting && state?.kind !== "resolving" && state?.kind !== "error" && !notice) return null;
    return <section className="conversation-held-panel" aria-label={t("Claude Code conversation open elsewhere")} data-held-waiting={waiting ? "" : undefined}>
      {waiting && <div className="conversation-held-wait">
        <ActivitySpinner size={14} />
        <p role="status">{t("Waiting for {origin} to release the conversation…", { origin: waitKey ? t(waitKey) : wait!.originText })}</p>
        {onStop && <button type="button" className="conversation-held-stop" data-held-stop disabled={stopDisabled} onClick={onStop}>
          <AppIcon name="stop" size={13} fill="currentColor" />{t("Stop the run")}</button>}
      </div>}
      {outcome}
    </section>;
  }
  const ready = canReview() && state === shown && armed === attempt;
  const detail = holderDetail(entry);
  const list = offered(entry);
  function answer(target: HTMLElement, choice: HeldChoice) {
    const current = scope.current;
    if (!current || !entry || !target.isConnected || target.closest("[inert]") || !canReview() || armed !== attempt) return;
    void current.answer(entry, choice);
  }
  return <section className="conversation-held-panel" aria-label={t("Claude Code conversation open elsewhere")}>
    <h3 id={`${sessionId}-held-question`}>{t("This Claude Code conversation is open in {origin}", { origin: origin(entry) })}</h3>
    {(detail.processId !== null || detail.workingDirectory) && <p className="detail" data-held-detail>
      {detail.processId !== null && <span>{t("Process {pid}", { pid: detail.processId })}</span>}
      {detail.workingDirectory && <code>{detail.workingDirectory}</code>}
    </p>}
    <div className="conversation-held-choices" role="group" aria-labelledby={`${sessionId}-held-question`}>
      {list.filter(choice => choice !== "cancel").map(choice => <button key={choice} type="button" data-held-choice={choice} disabled={!ready}
        onKeyDown={event => { if (event.repeat && event.key === "Enter") event.preventDefault(); }}
        onClick={event => answer(event.currentTarget, choice)}>
        <span>{t(choiceLabels[choice])}</span>
        {choice === "openAnyway" && <span className="detail">{t("Both would write the same conversation")}</span>}
      </button>)}
      {list.includes("cancel") && <button type="button" className="conversation-held-cancel" data-held-choice="cancel" disabled={!ready}
        onClick={event => answer(event.currentTarget, "cancel")}>{t(choiceLabels.cancel)}</button>}
    </div>
    {outcome}
  </section>;
}
