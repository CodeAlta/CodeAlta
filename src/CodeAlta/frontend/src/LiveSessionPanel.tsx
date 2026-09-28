import { useEffect, useLayoutEffect, useRef, useState, useSyncExternalStore } from "react";
import { createSessionDisplayStore } from "./sessionDisplay";
import type { SessionDisplayText, SessionDisplayToolActivity } from "#neoastra";
import type { createMutationCapability } from "./sessionOperations";
import { MarkdownContent } from "./MarkdownContent";
import { writeMarkdown } from "./timeline";
import { AppIcon } from "./AppIcon";
import { showLiveDisplay } from "./workspacePresentation";
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
  const session = snapshot?.session;
  if (!showLiveDisplay(state)) return null;
  return <section className="live-indicator" aria-label={t("Selected session live status")}>
    <span className={`status-pill live-${state?.kind ?? "loading"}`}>{t("Live")} · {session?.lifecycle?.kind ?? session?.statusKind ?? state?.kind ?? "loading"}</span>
    {state?.code === "stale_epoch" && <p role="alert">{t("The host has changed. Reload the Desktop UI before continuing; reconnecting with this old host identity will not work.")}</p>}
    {state?.kind === "error" && !state.code && <p role="alert">{t("Live observation unavailable. No idle or completion state is inferred.")}</p>}
    {state?.cleanupBlocked && <p role="alert">{t("Previous observation cleanup failed. Its owner is retained; no successor can open here. Reconnect cannot prove cleanup or recover effects.")}</p>}
    {(state?.code || state?.cleanupBlocked) && <button type="button" disabled={!canMutate || state?.code === "stale_epoch" || state?.cleanupBlocked} onClick={() => {
      const owned = scope.current;
      if (!owned || owned.hostEpoch !== hostEpoch || owned.sessionId !== sessionId || !capability.canMutate() || store.getSnapshot().cleanupBlocked) return;
      owned.selection = store.select(hostEpoch, sessionId, capability.observe);
    }}>{t("Reconnect live activity")}</button>}
    {snapshot && <>
      {snapshot.hasGap && <p role="status">{t("Intermediate updates were coalesced. This replacement is the latest retained window, not recovered history.")}</p>}
      {snapshot.isClosed && <p role="status">{t("Runtime display closed. No further updates will arrive on this observation.")}</p>}
      {(snapshot.evictedSessions !== "0" || snapshot.omittedSessionEvents !== "0") && <p className="detail">{t("Global coverage: {evicted} session windows evicted; {omitted} publications with omitted session identity.", { evicted: snapshot.evictedSessions, omitted: snapshot.omittedSessionEvents })}</p>}
      {!session && <p role="status">{t("No retained live state for this session. It may not have published or its window was evicted; this does not mean idle or completed.")}</p>}
      {session && <>
        <details className="live-metadata"><summary>{t("Runtime details")}</summary><dl>
          <dt>{t("Latest published lifecycle")}</dt><dd>{session.lifecycle?.kind ?? t("Not observed")}{session.lifecycle?.runId ? t(" · run {id}", { id: session.lifecycle.runId }) : ""}</dd>
          <dt>{t("Queue count")}</dt><dd>{session.queuedPromptCount ?? t("Not observed")}</dd>
          <dt>{t("Host status")}</dt><dd>{session.statusKind ?? t("Not observed")}{session.statusMessage ? ` · ${session.statusMessage}` : ""}</dd>
          <dt>{t("Provider / configuration key")}</dt><dd>{session.configuration?.providerId ?? t("Not observed")} / {session.configuration?.providerKey ?? t("Not observed")}</dd>
          <dt>{t("Model / reasoning")}</dt><dd>{session.configuration?.modelId ?? t("Not observed")} / {session.configuration?.reasoningEffort ?? t("Not observed")}</dd>
          <dt>{t("Agent prompt")}</dt><dd>{session.configuration?.agentPromptId ?? t("Not observed")}</dd>
        </dl></details>
        {session.lifecycle?.message && <p>{session.lifecycle.message}</p>}
        {(session.metadataTruncated || session.transportTruncated) && <p className="detail">{t("Some status/configuration labels were shortened.")}</p>}
        {(session.evictedTextItems !== "0" || session.unsupportedEvents !== "0") && <p className="detail">{t("Retained-window omissions: {evicted} text items evicted; {unsupported} unsupported publications.", { evicted: session.evictedTextItems, unsupported: session.unsupportedEvents })}</p>}
        <details className="live-metadata"><summary>{t("Live-window coverage")}</summary>
          <p className="detail">{t("Only this selected session is observed. This is not persisted history, a complete transcript, or a tool-results/usage/interaction view. Closing or reconnecting does not stop a run.")}</p>
          <p className="detail">{t("A same-host reload obtains retained partial values only. Restart restores no authority. {count} tool identities were evicted.", { count: session.evictedToolActivities })}</p>
        </details>
      </>}
    </>}
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
