import { useLayoutEffect, useRef, useState, useSyncExternalStore } from "react";
import type { createQueueSubmissions } from "./sessionQueue";
import type { createSteeringSubmissions } from "./sessionSteering";
import { useShellLanguage } from "./shellLanguage";
import type { MessageKey } from "./localization";

type Request = Readonly<{ expectedEpoch: string; sessionId: string; clientRequestId: string; text: string;
  expectedRuntimeInstanceId: string; expectedAttachmentGeneration: string; expectedRunId?: string }>;

// A read-only projection of this app's retained owner requests, never a queued-prompt inventory.
export function RetainedRequestStrip({ epoch, sessionId, queue, steering }: { epoch: string; sessionId: string;
  queue: ReturnType<typeof createQueueSubmissions>; steering: ReturnType<typeof createSteeringSubmissions> }) {
  const { t } = useShellLanguage();
  useSyncExternalStore(queue.subscribe, queue.getSnapshot);
  useSyncExternalStore(steering.subscribe, steering.getSnapshot);
  const queued = queue.pending(sessionId);
  const steered = steering.pending(sessionId);
  const queueEntry = queued?.request.expectedEpoch === epoch && queued.request.sessionId === sessionId ? queued : null;
  const steerEntry = steered?.request.expectedEpoch === epoch && steered.request.sessionId === sessionId ? steered : null;
  if (!queueEntry && !steerEntry) return null;
  return <section className="retained-intent-strip" aria-label={t("Retained queue and steering requests")}>
    <p className="detail">{t("Retained requests in this app only; not a queued-prompt list. Inspect receipts manually in the existing controls.")}</p>
    {queueEntry && <RetainedRow key={`Queue:${epoch}:${sessionId}:${queueEntry.request.clientRequestId}`} kind="Queue"
      owner={queue} request={queueEntry.request} inFlight={queueEntry.inFlight} />}
    {steerEntry && <RetainedRow key={`Steer:${epoch}:${sessionId}:${steerEntry.request.clientRequestId}`} kind="Steer"
      owner={steering} request={steerEntry.request} inFlight={steerEntry.inFlight} />}
  </section>;
}

function RetainedRow({ kind, owner, request, inFlight }: { kind: "Queue" | "Steer"; owner: object; request: Request; inFlight: boolean }) {
  const { t } = useShellLanguage();
  const [feedback, setFeedback] = useState<MessageKey | "">("");
  const active = useRef(false);
  const copySequence = useRef(0);
  useLayoutEffect(() => {
    active.current = true;
    setFeedback("");
    return () => { active.current = false; copySequence.current++; };
  }, [owner, request]);
  async function copy() {
    const sequence = ++copySequence.current;
    const text = request.text; // Capture the exact immutable owner request, not a mutable editor or receipt.
    setFeedback("");
    let result: MessageKey;
    try {
      if (!navigator.clipboard?.writeText) result = "Clipboard unavailable; retained text unchanged.";
      else { await navigator.clipboard.writeText(text); result = "Retained text copied."; }
    } catch { result = "Copy failed; retained text unchanged."; }
    if (active.current && sequence === copySequence.current) setFeedback(result);
  }
  return <div className="retained-intent-row" data-kind={kind}>
    <div className="retained-intent-heading">
      <details><summary>{t(kind === "Queue" ? "Host-only Queue" : "Steer")} · {t(inFlight
        ? "Exact-request waiter pending" : "Outcome unknown · manual recovery")}</summary>
        <p className="detail">{t(kind === "Queue" ? "Reservation is not host insertion, durability or execution."
          : "Steering admission is not run completion.")} {t("No automatic retry or retargeting.")}</p>
        <dl className="retained-intent-evidence">
          <dt>{t("Host epoch")}</dt><dd><code>{request.expectedEpoch}</code></dd>
          <dt>{t("Session")}</dt><dd><code>{request.sessionId}</code></dd>
          <dt>{t("Runtime")}</dt><dd><code>{request.expectedRuntimeInstanceId}</code></dd>
          <dt>{t("Attachment")}</dt><dd><code>{request.expectedAttachmentGeneration}</code></dd>
          {kind === "Steer" && <><dt>{t("Run")}</dt><dd><code>{request.expectedRunId}</code></dd></>}
          <dt>{t("Request")}</dt><dd><code>{request.clientRequestId}</code></dd>
        </dl>
        <pre aria-label={t(kind === "Queue" ? "Retained Queue text" : "Retained Steer text")}>{request.text}</pre>
      </details>
      <button type="button" className="quiet-button" aria-label={t(kind === "Queue" ? "Copy retained Queue text" : "Copy retained Steer text")} onClick={() => void copy()}>{t("Copy text")}</button>
    </div>
    {feedback && <p className="detail" role="status">{t(feedback)}</p>}
  </div>;
}
