import { useEffect, useLayoutEffect, useRef, useState, useSyncExternalStore } from "react";
import { createSessionDisplayStore } from "./sessionDisplay";
import type { SessionDisplayText, SessionDisplayToolActivity } from "#neoastra";
import type { createMutationCapability } from "./sessionOperations";
import { MarkdownContent } from "./MarkdownContent";
import { writeMarkdown } from "./timeline";
import { AppIcon } from "./AppIcon";
import { useShellLanguage } from "./shellLanguage";

export function LiveSessionPanel({ store, hostEpoch, sessionId, capability }: {
  store: ReturnType<typeof createSessionDisplayStore>; hostEpoch: string; sessionId: string;
  capability: ReturnType<typeof createMutationCapability>;
}) {
  const { t } = useShellLanguage();
  const observed = useSyncExternalStore(store.subscribe, store.getSnapshot);
  const canMutate = useSyncExternalStore(capability.subscribe, capability.canMutate);
  const scope = useRef<{ hostEpoch: string; sessionId: string; selection: ReturnType<typeof store.select> } | null>(null);
  useEffect(() => {
    const owned = { hostEpoch, sessionId, selection: store.select(hostEpoch, sessionId, capability.observe) };
    scope.current = owned;
    return () => { owned.selection.detach(); if (scope.current === owned) scope.current = null; };
  }, [store, hostEpoch, sessionId, capability]);
  // Never flash the previous selection during the render preceding effect cleanup/admission.
  const state = observed.hostEpoch === hostEpoch && observed.sessionId === sessionId ? observed : null;
  const snapshot = state?.snapshot;
  // Keep the observation owner mounted, but don't put runtime diagnostics into
  // the conversation. The composer presents current run activity instead.
  if (!state || (state.kind !== "error" && !state.code && !state.cleanupBlocked
    && state.kind !== "closed" && !snapshot?.isClosed)) return null;
  return <section className="live-indicator" aria-label={t("Selected session live status")}>
    {state?.code === "stale_epoch" && <p role="alert">{t("The host has changed. Reload the Desktop UI before continuing; reconnecting with this old host identity will not work.")}</p>}
    {state.kind === "error" && state.code !== "stale_epoch" && !state.cleanupBlocked && <p role="alert">{t("Live observation unavailable. No idle or completion state is inferred.")}</p>}
    {state?.cleanupBlocked && <p role="alert">{t("Previous observation cleanup failed. Its owner is retained; no successor can open here. Reconnect cannot prove cleanup or recover effects.")}</p>}
    {(state.kind === "error" || state.kind === "closed" || snapshot?.isClosed || state.code || state.cleanupBlocked) && <button type="button" disabled={!canMutate || state.code === "stale_epoch" || state.cleanupBlocked} onClick={() => {
      const owned = scope.current;
      if (!owned || owned.hostEpoch !== hostEpoch || owned.sessionId !== sessionId || !capability.canMutate() || store.getSnapshot().cleanupBlocked) return;
      owned.selection = store.select(hostEpoch, sessionId, capability.observe);
    }}>{t("Reconnect live activity")}</button>}
    {(state.kind === "closed" || snapshot?.isClosed) && <p role="status">{t("Runtime display closed. No further updates will arrive on this observation.")}</p>}
  </section>;
}

export function LiveToolMessage({ row }: { row: SessionDisplayToolActivity }) {
  const { t } = useShellLanguage();
  return <article className="message timeline-message message-tool" data-tool-phase={row.phase.toLowerCase()}><div className="avatar"><AppIcon name="tool" size={17} /></div>
    <div className="message-body"><strong className="tool-outcome">{row.name ?? t("Unnamed tool")}</strong><small> · {t("Reported {phase}", { phase: row.phase })}</small>
      {row.isNameTruncated && <p className="detail">{t("Name prefix truncated.")}</p>}
      <details><summary>{t("Live tool identity")}</summary><p>{t("Provider {provider} · run {run} · activity {activity}", { provider: row.providerId, run: row.runId ?? t("not supplied"), activity: row.activityId })}</p></details>
    </div></article>;
}

export function LiveTextMessage({ row }: { row: SessionDisplayText }) {
  const { t } = useShellLanguage();
  const [copyState, setCopyState] = useState<"idle" | "copied" | "failed">("idle");
  const reset = useRef<number | undefined>(undefined);
  const active = useRef(false);
  const copySequence = useRef(0);
  useLayoutEffect(() => {
    active.current = true;
    setCopyState("idle");
    return () => {
      active.current = false;
      copySequence.current++;
      if (reset.current !== undefined) window.clearTimeout(reset.current);
      reset.current = undefined;
    };
  }, [row.runId, row.contentId, row.kind, row.text, row.isComplete, row.isTruncated, row.startedWithDelta]);
  async function copy() {
    const text = row.text;
    if (!active.current) return;
    const sequence = ++copySequence.current;
    if (reset.current !== undefined) window.clearTimeout(reset.current);
    reset.current = undefined;
    setCopyState("idle");
    const state = await writeMarkdown(value => navigator.clipboard.writeText(value), text);
    if (!active.current || sequence !== copySequence.current) return;
    setCopyState(state);
    reset.current = window.setTimeout(() => {
      if (!active.current || sequence !== copySequence.current) return;
      reset.current = undefined;
      setCopyState("idle");
    }, 1600);
  }
  const copyLabel = copyState === "copied" ? t("Copied") : copyState === "failed" ? t("Copy failed") : t("Copy {title} as Markdown", { title: row.kind });
  return <article className={`message timeline-message message-${row.kind.toLowerCase() === "user" ? "user" : row.kind.toLowerCase().startsWith("reasoning") ? "reasoning" : "assistant"}`}>
    <div className="avatar"><AppIcon name={row.kind.toLowerCase() === "user" ? "user" : row.kind.toLowerCase().startsWith("reasoning") ? "brain" : "assistant"} size={17} /></div><div className="message-body">
      <div className="message-heading"><span>{!row.kind.toLowerCase().startsWith("reasoning") && <strong>{row.kind.toLowerCase() === "assistant" ? t("Assistant") : row.kind}</strong>}<small>{t(row.isComplete ? "Complete" : "Streaming")}</small></span><span className="message-actions">
        <button type="button" className={`copy-markdown copy-${copyState}`} aria-label={copyLabel} title={copyLabel} onClick={() => void copy()}><AppIcon name={copyState === "copied" ? "checked" : copyState === "failed" ? "error" : "copy"} size={15} /><span className="sr-only" aria-live="polite">{copyState === "idle" ? "" : copyLabel}</span></button>
      </span></div>
      <MarkdownContent source={row.text} />
      {row.isTruncated && <p className="detail">{t("Text prefix truncated.")}</p>}
      {row.startedWithDelta && <p className="detail">{t("Earlier text may be missing.")}</p>}
    </div>
  </article>;
}
