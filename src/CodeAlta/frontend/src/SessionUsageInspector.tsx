import { useEffect, useLayoutEffect, useRef, useState, useSyncExternalStore } from "react";
import { sessionUsage, type SessionUsageResponse } from "#neoastra";
import { AppIcon } from "./AppIcon";
import { usageMessage, validateUsage, type UsageTarget } from "./sessionUsage";
import type { createMutationCapability } from "./sessionOperations";

const show = (value: string | number | null) => value === null ? "Unknown" : String(value);

export function SessionUsageInspector({ target, capability }: {
  target: UsageTarget; capability: ReturnType<typeof createMutationCapability>;
}) {
  const trigger = useRef<HTMLButtonElement>(null);
  const dialog = useRef<HTMLDialogElement>(null);
  const request = useRef<AbortController | null>(null);
  const current = useRef(false);
  const composingEscape = useRef(false);
  const [open, setOpen] = useState(false);
  const [pending, setPending] = useState(false);
  const [status, setStatus] = useState("No read requested.");
  const [snapshot, setSnapshot] = useState<SessionUsageResponse | null>(null);
  const allowed = useSyncExternalStore(capability.subscribe, capability.canMutate);
  useLayoutEffect(() => {
    if (!open || !allowed) return;
    const element = dialog.current;
    element?.showModal();
    return () => { if (element?.open) element.close(); };
  }, [open, allowed]);
  useEffect(() => () => { current.current = false; request.current?.abort(); }, []);
  useEffect(() => {
    if (allowed) return;
    current.current = false;
    request.current?.abort();
    setOpen(false);
    setSnapshot(null);
  }, [allowed]);
  function close() {
    current.current = false;
    request.current?.abort();
    request.current = null;
    setOpen(false);
    setSnapshot(null);
    const button = trigger.current;
    requestAnimationFrame(() => { if (!current.current && button?.isConnected && !button.disabled) button.focus(); });
  }
  function read() {
    if (!current.current || !capability.canMutate()) return;
    request.current?.abort();
    const controller = new AbortController();
    request.current = controller;
    setPending(true);
    setSnapshot(null); // A failed refresh must not leave the prior observation looking current.
    setStatus("Reading last-observed usage…");
    void sessionUsage.read({ expectedHostEpoch: target.epoch, sessionId: target.sessionId,
      scope: target.scope, projectId: target.projectId, expectedProjectPath: target.expectedProjectPath },
    { signal: controller.signal, timeoutMilliseconds: 15000 }).then(value => {
      if (!current.current || request.current !== controller || controller.signal.aborted) return;
      if (value.status === "stale_epoch" || value.hostEpoch !== target.epoch) {
        capability.observe({ status: "stale_epoch", epoch: value.hostEpoch });
        if (!capability.canMutate()) return;
      }
      if (!capability.canMutate()) return;
      const verified = validateUsage(target, value);
      if (!verified) { setStatus("Invalid or foreign usage response; no observation established."); return; }
      if (snapshotRuntime.current && verified.runtimeInstanceId && verified.runtimeInstanceId !== snapshotRuntime.current) {
        capability.observe({ status: "stale_runtime", epoch: target.epoch });
        setStatus("Runtime changed; reload before reading usage."); return;
      }
      if (verified.runtimeInstanceId) snapshotRuntime.current = verified.runtimeInstanceId;
      setSnapshot(verified);
      setStatus(verified.status === "ok" ? "Last observed on this attachment; not current occupancy or a cumulative total."
        : usageMessage(verified.status));
    }).catch(() => {
      if (current.current && request.current === controller && !controller.signal.aborted)
        setStatus("Usage read failed; no observation established. Refresh explicitly if needed.");
    }).finally(() => { if (current.current && request.current === controller) setPending(false); });
  }
  const snapshotRuntime = useRef<string | null>(null);
  function openDialog() {
    if (!capability.canMutate() || current.current) return;
    current.current = true;
    snapshotRuntime.current = null;
    setOpen(true);
    read();
  }
  const observation = snapshot?.status === "ok" ? snapshot.observation : null;
  return <>
    <button ref={trigger} type="button" className="composer-icon-button" disabled={!allowed} aria-label="Inspect last-observed session usage"
      aria-haspopup="dialog" aria-expanded={open && allowed} title="Inspect last-observed usage (explicit read)" onClick={openDialog}>
      <AppIcon name="usage" size={16} /></button>
    {open && allowed && <dialog ref={dialog} className="app-dialog session-usage-dialog" aria-modal="true"
      aria-labelledby="session-usage-title" aria-describedby="session-usage-description"
      onKeyDown={event => { event.stopPropagation(); if (event.key !== "Escape") return;
        event.preventDefault(); if (!event.nativeEvent.isComposing && event.nativeEvent.keyCode !== 229) close();
        else composingEscape.current = true;
      }} onKeyUp={() => { composingEscape.current = false; }}
      onCompositionEnd={() => { composingEscape.current = false; }}
      onCancel={event => { event.preventDefault(); if (!composingEscape.current) close(); }}>
      <header><div><span className="eyebrow">Owned session</span><h2 id="session-usage-title">Last-observed usage</h2></div>
        <button type="button" className="icon-button" aria-label="Close usage inspector" onClick={close}><AppIcon name="close" size={16} /></button></header>
      <p id="session-usage-description" className="muted-text">One admitted provider event, not a live context measurement, complete history or inferred total. Unknown is not zero.</p>
      <p role="status">{status}</p>
      {observation && <dl className="session-info-fields session-usage-fields" tabIndex={0} aria-label="Last-observed usage fields">
        <div><dt>Attachment / event sequence</dt><dd><code>{snapshot?.attachmentGeneration}</code> / <code>{observation.sequence}</code></dd></div>
        <div><dt>Source / reported scope</dt><dd>{observation.source} / {observation.scope}</dd></div>
        <div><dt>Usage source time / event time</dt><dd>{show(observation.sourceUpdatedAt)} / {show(observation.eventTimestamp)}</dd></div>
        <div><dt>Reported window tokens / limit / messages</dt><dd>{show(observation.window?.currentTokens ?? null)} / {show(observation.window?.tokenLimit ?? null)} / {show(observation.window?.messageCount ?? null)}</dd></div>
        <div><dt>Last-operation input / output</dt><dd>{show(observation.lastOperation?.inputTokens ?? null)} / {show(observation.lastOperation?.outputTokens ?? null)}</dd></div>
        <div><dt>Last-operation cache read / write / reused input / reasoning</dt><dd>{show(observation.lastOperation?.cacheReadTokens ?? null)} / {show(observation.lastOperation?.cacheWriteTokens ?? null)} / {show(observation.lastOperation?.cachedInputTokens ?? null)} / {show(observation.lastOperation?.reasoningTokens ?? null)}</dd></div>
        <div><dt>Last-operation reported cost (currency unspecified) / duration (ms)</dt><dd>{show(observation.lastOperation?.cost ?? null)} / {show(observation.lastOperation?.durationMs ?? null)}</dd></div>
        <div><dt>Invalid values / omitted data / mismatched usage callbacks</dt><dd>{observation.hadInvalidValues ? "Yes" : "No"} / {observation.hadOmittedData ? "Yes" : "No"} / {snapshot?.omittedUsageEvents}</dd></div>
      </dl>}
      {snapshot?.status === "no_observation" && <p>Attachment {snapshot.attachmentGeneration}; no admitted usage event. Mismatched usage callbacks: {snapshot.omittedUsageEvents}.</p>}
      <footer><span>Point-in-time; external metadata changes can race this read.</span><span><button type="button" className="quiet-button" disabled={pending} onClick={read}>Refresh usage</button>{" "}
        <button type="button" className="quiet-button" onClick={close}>Close</button></span></footer>
    </dialog>}
  </>;
}
