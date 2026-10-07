import { createContext, useContext } from "react";
import { AppIcon } from "./AppIcon";
import { useShellLanguage } from "./shellLanguage";

/** What the window knows of the sessions a message can name: their titles, and how one is opened. */
export type SessionLinks = Readonly<{ title(sessionId: string): string | null; open(sessionId: string): void }>;

/** Given by the window. Without it (a fixture, a detached view) a session is named by the start of its id. */
export const SessionLinksContext = createContext<SessionLinks | null>(null);

/**
 * The session a message between agents comes from: its title, which opens it. A session the window does not
 * list (deleted, or of a project that is not loaded) is named by the start of its id and opens nothing.
 */
export function SessionReference({ sessionId }: { sessionId: string }) {
  const { t } = useShellLanguage();
  const links = useContext(SessionLinksContext);
  const title = links?.title(sessionId) ?? null;
  if (!links || title === null) return <small className="session-reference-id" title={sessionId}>{sessionId.slice(0, 8)}</small>;
  const label = t("Open the session {title}", { title });
  return <button type="button" className="session-reference" title={label} aria-label={label} onClick={event => { event.stopPropagation(); links.open(sessionId); }}>
    <AppIcon name="childSession" size={12} /><span>{title}</span></button>;
}

/** The mark of a session that has sub-agents: how many sessions it started. */
export function SubAgentBadge({ count }: { count: number }) {
  const { t } = useShellLanguage();
  if (count <= 0) return null;
  const label = t("{count} sub-agent(s)", { count });
  return <span className="sub-agent-badge" role="img" aria-label={label} title={label}><AppIcon name="branch" size={11} /><span aria-hidden="true">{count}</span></span>;
}
