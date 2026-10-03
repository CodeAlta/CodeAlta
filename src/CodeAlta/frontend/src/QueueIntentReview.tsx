import { AppWindowSurface } from "./AppWindow";
import { useLayoutEffect, useRef, useState, useSyncExternalStore } from "react";
import type { createQueueSubmissions } from "./sessionQueue";
import { retainedQueueEvidence } from "./retainedQueueEvidence";
import { useShellLanguage } from "./shellLanguage";

type Evidence = NonNullable<ReturnType<typeof retainedQueueEvidence>>;
type Review = { evidence: Evidence; current: () => boolean; origin: HTMLButtonElement };
type CopyFeedback = "Retained text copied." | "Clipboard unavailable; retained text unchanged." | "Copy failed; retained text unchanged.";

export function QueueIntentReview({ owner, epoch, sessionId, capture }: {
  owner: ReturnType<typeof createQueueSubmissions>; epoch: string; sessionId: string;
  capture: () => (() => boolean) | null;
}) {
  const { t } = useShellLanguage();
  useSyncExternalStore(owner.subscribe, owner.getSnapshot);
  const evidence = retainedQueueEvidence(owner, epoch, sessionId);
  const [review, setReview] = useState<Review | null>(null);
  const latest = useRef(review); latest.current = review;
  const current = (value: Review) => latest.current === value && value.current()
    && owner.getSnapshot() === value.evidence.revision && value.origin.isConnected;
  const visible = review && current(review) ? review : null;
  useLayoutEffect(() => { if (review && !visible) { latest.current = null; setReview(null); } }, [review, visible]);
  function close(restore = false) {
    const value = latest.current;
    const focus = restore && value && current(value);
    latest.current = null; setReview(null);
    if (focus) requestAnimationFrame(() => {
      if (value.current() && value.origin.isConnected && !document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) value.origin.focus();
    });
  }
  if (!evidence) return null;
  return <section className="queue-intent-compact" aria-label={t("Retained queue intent")}>
    <span>{t("Retained queue intent")}{evidence.queue && <> · {t(evidence.queue.inFlight ? "Exact-request waiter pending" : "Outcome unknown · manual recovery")}</>}</span>
    {evidence.queue && <span className="queue-intent-preview">{evidence.queue.request.text.slice(0, 160)}</span>}
    <button type="button" className="quiet-button queue-intent-review-trigger" aria-haspopup="dialog" aria-expanded={!!visible}
      onKeyDown={event => { if (event.repeat || event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229) { event.preventDefault(); event.stopPropagation(); } }}
      onClick={event => {
        if (document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) return;
        const fence = capture(); const original = retainedQueueEvidence(owner, epoch, sessionId);
        if (!fence || !fence() || !original) return;
        const value = { evidence: original, current: fence, origin: event.currentTarget };
        latest.current = value; setReview(value);
      }}>{t("Review retained intent")}</button>
    {visible && <QueueIntentDialog review={visible} current={() => current(visible)} close={close} />}
  </section>;
}

function QueueIntentDialog({ review, current, close }: { review: Review; current: () => boolean; close: (restore?: boolean) => void }) {
  const { t } = useShellLanguage();
  const dialog = useRef<HTMLDialogElement>(null);
  const alive = useRef(false);
  const retired = useRef(false);
  const copying = useRef(false);
  const composing = useRef(false);
  const [feedback, setFeedback] = useState<CopyFeedback | null>(null);
  const valid = () => alive.current && !retired.current && current() && !!dialog.current?.isConnected && !!dialog.current.open;
  useLayoutEffect(() => {
    const element = dialog.current!;
    alive.current = true;
    let opening = !element.open;
    const modal = (event: Event) => {
      if (!(event.target instanceof HTMLDialogElement)) return;
      if (opening && event.target === element && (event as ToggleEvent).newState === "open") { opening = false; return; }
      retired.current = true; close();
    };
    document.addEventListener("beforetoggle", modal, true);
    if (!element.open) element.showModal();
    element.querySelector<HTMLButtonElement>("header button")?.focus();
    return () => { alive.current = false; document.removeEventListener("beforetoggle", modal, true); };
  }, []);
  async function copy() {
    const original = review.evidence.queue;
    if (!original || !valid() || copying.current) return;
    copying.current = true;
    let result: CopyFeedback = "Retained text copied.";
    try {
      if (!navigator.clipboard?.writeText) result = "Clipboard unavailable; retained text unchanged.";
      else await navigator.clipboard.writeText(original.request.text);
    } catch { result = "Copy failed; retained text unchanged."; }
    copying.current = false;
    if (valid()) setFeedback(result);
  }
  const queue = review.evidence.queue;
  return <dialog ref={dialog} className="app-dialog session-info-dialog queue-intent-dialog" aria-modal="true" aria-labelledby="queue-intent-title"
    onKeyDown={event => {
      event.stopPropagation();
      if (event.key === "Escape") {
        event.preventDefault(); composing.current = event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229;
        if (!composing.current && !event.repeat) close(true);
      } else if ((event.repeat || event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229) && ["Enter", " "].includes(event.key)) event.preventDefault();
    }} onKeyUp={() => { composing.current = false; }} onCompositionEnd={() => { composing.current = false; }}
    onCancel={event => { event.preventDefault(); if (!composing.current) close(true); }}>
    <AppWindowSurface storageKey="codealta.desktop.window.queue-intent.v1" title={t("Retained queue intent")} titleId="queue-intent-title" preferredSize={viewport => ({ width: Math.min(600, viewport.width - 40), height: Math.min(480, viewport.height - 40) })}
      onClose={() => close(true)} closeLabel={t("Close")}>
    <p>{t("Local unresolved originals only; not live queue inventory or settled text. Reservation is not execution or durable storage. Existing controls retain all retry, cancellation and receipt-refresh authority.")}</p>
    {queue && <><h3>{t("Host-only Queue")}</h3><p>{t(queue.inFlight ? "Exact-request waiter pending" : "Outcome unknown · manual recovery")}</p>
      <dl className="session-info-fields">{Object.entries(queue.request).filter(([key]) => key !== "text").map(([key, value]) => <div key={key}><dt>{key}</dt><dd><code>{value}</code></dd></div>)}</dl>
      <pre className="queue-intent-text" tabIndex={0}>{queue.request.text}</pre></>}
    {review.evidence.cancellations.map(value => <section key={value.intent.request.clientRequestId}>
      <h3>{t("Retained cancellation intent")}</h3><p>{t(value.inFlight ? "Exact-request waiter pending" : "Outcome unknown · manual recovery")}</p>
      <pre tabIndex={0}>{JSON.stringify(value.intent, null, 2)}</pre>
    </section>)}
    <footer><span role="status">{feedback && t(feedback)}</span>{queue && <button type="button" className="quiet-button queue-intent-copy" onClick={() => void copy()}>{t("Copy text")}</button>}</footer>
  </AppWindowSurface></dialog>;
}
