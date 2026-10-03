import { AppIcon } from "./AppIcon";
import { useShellLanguage } from "./shellLanguage";

/** Explorer marker for a session (or a scope) with active reminders; renders nothing for zero. */
export function ReminderBadge({ count }: { count: number }) {
  const { t } = useShellLanguage();
  if (count <= 0) return null;
  const label = t("{count} active reminder(s)", { count });
  return <span className="reminder-badge" role="img" aria-label={label} title={label}><AppIcon name="reminder" size={12} />{count > 1 && <span aria-hidden="true">{count}</span>}</span>;
}
