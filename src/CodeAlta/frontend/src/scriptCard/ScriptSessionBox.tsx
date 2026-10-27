import { useContext } from "react";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { SessionLinksContext } from "../SessionReference";
import { useShellLanguage } from "../shellLanguage";
import type { ScriptSession } from "../timeline";
import { formatDuration } from "../toolCall";
import { scriptMarkState as state } from "./scriptRunOffers";

/** A session a run started: its state, its title, its step, its duration and its tokens. A click opens it. */
export function ScriptSessionBox({ session, showStep = true }: { session: ScriptSession; showStep?: boolean }) {
  const { t } = useShellLanguage();
  const links = useContext(SessionLinksContext);
  const title = links?.title(session.sessionId) ?? session.title ?? session.sessionId.slice(0, 8);
  const tokens = session.inputTokens !== null || session.outputTokens !== null ? (session.inputTokens ?? 0) + (session.outputTokens ?? 0) : null;
  const facts = [showStep ? session.stepId : null, session.durationMs !== null ? formatDuration(session.durationMs) : null,
    tokens !== null ? t("{count} tokens", { count: tokens.toLocaleString() }) : null].filter(Boolean).join(" · ");
  const content = <>
    <span className="script-session-mark">{state(session.status) === "running" ? <ActivitySpinner size={10} /> : <AppIcon name="childSession" size={12} />}</span>
    <span className="script-session-text"><strong>{title}</strong>{facts && <small>{facts}</small>}</span>
  </>;
  const label = t("Open the session {title}", { title });
  return links && links.title(session.sessionId) !== null
    ? <button type="button" className="script-session" data-state={state(session.status)} title={session.message ?? label} aria-label={label}
      onClick={event => { event.stopPropagation(); links.open(session.sessionId); }}>{content}</button>
    : <span className="script-session" data-state={state(session.status)} title={session.message ?? undefined}>{content}</span>;
}
