import { AppIcon } from "./AppIcon";
import { useShellLanguage } from "./shellLanguage";

/**
 * The mark of a session that waits for the user (a question, a command to allow, a form), or of a scope that has such
 * sessions: in the Explorer and on the tab of the session. It renders nothing for zero.
 */
export function WaitingBadge({ count }: { count: number }) {
  const { t } = useShellLanguage();
  if (count <= 0) return null;
  const label = t(count === 1 ? "{count} session waits for you" : "{count} sessions wait for you", { count });
  return <span className="waiting-badge" role="img" aria-label={label} title={label}><AppIcon name="ask" size={12} />{count > 1 && <span aria-hidden="true">{count}</span>}</span>;
}

/** The same mark for one session, which says that it waits. */
export function SessionWaitingBadge({ waiting }: { waiting: boolean }) {
  const { t } = useShellLanguage();
  if (!waiting) return null;
  const label = t("Waits for your answer");
  return <span className="waiting-badge" role="img" aria-label={label} title={label}><AppIcon name="ask" size={12} /></span>;
}
