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

/**
 * The same mark for one session, which says that it waits, or, when it does not, that sessions under it (the ones it
 * created, theirs…) wait: its own work waits for theirs.
 */
export function SessionWaitingBadge({ waiting, below = 0 }: { waiting: boolean; below?: number }) {
  const { t } = useShellLanguage();
  if (!waiting && below <= 0) return null;
  const label = waiting ? t("Waits for your answer")
    : t(below === 1 ? "A sub-session waits for you" : "{count} sub-sessions wait for you", { count: below });
  return <span className="waiting-badge" data-below={waiting ? undefined : "true"} role="img" aria-label={label} title={label}><AppIcon name="ask" size={12} /></span>;
}
