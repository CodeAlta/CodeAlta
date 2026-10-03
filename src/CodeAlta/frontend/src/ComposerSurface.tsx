import type { ReactNode } from "react";
import { useShellLanguage } from "./shellLanguage";

// Shared presentation for owned, new-session and catalog composers. Authority,
// request recovery and draft ownership stay with each composer, not this surface.
export function ComposerSurface({ status, busy = false, children, className }: {
  status: ReactNode; busy?: boolean; children: ReactNode; className?: string;
}) {
  const { t } = useShellLanguage();
  return <section className={`owned-session${className ? ` ${className}` : ""}`} aria-label={t("Message composer")}>
    <div className="composer-status-line" role="status" data-busy={busy}><span>{status}</span></div>
    {children}
  </section>;
}

export function ComposerToolbar({ options, children }: { options?: ReactNode; children: ReactNode }) {
  const { t } = useShellLanguage();
  return <div className="composer-toolbar">
    {options && <div className="prompt-options" aria-label={t("Session configuration")}>{options}</div>}
    <div className="history-controls">{children}</div>
  </div>;
}
