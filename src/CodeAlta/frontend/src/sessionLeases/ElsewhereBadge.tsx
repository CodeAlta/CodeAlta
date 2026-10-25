import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import { elsewhereLabel, type ElsewhereKind } from "./sessionLeases";

/**
 * The mark of a session another process holds (the command line, another instance): in the Explorer and on the tab of
 * the session, which is read-only here meanwhile. It renders nothing for a session no other process holds.
 */
export function ElsewhereBadge({ kind }: { kind: ElsewhereKind | null }) {
  const { t } = useShellLanguage();
  if (!kind) return null;
  const label = t(elsewhereLabel(kind));
  return <span className="elsewhere-badge" data-kind={kind} role="img" aria-label={label} title={label}><AppIcon name={kind === "altacli" ? "terminal" : "remote"} size={12} /></span>;
}
