import { useContext } from "react";
import { Button, PopoverNext } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { SessionLinksContext } from "../SessionReference";
import { useShellLanguage } from "../shellLanguage";
import type { SubSessionAtWork } from "../spaces/spaces";

/**
 * The sub-agents at work under the session, below its prompt: how many, with a dot that says whether one waits for
 * the user, and on a click each of them, which opens it. Nothing is shown while none works.
 */
export function SubAgentsChip({ sessions }: { sessions: readonly SubSessionAtWork[] | undefined }) {
  const { t } = useShellLanguage();
  const links = useContext(SessionLinksContext);
  if (!sessions?.length) return null;
  const waiting = sessions.filter(session => session.state === "waiting").length;
  const label = waiting
    ? t("{count} sub-agent(s) at work, {waiting} waiting for you", { count: sessions.length, waiting })
    : t("{count} sub-agent(s) at work", { count: sessions.length });
  const state = (session: SubSessionAtWork) => t(session.state === "waiting" ? "Waits for your answer" : session.state === "running" ? "At work" : "Background tasks");
  const list = <div className="sub-agents-list" role="group" aria-label={t("Sub-agents at work")}>
    <header><AppIcon name="subAgents" size={15} /><strong>{t("Sub-agents at work")}</strong><span>{sessions.length}</span></header>
    <ul>{sessions.map(session => {
      const title = links?.title(session.sessionId) ?? session.title ?? session.sessionId.slice(0, 8);
      return <li key={session.sessionId} data-state={session.state}>
        <span className="sub-agents-dot" aria-hidden="true" />
        {links ? <button type="button" className="sub-agents-open" title={t("Open the session {title}", { title })} onClick={() => links.open(session.sessionId)}>{title}</button>
          : <span className="sub-agents-title">{title}</span>}
        <small>{state(session)}</small>
      </li>;
    })}</ul>
  </div>;
  return <PopoverNext content={list} placement="top-end" popoverClassName="sub-agents-popover">
    <Button variant="minimal" size="small" className="sub-agents-chip" data-waiting={waiting > 0 || undefined} aria-label={label} title={label}>
      <AppIcon name="subAgents" size={14} /><span className="sub-agents-dot" aria-hidden="true" /><span>{sessions.length}</span>
    </Button>
  </PopoverNext>;
}
