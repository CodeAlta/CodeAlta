import { useEffect, useMemo, useRef, useState, useSyncExternalStore, type ReactNode } from "react";
import { Button, Callout, Tag } from "@blueprintjs/core";
import { providerUsage, sessionUsage, type SessionUsageObservation, type SessionUsageResponse } from "#neoastra";
import { AppIcon } from "./AppIcon";
import { ProviderIcon } from "./ProviderIcon";
import { AppWindow } from "./AppWindow";
import { compactTokens, contextSegments, contextUsage, costText, groupedTokens, mergeUsageObservation, operationSegments, persistedContextUsage,
  persistedOperation, persistedUsageFields, usageIntent, usageMarkdown, type UsageSegment } from "./contextUsage";
import { UsageLimitList, usageNote, useProviderUsage } from "./UsageLimits";
import { limitsMarkdown, liveLimitsAreCurrent, planLabel, providerLimits, sessionLimits } from "./subscriptionUsage";
import { usageMessage, validateUsage, type UsageTarget } from "./sessionUsage";
import type { createMutationCapability } from "./sessionOperations";
import { useShellLanguage } from "./shellLanguage";
import type { MessageKey } from "./localization";

const minimumReadInterval = 4000;
const segmentLabels: Record<string, MessageKey> = { active: "Active context", headroom: "Input headroom", input: "Input", output: "Output",
  cacheRead: "Cache read", cacheWrite: "Cache write", cachedInput: "Cached input", reasoning: "Reasoning" };

/** A proportional stacked bar with its legend (value and share per slice), the TUI's breakdown chart. */
function UsageBreakdown({ segments, label }: { segments: readonly UsageSegment[]; label: string }) {
  const { t } = useShellLanguage();
  if (!segments.length) return null;
  return <div className="usage-breakdown" role="img" aria-label={`${label}: ${segments.map(segment => `${t(segmentLabels[segment.key])} ${groupedTokens(segment.tokens)} (${segment.share}%)`).join(", ")}`}>
    <div className="usage-breakdown-bar" aria-hidden="true">{segments.map(segment =>
      <span key={segment.key} data-segment={segment.key} style={{ flexGrow: Math.max(segment.share, .6) }} />)}</div>
    <ul className="usage-breakdown-legend" aria-hidden="true">{segments.map(segment => <li key={segment.key} data-segment={segment.key}>
      <span className="usage-breakdown-dot" /><span>{t(segmentLabels[segment.key])}</span>
      <strong>{groupedTokens(segment.tokens)}</strong><small>{segment.share}%</small></li>)}</ul>
  </div>;
}

function UsageCard({ title, aside, children }: { title: ReactNode; aside?: ReactNode; children: ReactNode }) {
  return <section className="usage-card"><header><h3>{title}</h3>{aside && <span className="usage-card-aside">{aside}</span>}</header>{children}</section>;
}

/**
 * Compact context-usage meter for the composer bar, with a details window that carries what the TUI
 * usage popup shows: context pressure, the last operation's token breakdown, the usage of the subscription and the
 * provider's session totals. The usage of the subscription is what the last turns reported while that is recent;
 * otherwise the provider is asked, when the window opens and on a refresh only. It reads the last observed usage when the session is shown and again
 * (at most every few seconds) when `refreshKey` changes; until the host has an observation it falls
 * back to the last persisted usage record of the loaded timeline.
 */
export function SessionUsageInspector({ target, capability, refreshKey, persisted, provider, model, subscription = providerUsage }: {
  target: UsageTarget; capability: ReturnType<typeof createMutationCapability>;
  /** Changes when a new observation is likely (run state or live revision). */
  refreshKey?: string;
  /** Text of the newest persisted usage record in the loaded timeline, if any. */
  persisted?: string | null;
  /** Session provider and selected model, shown until an operation reports its own model. */
  provider?: string | null; model?: string | null;
  /** Reads the usage of the subscription of the provider, for a session whose turns reported none. */
  subscription?: Pick<typeof providerUsage, "read">;
}) {
  const { t } = useShellLanguage();
  const trigger = useRef<HTMLButtonElement>(null);
  const request = useRef<AbortController | null>(null);
  const lastRead = useRef(0);
  const runtime = useRef<string | null>(null);
  // Fields accumulate across the events of one attachment; a new attachment starts over.
  const attachment = useRef<string | null>(null);
  const [open, setOpen] = useState(false);
  const [pending, setPending] = useState(false);
  const [copied, setCopied] = useState<boolean | null>(null);
  const [status, setStatus] = useState<MessageKey | { raw: string } | null>(null);
  const [snapshot, setSnapshot] = useState<SessionUsageResponse | null>(null);
  const [observation, setObservation] = useState<SessionUsageObservation | null>(null);
  const allowed = useSyncExternalStore(capability.subscribe, capability.canMutate);

  function read() {
    if (!capability.canMutate()) return;
    request.current?.abort();
    const controller = new AbortController();
    request.current = controller;
    lastRead.current = Date.now();
    setPending(true);
    void sessionUsage.read({ expectedHostEpoch: target.epoch, sessionId: target.sessionId,
      scope: target.scope, projectId: target.projectId, expectedProjectPath: target.expectedProjectPath },
    { signal: controller.signal, timeoutMilliseconds: 15000 }).then(value => {
      if (request.current !== controller || controller.signal.aborted) return;
      if (value.status === "stale_epoch" || value.hostEpoch !== target.epoch) capability.observe({ status: "stale_epoch", epoch: value.hostEpoch });
      if (!capability.canMutate()) return;
      const verified = validateUsage(target, value);
      if (!verified) { setSnapshot(null); setObservation(null); setStatus("Invalid or foreign usage response; no observation established."); return; }
      if (runtime.current && verified.runtimeInstanceId && verified.runtimeInstanceId !== runtime.current) {
        capability.observe({ status: "stale_runtime", epoch: target.epoch });
        setSnapshot(null); setObservation(null); setStatus("Runtime changed; reload before reading usage."); return;
      }
      if (verified.runtimeInstanceId) runtime.current = verified.runtimeInstanceId;
      setSnapshot(verified);
      setStatus(verified.status === "ok" ? null : { raw: usageMessage(verified.status) });
      const identity = verified.attachmentGeneration ? `${verified.runtimeInstanceId}:${verified.attachmentGeneration}` : null;
      if (verified.status === "ok" && verified.observation) {
        const sameAttachment = attachment.current === identity;
        attachment.current = identity;
        const incoming = verified.observation;
        setObservation(previous => sameAttachment ? mergeUsageObservation(previous, incoming) : incoming);
      } else if (identity && attachment.current !== identity) {
        attachment.current = identity; setObservation(null);
      }
    }).catch(() => {
      // A failed refresh keeps the previous observation; the details window says the read failed.
      if (request.current === controller && !controller.signal.aborted) setStatus("Usage read failed; no observation established. Refresh explicitly if needed.");
    }).finally(() => { if (request.current === controller) setPending(false); });
  }
  // Read when shown and whenever the refresh key changes, but never more often than the minimum interval.
  useEffect(() => {
    if (!allowed) return;
    const wait = Math.max(0, lastRead.current + minimumReadInterval - Date.now());
    const timer = setTimeout(read, wait);
    return () => clearTimeout(timer);
  }, [allowed, refreshKey]);
  useEffect(() => () => request.current?.abort(), []);
  useEffect(() => { if (!allowed) { request.current?.abort(); setOpen(false); setSnapshot(null); setObservation(null); attachment.current = null; } }, [allowed]);
  useEffect(() => { if (copied === null) return; const timer = setTimeout(() => setCopied(null), 2500); return () => clearTimeout(timer); }, [copied]);

  const observed = contextUsage(observation?.window?.currentTokens, observation?.window?.tokenLimit);
  const usage = observed ?? persistedContextUsage(persisted);
  const fromHistory = !observed && !!usage;
  const percent = usage?.percent ?? null;
  const intent = usageIntent(percent);
  const summary = usage ? `${compactTokens(usage.used)}${usage.limit ? ` / ${compactTokens(usage.limit)}` : ""}` : null;
  const label = usage
    ? t("Context usage: {summary}", { summary: percent === null ? summary! : `${Math.round(percent)}% · ${summary}` })
    : t("Context usage unknown");
  const operation = observation?.lastOperation ?? persistedOperation(persisted);
  const history = new Map(persistedUsageFields(persisted).map(field => [field.label.toLowerCase(), field.value]));
  const savedMessages = history.get("messages in context");
  const messages = observation?.window?.messageCount ?? (savedMessages && /^\d+$/.test(savedMessages) ? Number(savedMessages) : null);
  const modelName = operation?.model ?? model ?? null;
  const limits = observation?.rateLimits ?? null;
  const total = observation?.sessionTotal ?? null;
  const updated = observation?.sourceUpdatedAt ?? observation?.eventTimestamp ?? null;
  const updatedTime = updated && !Number.isNaN(new Date(updated).getTime()) ? new Date(updated).toLocaleTimeString([], { hour12: false }) : null;
  const envelope = [observation?.window?.totalContextEnvelope && t("context window {tokens} tokens", { tokens: groupedTokens(observation.window.totalContextEnvelope) }),
    observation?.window?.maxOutputTokens && t("max output {tokens} tokens", { tokens: groupedTokens(observation.window.maxOutputTokens) })].filter(Boolean).join("; ");
  const operationFacts = operation ? [operation.reasoningEffort && t("effort {effort}", { effort: operation.reasoningEffort }),
    operation.initiator && t("initiator {initiator}", { initiator: operation.initiator }),
    operation.durationMs && Number.isFinite(Number(operation.durationMs)) && t("duration {duration} ms", { duration: String(Math.round(Number(operation.durationMs))) }),
    operation.cost && t("cost {cost}", { cost: costText(operation) ?? operation.cost })].filter((value): value is string => !!value) : [];
  // What the turns of the session reported is shown while it is recent; otherwise the provider is asked.
  const live = useMemo(() => sessionLimits(limits), [limits]);
  const liveCurrent = liveLimitsAreCurrent(live, updated, Date.now());
  const asked = useProviderUsage(target.epoch, provider ?? null, open && allowed && !liveCurrent, subscription);
  const reading = asked.response?.status === "ok" && asked.response.limits.length > 0 ? asked.response : null;
  const shownLimits = liveCurrent || !reading ? live : providerLimits(reading.limits);
  const plan = liveCurrent || !reading ? limits?.planType ?? null : reading.plan;
  const subscriptionNote = !shownLimits.length && asked.response?.supported ? usageNote(asked.response) : null;
  async function copy() {
    const markdown = [usageMarkdown({ provider: provider ?? null, model: modelName, usage, messages, window: observation?.window ?? null,
      operation, rateLimits: limits, sessionTotal: total }), ...(liveCurrent || !reading ? [] : limitsMarkdown(shownLimits, plan, Date.now()))].join("\n");
    try { await navigator.clipboard.writeText(markdown); setCopied(true); } catch { setCopied(false); }
  }
  const operations = operationSegments(operation);
  return <>
    <Button ref={trigger} id="session-usage-trigger" data-usage-target={JSON.stringify(target)} variant="minimal" className="context-usage" data-intent={intent}
      disabled={!allowed} aria-label={label} aria-haspopup="dialog" aria-expanded={open && allowed} title={label} onClick={() => { if (capability.canMutate()) { setOpen(true); read(); } }}>
      <span className="context-usage-meter" aria-hidden="true"><span style={{ width: `${percent ?? 0}%` }} /></span>
      <span className="context-usage-text">{usage ? percent === null ? summary : `${Math.round(percent)}%` : <AppIcon name="usage" size={15} />}</span>
      {usage && percent !== null && <span className="context-usage-tokens">{summary}</span>}
    </Button>
    {open && allowed && <AppWindow storageKey="codealta.desktop.window.usage.v2" className="session-usage-dialog" titleId="session-usage-title" title={t("Context usage")}
      preferredSize={viewport => ({ width: Math.min(620, viewport.width - 40), height: Math.min(720, viewport.height - 40) })} minimumSize={{ width: 400, height: 320 }}
      onClose={() => setOpen(false)} closeLabel={t("Close usage inspector")}
      onCancel={event => { event.preventDefault(); setOpen(false); }} onKeyDown={event => event.stopPropagation()}
      headerActions={<>
        <Button variant="minimal" size="small" icon={<AppIcon name={copied ? "check" : "copy"} size={15} />} aria-label={t("Copy as Markdown")} title={t("Copy as Markdown")} onClick={() => void copy()} />
        <Button variant="minimal" size="small" icon={<AppIcon name="refresh" size={15} />} disabled={pending} aria-label={t("Refresh usage")} title={t("Refresh usage")} onClick={() => { read(); if (!liveCurrent) asked.refresh(); }} /></>}>
      <div className="context-usage-details">
        <header className="context-usage-subject">
          <div><strong className="with-logo"><ProviderIcon providerKey={provider} size={15} />{provider ?? t("session provider")}</strong>{modelName && <span>{modelName}</span>}</div>
          <div>{fromHistory && <Tag minimal round>{t("From saved history")}</Tag>}
            {updatedTime && <span className="bp6-text-muted">{t("updated {time}", { time: updatedTime })}</span>}
            <span role="status" className="bp6-text-muted">{copied === true ? t("Usage copied.") : copied === false ? t("Clipboard unavailable; nothing copied.") : ""}</span></div>
        </header>
        <UsageCard title={t("Context window")} aside={messages !== null && t("{count} messages", { count: messages })}>
          <div className="context-usage-headline" data-intent={intent}><strong>{percent === null ? "—" : `${percent.toFixed(1)}%`}</strong>
            <span>{usage ? usage.limit ? t("{used} / {limit} input tokens", { used: groupedTokens(usage.used), limit: groupedTokens(usage.limit) })
              : t("{used} tokens", { used: groupedTokens(usage.used) }) : t("Waiting for usage data from the active session.")}</span></div>
          <UsageBreakdown segments={contextSegments(usage)} label={t("Context window")} />
          {envelope && <p className="usage-card-note">{t("Indicative model limits")}: {envelope}</p>}
        </UsageCard>
        {operation && <UsageCard title={operation.label ?? t("Last operation")} aside={operationFacts.join(" · ")}>
          {operations.length ? <UsageBreakdown segments={operations} label={operation.label ?? t("Last operation")} />
            : <p className="usage-card-note">{t("No token counts were reported for this operation.")}</p>}
        </UsageCard>}
        {(shownLimits.length > 0 || subscriptionNote || asked.loading && !liveCurrent) && <UsageCard title={t("Subscription usage")} aside={planLabel(plan)}>
          {shownLimits.length > 0 ? <UsageLimitList limits={shownLimits} />
            : <p className="usage-card-note">{subscriptionNote ? t(subscriptionNote.key, subscriptionNote.tool ? { tool: subscriptionNote.tool } : undefined) : t("Reading the usage…")}</p>}
        </UsageCard>}
        {total && <UsageCard title={t("Session total")} aside={t("{used} tokens", { used: groupedTokens(total.totalTokens) })}>
          <dl className="context-usage-grid">
            <div><dt>{t("Input")}</dt><dd>{groupedTokens(total.inputTokens)}</dd></div>
            <div><dt>{t("Output")}</dt><dd>{groupedTokens(total.outputTokens)}</dd></div>
            <div><dt>{t("Cached input")}</dt><dd>{groupedTokens(total.cachedInputTokens)}</dd></div>
            <div><dt>{t("Reasoning")}</dt><dd>{groupedTokens(total.reasoningTokens)}</dd></div>
          </dl>
        </UsageCard>}
        {status && !fromHistory && snapshot?.status !== "no_observation" && snapshot?.status !== "missing_session"
          && <Callout compact intent="warning" role="status">{typeof status === "string" ? t(status) : status.raw}</Callout>}
      </div>
    </AppWindow>}
  </>;
}
